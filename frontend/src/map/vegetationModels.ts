import * as THREE from 'three'
import type { SubTileDto } from '@/types/tile'
import { latLonToVector3, tangentBasisAt } from './sphereCoordinates'
import { tileSurfaceRadius, type TileMeshOptions } from './tileMeshBuilder'
import { hashId, type Season } from './constructionModels'

/**
 * 林地上的樹（2026-11-17）：近看的小格是林地（Forest）就種幾棵樹，高度與疏密照 heightCategory，
 * 樹種照當地的年均溫，樹冠顏色跟著季節變。
 *
 * - `heightCategory` ＝ 高度（0~3：人高／一層樓／二層樓／三層樓）× 2 ＋ 疏密（0 疏、1 密），見後端 TerrainType.Forest。
 * - 一格 250 公尺的小格不可能真的種幾千棵樹：疏林 4 棵、密林 9 棵，代表「這裡是哪一種林子」。
 *   尺寸跟建築物一樣放大（房子半徑到 190 公尺），不然鏡頭最近也在 4.5 公里外，真實大小的樹只是一個點
 *   （第一版 30~85 公尺，實際看是小格上幾個暗點，2026-11-17）。
 * - 位置由小格編號決定（每次重建同一格的樹都在同一個地方）。
 */

/** 樹的種類：寒帶針葉、溫帶落葉闊葉、熱帶常綠闊葉。 */
export type TreeKind = 'conifer' | 'deciduous' | 'evergreen'

/** 一棵樹（在小格切平面上，往東 x、往北 z 公尺）。 */
export interface TreeSpec {
  x: number
  z: number
  /** 樹高（公尺，放大過的）。 */
  height: number
  kind: TreeKind
  /** 0~1：同一種樹的顏色深淺差異。 */
  shade: number
}

/** 高度分級 0~3 的樹高（公尺，放大過）。 */
export const TREE_HEIGHTS_METERS = [80, 110, 150, 190] as const
/** 疏林、密林每一格幾棵。 */
export const TREES_PER_SUBTILE = { sparse: 4, dense: 9 } as const
/** 樹種在小格中心多遠以內（公尺）：小格內切圓約 216 公尺，留一點邊，樹冠才不會伸進隔壁格。 */
const PLANT_RADIUS_METERS = 170
/** 年均溫低於這個（°C）全是針葉林；到 {@link MIXED_FOREST_TOP_C} 之間針葉的比例線性減少。 */
const CONIFER_ONLY_BELOW_C = 3
const MIXED_FOREST_TOP_C = 10
/** 年均溫高於這個（°C）是熱帶常綠林，冬天不落葉。 */
const TROPICAL_ABOVE_C = 20

/** 由編號決定的亂數（mulberry32）：同一格每次重建都一樣。 */
function seeded(seed: number): () => number {
  let a = seed >>> 0
  return () => {
    a = (a + 0x6d2b79f5) >>> 0
    let t = a
    t = Math.imul(t ^ (t >>> 15), t | 1)
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61)
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

/**
 * 這種氣候下，一棵樹（用 0~1 的亂數決定）是什麼樹。
 * @param annualMeanC 年均溫；沒有四季資料（程序化的星球）時傳 undefined，當成溫帶落葉林。
 */
export function treeKindAt(annualMeanC: number | undefined, roll: number): TreeKind {
  if (annualMeanC === undefined) return 'deciduous'
  if (annualMeanC >= TROPICAL_ABOVE_C) return 'evergreen'
  const coniferShare = THREE.MathUtils.clamp(
    (MIXED_FOREST_TOP_C - annualMeanC) / (MIXED_FOREST_TOP_C - CONIFER_ONLY_BELOW_C),
    0,
    1,
  )
  return roll < coniferShare ? 'conifer' : 'deciduous'
}

/** 一個林地小格上的樹；不是林地就是空的。 */
export function forestTrees(sub: SubTileDto, annualMeanC: number | undefined): TreeSpec[] {
  if (sub.terrain !== 'Forest') return []
  const category = THREE.MathUtils.clamp(sub.heightCategory, 0, 7)
  const heightClass = category >> 1
  const dense = (category & 1) === 1
  const count = dense ? TREES_PER_SUBTILE.dense : TREES_PER_SUBTILE.sparse
  const random = seeded(hashId(sub.tileId))
  const trees: TreeSpec[] = []
  for (let i = 0; i < count; i++) {
    // 圓盤上均勻分布：半徑取平方根
    const r = Math.sqrt(random()) * PLANT_RADIUS_METERS
    const angle = random() * Math.PI * 2
    trees.push({
      x: r * Math.cos(angle),
      z: r * Math.sin(angle),
      height: TREE_HEIGHTS_METERS[heightClass]! * (0.8 + 0.4 * random()),
      kind: treeKindAt(annualMeanC, random()),
      shade: random(),
    })
  }
  return trees
}

/** 樹冠顏色；落葉樹的冬天沒有樹冠（回傳 null）。 */
export function crownColor(kind: TreeKind, season: Season, snow: boolean, shade: number): number | null {
  const base = (() => {
    switch (kind) {
      case 'conifer':
        return snow ? 0xc9d6cf : 0x2e5a3a
      case 'evergreen':
        return 0x2f6b2a
      case 'deciduous':
        switch (season) {
          case '春':
            return 0x8cc456
          case '夏':
            return 0x3f7a2a
          case '秋':
            // 秋天有紅、有橘、有黃
            return shade < 0.33 ? 0xb5482a : shade < 0.66 ? 0xd08a2e : 0xd9b23a
          case '冬':
            return null
        }
    }
  })()
  if (base === null) return null
  // 同一種樹深淺差一點，整片林子才不會是一個顏色
  return new THREE.Color(base).multiplyScalar(0.85 + 0.25 * shade).getHex()
}

/** 一格小格當下的季節、積雪、年均溫（給樹種與樹冠顏色用）。 */
export interface ForestClimate {
  season: Season
  snow: boolean
  annualMeanC: number | undefined
}

export interface VegetationLayer {
  readonly group: THREE.Group
  /** 種了幾棵樹（診斷用）。 */
  readonly treeCount: number
  dispose(): void
}

const TRUNK_COLOR = 0x6b4a2e

/**
 * 建出一批小格上的樹：樹幹、闊葉樹冠、針葉樹冠各一個 InstancedMesh（幾萬棵樹也只有三次繪製）。
 * @param subs 要種樹的小格（呼叫端先挑好：林地、沒有建築物、在裁切圓內）。
 * @param options 小格的尺寸換算（跟小格 mesh 同一份）。
 * @param climateOf 每一格的季節、積雪、年均溫。
 */
export function buildVegetationLayer(
  subs: readonly SubTileDto[],
  options: TileMeshOptions,
  climateOf: (sub: SubTileDto) => ForestClimate,
): VegetationLayer {
  const metersToScene = options.renderRadius / options.planetRadiusMeters
  const trunks: THREE.Matrix4[] = []
  const broadleaf: { matrix: THREE.Matrix4; color: number }[] = []
  const conifers: { matrix: THREE.Matrix4; color: number }[] = []

  const scale = new THREE.Matrix4()
  for (const sub of subs) {
    const climate = climateOf(sub)
    const trees = forestTrees(sub, climate.annualMeanC)
    if (trees.length === 0) continue
    const frame = tangentBasisAt(latLonToVector3(sub.latitude, sub.longitude, 1))
    const south = frame.north.clone().negate()
    const basis = new THREE.Matrix4().makeBasis(frame.east, frame.normal, south)
    const surface = tileSurfaceRadius(sub, options)
    // 一棵樹：底部在地面、往東 x 往北 z，(寬, 高, 寬) 縮放（幾何體都是底部在原點、高 1、半徑 1）
    const at = (x: number, z: number, lift: number, width: number, height: number) => {
      const position = frame.normal
        .clone()
        .multiplyScalar(surface + lift * metersToScene)
        .addScaledVector(frame.east, x * metersToScene)
        .addScaledVector(frame.north, z * metersToScene)
      return basis
        .clone()
        .setPosition(position)
        .multiply(scale.makeScale(width * metersToScene, height * metersToScene, width * metersToScene))
    }
    for (const tree of trees) {
      const h = tree.height
      const color = crownColor(tree.kind, climate.season, climate.snow, tree.shade)
      if (tree.kind === 'conifer') {
        trunks.push(at(tree.x, tree.z, 0, h * 0.05, h * 0.25))
        conifers.push({ matrix: at(tree.x, tree.z, h * 0.15, h * 0.24, h * 0.85), color: color! })
      } else {
        trunks.push(at(tree.x, tree.z, 0, h * 0.06, h * 0.55))
        // 冬天落葉：只剩樹幹
        if (color !== null) broadleaf.push({ matrix: at(tree.x, tree.z, h * 0.38, h * 0.32, h * 0.62), color })
      }
    }
  }

  const group = new THREE.Group()
  const geometries: THREE.BufferGeometry[] = []
  const materials: THREE.Material[] = []

  function addInstanced(geometry: THREE.BufferGeometry, items: { matrix: THREE.Matrix4; color: number }[], uniform?: number) {
    if (items.length === 0) {
      geometry.dispose()
      return
    }
    geometries.push(geometry)
    const material = new THREE.MeshLambertMaterial({ color: uniform ?? 0xffffff })
    materials.push(material)
    const mesh = new THREE.InstancedMesh(geometry, material, items.length)
    const color = new THREE.Color()
    items.forEach((item, i) => {
      mesh.setMatrixAt(i, item.matrix)
      if (uniform === undefined) mesh.setColorAt(i, color.setHex(item.color))
    })
    mesh.instanceMatrix.needsUpdate = true
    if (mesh.instanceColor) mesh.instanceColor.needsUpdate = true
    mesh.computeBoundingSphere()
    mesh.raycast = () => {} // 點選照舊點到底下的小格
    group.add(mesh)
  }

  // 幾何體：底部在原點、高 1、半徑 1。面數壓低（幾萬棵樹）
  const trunk = new THREE.CylinderGeometry(0.7, 1, 1, 5, 1, true).translate(0, 0.5, 0)
  const crown = new THREE.IcosahedronGeometry(1, 0).scale(1, 0.5, 1).translate(0, 0.5, 0)
  const cone = new THREE.ConeGeometry(1, 1, 7, 1, true).translate(0, 0.5, 0)
  addInstanced(trunk, trunks.map((matrix) => ({ matrix, color: TRUNK_COLOR })), TRUNK_COLOR)
  addInstanced(crown, broadleaf)
  addInstanced(cone, conifers)

  return {
    group,
    treeCount: trunks.length,
    dispose() {
      for (const g of geometries) g.dispose()
      for (const m of materials) m.dispose()
      group.clear()
    },
  }
}
