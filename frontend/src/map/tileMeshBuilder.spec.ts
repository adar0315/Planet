import { describe, expect, it } from 'vitest'
import * as THREE from 'three'
import { computeTilePolygon, sharedCornerDirection, type TileMeshOptions } from './tileMeshBuilder'
import { latLonToVector3, tangentBasisAt } from './sphereCoordinates'
import type { TileDto, TileShape } from '@/types/tile'

/**
 * 這裡最重要的一條是「密合性」：三個互相相鄰的格子，各自算出來的那個共用角落必須是**完全同一點**。
 * 這就是「無縫」的定義本身——比截圖目視可靠得多，而且是這次改動唯一真正要保證的性質。
 */

const OPTIONS: TileMeshOptions = { renderRadius: 50, planetRadiusMeters: 1_851_000, edgeLengthMeters: 1000 }

function tile(id: number, lat: number, lon: number, elevationLevel = 0, shape: TileShape = 'Hexagon'): TileDto {
  return {
    tileId: id,
    shape,
    faceIndex: 0,
    latitude: lat,
    longitude: lon,
    terrain: 'Grassland',
    heightCategory: 0,
    elevationLevel,
    seaLevel: 0,
  }
}

/** 造一個中心格＋六個大致等距環繞的鄰居（用小角距模擬真實的相鄰關係）。 */
function hexNeighbourhood(centreLat: number, centreLon: number, spacingDeg = 0.05) {
  const centre = tile(0, centreLat, centreLon)
  const cosLat = Math.cos((centreLat * Math.PI) / 180)
  const neighbours = Array.from({ length: 6 }, (_, i) => {
    const angle = (i / 6) * Math.PI * 2
    return tile(
      i + 1,
      centreLat + spacingDeg * Math.sin(angle),
      centreLon + (spacingDeg * Math.cos(angle)) / cosLat,
    )
  })
  return { centre, neighbours }
}

/**
 * 造一個**真正的**六角晶格（axial座標），讓相鄰關係是真的而不是湊出來的。
 * 密合性測試需要「A、B、C 三格互為鄰居，且 B、C 在繞著 A 的角度順序上相鄰」，
 * 隨手擺六個點是做不到這件事的（排序後 B、C 可能根本不相鄰，就不會形成共用角落）。
 */
function hexLattice(baseLat: number, baseLon: number, spacingDeg = 0.05) {
  const cosLat = Math.cos((baseLat * Math.PI) / 180)
  const AXIAL_DIRS: [number, number][] = [
    [1, 0],
    [1, -1],
    [0, -1],
    [-1, 0],
    [-1, 1],
    [0, 1],
  ]
  const key = (q: number, r: number) => `${q},${r}`
  const byKey = new Map<string, TileDto>()
  let nextId = 1

  function at(q: number, r: number): TileDto {
    const k = key(q, r)
    const existing = byKey.get(k)
    if (existing) return existing

    // axial → 平面座標（pointy-top 佈局），再換算成經緯度
    const x = spacingDeg * (1.5 * q)
    const y = spacingDeg * (Math.sqrt(3) * (r + q / 2))
    // 用 tileId 編碼座標，方便除錯時看得出是哪一格
    const created = tile(nextId++, baseLat + y, baseLon + x / cosLat, 0)
    byKey.set(k, created)
    return created
  }

  const neighboursOf = (q: number, r: number) => AXIAL_DIRS.map(([dq, dr]) => at(q + dq, r + dr))

  return { at, neighboursOf, AXIAL_DIRS }
}

describe('sharedCornerDirection', () => {
  it('與三個輸入的順序無關——這是密合的前提', () => {
    const a = latLonToVector3(10, 20, 1)
    const b = latLonToVector3(10.05, 20.05, 1)
    const c = latLonToVector3(10.05, 19.95, 1)

    const abc = sharedCornerDirection(a, b, c, new THREE.Vector3())
    const bca = sharedCornerDirection(b, c, a, new THREE.Vector3())
    const cab = sharedCornerDirection(c, a, b, new THREE.Vector3())

    expect(bca.x).toBe(abc.x)
    expect(bca.y).toBe(abc.y)
    expect(bca.z).toBe(abc.z)
    expect(cab.x).toBe(abc.x)
    expect(cab.y).toBe(abc.y)
    expect(cab.z).toBe(abc.z)
  })

  it('回傳單位向量', () => {
    const d = sharedCornerDirection(
      latLonToVector3(0, 0, 1),
      latLonToVector3(0.05, 0.05, 1),
      latLonToVector3(0.05, -0.05, 1),
      new THREE.Vector3(),
    )
    expect(d.length()).toBeCloseTo(1, 10)
  })
})

describe('computeTilePolygon — 共用角落', () => {
  it('有完整鄰居時產生對應數量的角落與邊鄰居', () => {
    const { centre, neighbours } = hexNeighbourhood(10, 20)
    const poly = computeTilePolygon(centre, OPTIONS, neighbours)

    expect(poly.sideCount).toBe(6)
    expect(poly.ring).toHaveLength(6)
    expect(poly.edgeNeighbours).toHaveLength(6)
    expect(poly.edgeNeighbours.every((n) => n !== null)).toBe(true)
  })

  it('角落依角度順序排列，形成凸多邊形（不會連成星形）', () => {
    const { centre, neighbours } = hexNeighbourhood(10, 20)
    // 故意把鄰居順序打亂——後端的 neighborIds 順序本來就不保證是角度順序
    const shuffled = [neighbours[3]!, neighbours[0]!, neighbours[5]!, neighbours[1]!, neighbours[4]!, neighbours[2]!]
    const poly = computeTilePolygon(centre, OPTIONS, shuffled)

    // 必須投影到**這一格的切平面**（east/north）再取角度——直接用世界座標的 x/y 是錯的，
    // 格子在球面上，世界座標軸跟它的切平面沒有關係。
    const basis = tangentBasisAt(poly.center)
    const angles = poly.ring.map((p) => {
      const v = p.clone().sub(poly.center)
      return Math.atan2(v.dot(basis.north), v.dot(basis.east))
    })
    let wraps = 0
    for (let i = 0; i < angles.length; i++) {
      if (angles[i]! > angles[(i + 1) % angles.length]!) wraps++
    }
    expect(wraps).toBeLessThanOrEqual(1)
  })

  it('鄰居順序被打亂也算出同一組角落（排序有生效）', () => {
    const { centre, neighbours } = hexNeighbourhood(-25, 130)
    const a = computeTilePolygon(centre, OPTIONS, neighbours)
    const b = computeTilePolygon(centre, OPTIONS, [...neighbours].reverse())

    const key = (poly: typeof a) =>
      poly.ring
        .map((p) => `${p.x.toFixed(9)},${p.y.toFixed(9)},${p.z.toFixed(9)}`)
        .sort()
        .join('|')
    expect(key(b)).toBe(key(a))
  })

  it('五角形產生5個角落', () => {
    const centre = tile(0, 89.9, 0, 0, 'Pentagon')
    const neighbours = Array.from({ length: 5 }, (_, i) => {
      const angle = (i / 5) * Math.PI * 2
      return tile(i + 1, 89.9 - 0.05 * Math.cos(angle), 0.05 * Math.sin(angle) * 10)
    })
    const poly = computeTilePolygon(centre, OPTIONS, neighbours)
    expect(poly.sideCount).toBe(5)
    expect(poly.ring).toHaveLength(5)
  })

  it('海拔只影響半徑，不影響角落方向——否則相鄰高低不同的格子就對不上了', () => {
    const { centre, neighbours } = hexNeighbourhood(5, 5)
    const low = computeTilePolygon(tile(0, 5, 5, 0), OPTIONS, neighbours)
    const high = computeTilePolygon(tile(0, 5, 5, 200), OPTIONS, neighbours)

    for (let i = 0; i < 6; i++) {
      const dLow = low.ring[i]!.clone().normalize()
      const dHigh = high.ring[i]!.clone().normalize()
      expect(dHigh.x).toBeCloseTo(dLow.x, 12)
      expect(dHigh.y).toBeCloseTo(dLow.y, 12)
      expect(dHigh.z).toBeCloseTo(dLow.z, 12)
    }
    expect(high.ring[0]!.length()).toBeGreaterThan(low.ring[0]!.length())
  })
})

describe('computeTilePolygon — 密合性（本次改動的核心保證）', () => {
  /**
   * 三格互相相鄰時，它們共用的那個角落必須是同一個「方向」。
   * 半徑會因各自海拔而不同（那正是要補側面牆的原因），但方向必須完全一致。
   */
  it('三個互相相鄰的格子算出同一個共用角落方向（海拔不同也一樣）', () => {
    const lattice = hexLattice(10, 20)

    // A 在原點；B、C 取 axial 方向上「相鄰的兩個方向」，所以 A、B、C 三格互為鄰居
    const A = lattice.at(0, 0)
    const B = lattice.at(1, 0) // 方向 [1,0]
    const C = lattice.at(1, -1) // 方向 [1,-1]，與 [1,0] 在環上相鄰

    // 海拔刻意設成不同，驗證海拔不影響角落「方向」
    const elevated = (t: TileDto, level: number): TileDto => ({ ...t, elevationLevel: level })

    const polyA = computeTilePolygon(elevated(A, 3), OPTIONS, lattice.neighboursOf(0, 0))
    const polyB = computeTilePolygon(elevated(B, 40), OPTIONS, lattice.neighboursOf(1, 0))
    const polyC = computeTilePolygon(elevated(C, -12), OPTIONS, lattice.neighboursOf(1, -1))

    const expected = sharedCornerDirection(
      latLonToVector3(A.latitude, A.longitude, 1),
      latLonToVector3(B.latitude, B.longitude, 1),
      latLonToVector3(C.latitude, C.longitude, 1),
      new THREE.Vector3(),
    )

    for (const [name, poly] of [
      ['A', polyA],
      ['B', polyB],
      ['C', polyC],
    ] as const) {
      const distances = poly.ring.map((corner) => corner.clone().normalize().distanceTo(expected))
      const closest = Math.min(...distances)
      // 三格都必須含有「方向完全相同」的那個角落——這就是無縫的定義
      expect(closest, `${name} 應該含有共用角落`).toBeLessThan(1e-12)
    }
  })

  it('相鄰兩格的共用邊：兩個角落方向都一致（整條邊貼合，不只是一個點）', () => {
    const lattice = hexLattice(-30, 150)
    const A = lattice.at(0, 0)
    const B = lattice.at(1, 0)

    const polyA = computeTilePolygon(A, OPTIONS, lattice.neighboursOf(0, 0))
    const polyB = computeTilePolygon(B, OPTIONS, lattice.neighboursOf(1, 0))

    const dirsA = polyA.ring.map((p) => p.clone().normalize())
    const dirsB = polyB.ring.map((p) => p.clone().normalize())

    // A 的角落中，有幾個能在 B 找到方向完全相同的？共用一條邊 → 應該剛好2個
    const shared = dirsA.filter((a) => dirsB.some((b) => a.distanceTo(b) < 1e-12))
    expect(shared).toHaveLength(2)
  })
})

describe('computeTilePolygon — 鄰居不齊時的退路', () => {
  it('沒有鄰居時仍畫得出多邊形（patch最外圈用）', () => {
    const poly = computeTilePolygon(tile(0, 10, 20), OPTIONS)
    expect(poly.ring).toHaveLength(6)
    expect(poly.edgeNeighbours.every((n) => n === null)).toBe(true)
  })

  it('鄰居數量不足時不會誤用共用角落（會連出錯誤形狀）', () => {
    const { centre, neighbours } = hexNeighbourhood(10, 20)
    const poly = computeTilePolygon(centre, OPTIONS, neighbours.slice(0, 4))
    expect(poly.ring).toHaveLength(6)
    expect(poly.edgeNeighbours.every((n) => n === null)).toBe(true)
  })
})
