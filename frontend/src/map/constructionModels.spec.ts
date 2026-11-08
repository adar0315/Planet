import { describe, expect, it } from 'vitest'
import * as THREE from 'three'
import type { SubTileDto } from '@/types/tile'
import {
  HOUSE_MAX_RADIUS_METERS,
  HOUSE_ROOF_REDS,
  HOUSE_WALL_COLOR,
  houseDesign,
  houseCluster,
  castleGroups,
  BRIDGE_CLEARANCE_METERS,
  bridgeDeckRadius,
  buildConstructionLayer,
  connectedNeighbours,
} from './constructionModels'
import { tileFloorRadius, tileSurfaceRadius, type TileMeshOptions } from './tileMeshBuilder'

const options: TileMeshOptions = { renderRadius: 50, planetRadiusMeters: 1_852_000, edgeLengthMeters: 250 }

function sub(id: number, lon: number, extra: Partial<SubTileDto> = {}): SubTileDto {
  return {
    tileId: id,
    parentTileId: 1,
    shape: 'Hexagon',
    faceIndex: 0,
    latitude: 10,
    longitude: lon,
    terrain: 'Grassland',
    heightCategory: 0,
    elevationLevel: 20,
    seaLevel: 0,
    ...extra,
  }
}

/** 相鄰小格的格心約差 433 公尺（經度約 0.0136°，在北緯 10° 再除以 cos）。 */
const STEP = (433 / 1_852_000) * (180 / Math.PI) / Math.cos((10 * Math.PI) / 180)

describe('constructionModels', () => {
  it('roads connect to roads, bridges and buildings; bridges only to roads and bridges', () => {
    const road = sub(1, 0, { structure: 'Road' })
    const house = sub(2, STEP, { structure: 'House' })
    const bridge = sub(3, -STEP, { structure: 'Bridge' })
    const empty = sub(4, 2 * STEP)
    expect(connectedNeighbours(road, [house, bridge, empty]).map((s) => s.tileId)).toEqual([2, 3])
    expect(connectedNeighbours(bridge, [road, house]).map((s) => s.tileId)).toEqual([1])
  })

  it('bridge deck clears the water and is not below a connected bank road', () => {
    const water = sub(1, 0, { structure: 'Bridge', terrain: 'Freshwater', elevationLevel: 10, seaLevel: 14 })
    const clearance = (BRIDGE_CLEARANCE_METERS / options.planetRadiusMeters) * options.renderRadius
    expect(bridgeDeckRadius(water, [], options)).toBeCloseTo(tileFloorRadius(water, options) + clearance, 10)

    const highBank = sub(2, STEP, { structure: 'Road', elevationLevel: 200 })
    expect(bridgeDeckRadius(water, [highBank], options)).toBeGreaterThan(tileSurfaceRadius(highBank, options))
  })

  it('builds one mesh per material, sitting on the tile', () => {
    const subs = [sub(1, 0, { structure: 'Castle' }), sub(2, STEP, { structure: 'House' }), sub(3, 2 * STEP)]
    const layer = buildConstructionLayer(subs, () => [], options)
    // 城堡：石牆、屋頂；房子依造型至少牆、屋頂兩種顏色
    expect(layer.group.children.length).toBeGreaterThanOrEqual(4)
    const surface = tileSurfaceRadius(subs[0]!, options)
    for (const child of layer.group.children) {
      const geometry = (child as THREE.Mesh).geometry
      geometry.computeBoundingBox()
      const position = geometry.getAttribute('position')
      let minRadius = Infinity
      let maxRadius = 0
      const v = new THREE.Vector3()
      for (let i = 0; i < position.count; i++) {
        const r = v.fromBufferAttribute(position, i).length()
        minRadius = Math.min(minRadius, r)
        maxRadius = Math.max(maxRadius, r)
      }
      // 底部貼著地面（誤差是切平面離開球面的那一點點），最高不超過 300 公尺
      expect(minRadius).toBeGreaterThan(surface - 1e-5)
      expect(maxRadius).toBeLessThan(surface + (300 / options.planetRadiusMeters) * options.renderRadius)
    }
    layer.dispose()
    expect(layer.group.children).toHaveLength(0)
  })

  it('faces wind outward (the local frame is not mirrored)', () => {
    // 鏡射的變換會讓三角形的繞向反過來，但法向量照樣朝外：畫出來是內側（背面被剔除）
    const subs = [
      sub(1, 0, { structure: 'House' }),
      sub(2, STEP, { structure: 'Road' }),
      sub(3, 2 * STEP, { structure: 'Castle' }),
      sub(4, 3 * STEP, { structure: 'Castle' }),
      sub(5, 4 * STEP, { structure: 'Farm' }),
      sub(6, 5 * STEP, { structure: 'Orchard' }),
      sub(7, 6 * STEP, { structure: 'Pasture' }),
    ]
    const layer = buildConstructionLayer(subs, (s) => subs.filter((o) => Math.abs(o.tileId - s.tileId) === 1), options, () => ({
      season: '秋',
      snow: false,
    }))
    const a = new THREE.Vector3()
    const b = new THREE.Vector3()
    const c = new THREE.Vector3()
    const n = new THREE.Vector3()
    for (const child of layer.group.children) {
      const geometry = (child as THREE.Mesh).geometry
      const position = geometry.getAttribute('position')
      const normal = geometry.getAttribute('normal')
      const index = geometry.getIndex()
      const count = index ? index.count : position.count
      for (let t = 0; t < count; t += 3) {
        const [i, j, k] = index ? [index.getX(t), index.getX(t + 1), index.getX(t + 2)] : [t, t + 1, t + 2]
        a.fromBufferAttribute(position, i)
        b.fromBufferAttribute(position, j)
        c.fromBufferAttribute(position, k)
        const face = b.sub(a).cross(c.sub(a))
        if (face.lengthSq() === 0) continue
        n.fromBufferAttribute(normal, i)
        expect(face.dot(n)).toBeGreaterThan(0)
      }
    }
  })

  it('adjacent castle cells form one castle, keep in the best-connected cell', () => {
    const line = [1, 2, 3, 4].map((id) => sub(id, id * STEP, { structure: 'Castle' }))
    const apart = sub(9, 20 * STEP, { structure: 'Castle' })
    const all = [...line, apart, sub(5, 5 * STEP)]
    const neighbours = (s: SubTileDto) => all.filter((o) => Math.abs(o.tileId - s.tileId) === 1)
    const groups = castleGroups(all, neighbours)
    expect(groups.map((g) => g.map((s) => s.tileId).sort())).toEqual([[1, 2, 3, 4], [9]])
    expect(groups[0]![0]!.tileId).toBe(2) // 2 與 3 都有兩個城堡鄰居，取編號小的
  })

  it('farm, orchard and pasture change with the season', () => {
    const subs = [sub(1, 0, { structure: 'Farm' }), sub(2, 3 * STEP, { structure: 'Orchard' })]
    const colours = (season: '春' | '夏' | '秋' | '冬', snow = false) => {
      const layer = buildConstructionLayer(subs, () => [], options, () => ({ season, snow }))
      const result = layer.group.children
        .map((c) => ((c as THREE.Mesh).material as THREE.MeshLambertMaterial).color.getHex())
        .sort()
      layer.dispose()
      return result
    }
    expect(colours('春')).not.toEqual(colours('秋'))
    // 冬天果樹落葉：少了樹冠那一種顏色；秋天多了果子
    expect(colours('冬').length).toBeLessThan(colours('夏').length)
    expect(colours('秋').length).toBeGreaterThan(colours('夏').length)
    expect(colours('冬', true)).toContain(0xeef2f5)
  })

  it('houses stay inside the cell, face outward, and only use cream walls and red roofs', () => {
    const v = new THREE.Vector3()
    const a = new THREE.Vector3()
    const b = new THREE.Vector3()
    const c = new THREE.Vector3()
    const base = 1_000_237_225_700
    let maxReach = 0
    let minY = Infinity
    let maxY = -Infinity
    const inward: string[] = []
    for (let id = base; id < base + 200; id++) {
      const design = houseDesign(id)
      for (const part of design.parts((meters: number) => meters)) {
        expect([HOUSE_WALL_COLOR, ...HOUSE_ROOF_REDS]).toContain(part.color)
        const geometry = part.geometry.clone().translate(part.x ?? 0, part.y, -(part.z ?? 0))
        const flat = geometry.index ? geometry.toNonIndexed() : geometry
        const position = flat.getAttribute('position')
        const normal = flat.getAttribute('normal')
        for (let i = 0; i < position.count; i++) {
          v.fromBufferAttribute(position, i)
          maxReach = Math.max(maxReach, Math.hypot(v.x, v.z))
          minY = Math.min(minY, v.y)
          maxY = Math.max(maxY, v.y)
        }
        for (let t = 0; t < position.count; t += 3) {
          a.fromBufferAttribute(position, t)
          b.fromBufferAttribute(position, t + 1)
          c.fromBufferAttribute(position, t + 2)
          const face = b.sub(a).cross(c.sub(a))
          if (face.lengthSq() < 1e-12) continue
          v.fromBufferAttribute(normal, t)
          if (face.dot(v) <= 0) inward.push(`${design.shape} ${id}`)
        }
      }
    }
    expect(maxReach).toBeLessThanOrEqual(HOUSE_MAX_RADIUS_METERS + 1e-6)
    expect(minY).toBeGreaterThan(-1e-6)
    expect(maxY).toBeLessThan(110) // 2026-11-08 高度降一半：兩層樓加塔也不到 110 公尺
    expect(inward).toEqual([])
  })

  it('a house cell holds 3 to 5 houses that do not overlap and stay inside the cell', () => {
    const base = 1_000_237_225_700
    const counts = new Set<number>()
    for (let id = base; id < base + 150; id++) {
      const houses = houseCluster(id)
      counts.add(houses.length)
      const reach = houses.map((h) => {
        let r = 0
        for (const part of h.design.parts((meters: number) => meters)) {
          const position = part.geometry.getAttribute('position')
          for (let i = 0; i < position.count; i++) {
            const x = position.getX(i) + (part.x ?? 0)
            const z = -position.getZ(i) + (part.z ?? 0)
            r = Math.max(r, Math.hypot(x, z))
          }
        }
        return r
      })
      houses.forEach((h, i) => {
        // 整棟（含轉向）落在小格裡
        expect(Math.hypot(h.x, h.z) + reach[i]!).toBeLessThanOrEqual(HOUSE_MAX_RADIUS_METERS + 1e-6)
        for (let j = i + 1; j < houses.length; j++) {
          const gap = Math.hypot(h.x - houses[j]!.x, h.z - houses[j]!.z)
          expect(gap).toBeGreaterThanOrEqual(reach[i]! + reach[j]! - 1e-6)
        }
      })
    }
    expect(counts).toEqual(new Set([3, 4, 5]))
  })

  it('houses vary in shape, floors, tower and roof shade, but a cell always gets the same house', () => {
    const base = 1_000_237_225_700
    const designs = Array.from({ length: 300 }, (_, i) => houseDesign(base + i))
    expect(new Set(designs.map((d) => d.shape))).toEqual(new Set(['I', 'L', 'T', 'H', 'O', '多間']))
    expect(new Set(designs.map((d) => d.floors))).toEqual(new Set([1, 2]))
    expect(new Set(designs.map((d) => d.tower))).toEqual(new Set([true, false]))
    expect(new Set(designs.map((d) => d.roofColor)).size).toBe(HOUSE_ROOF_REDS.length)
    // 相鄰的格子大多長得不一樣
    const sameAsNext = designs.filter((d, i) => i > 0 && d.shape === designs[i - 1]!.shape).length
    expect(sameAsNext).toBeLessThan(100)
    const again = houseDesign(base + 7)
    expect(again.shape).toBe(designs[7]!.shape)
    expect(again.turn).toBe(designs[7]!.turn)
  })

  it('a lone sub without structures builds nothing', () => {
    expect(buildConstructionLayer([sub(1, 0)], () => [], options).group.children).toHaveLength(0)
  })
})
