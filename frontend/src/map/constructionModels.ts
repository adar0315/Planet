import * as THREE from 'three'
import { mergeGeometries } from 'three/examples/jsm/utils/BufferGeometryUtils.js'
import type { StructureKind, SubTileDto } from '@/types/tile'
import { latLonToVector3, tangentBasisAt } from './sphereCoordinates'
import {
  computeTilePolygon,
  tileFloorRadius,
  tileSurfaceRadius,
  type TileMeshOptions,
} from './tileMeshBuilder'

/**
 * 編輯模式的建築物模型（2026-11-08）：房子、城堡、橋、路、農田、果園、牧場，用方塊與柱體拼成。
 *
 * 尺寸都是公尺。水平是實際比例（一個小格的外接圓半徑 250 公尺，內切圓約 216 公尺）；
 * 高度是**畫面上的**公尺數——地形的海拔有 8 倍誇張，建築物不乘，否則一棟房子會比山還高。
 *
 * 同一個顏色的東西合成一個 mesh：一片地方可能有上百段路、上千棵果樹，各自一個 mesh 會變成上千次 draw call。
 */

/** 道路寬度（公尺）。 */
export const ROAD_WIDTH_METERS = 80
/** 路面比地面高多少（畫面上的公尺）：太低會跟地面搶深度而閃爍。 */
const ROAD_LIFT_METERS = 4
const ROAD_THICKNESS_METERS = 4
/** 道路中心的圓盤半徑：轉彎與交叉口不會缺角。 */
const ROAD_HUB_RADIUS_METERS = ROAD_WIDTH_METERS / 2

const BRIDGE_WIDTH_METERS = 90
const BRIDGE_THICKNESS_METERS = 8
/** 橋面比水面高多少（畫面上的公尺）。 */
export const BRIDGE_CLEARANCE_METERS = 50
const BRIDGE_PIER_RADIUS_METERS = 12

const HOUSE_SIZE_METERS = 140
const HOUSE_WALL_METERS = 70
const HOUSE_ROOF_METERS = 60

/** 城牆：沿整座城的外緣（相鄰的城堡格之間不畫）。 */
const CASTLE_WALL_HEIGHT_METERS = 80
const CASTLE_WALL_THICKNESS_METERS = 26
/** 城牆從格子邊緣往內縮多少：牆整個落在自己這一格裡，不壓到鄰格。 */
const CASTLE_WALL_INSET_METERS = 18
const CASTLE_TOWER_RADIUS_METERS = 34
const CASTLE_TOWER_HEIGHT_METERS = 130
const CASTLE_TOWER_ROOF_METERS = 65
const CASTLE_KEEP_SIZE_METERS = 110
const CASTLE_KEEP_HEIGHT_METERS = 170
const CASTLE_KEEP_ROOF_METERS = 80
/** 主堡每多一格城就放大這麼多（7 格的城主堡約 1.7 倍）。 */
const CASTLE_KEEP_GROWTH_PER_CELL = 0.12
const CASTLE_HALL_SIZE = { width: 150, height: 55, depth: 90, roof: 45 } as const

/** 農田、果園、牧場的地面：比地面高一點，蓋住底下的地形顏色。 */
const FIELD_LIFT_METERS = 2
/** 地面多邊形相對格子縮小的比例：留一圈格子原本的顏色當田埂。 */
const FIELD_SHRINK = 0.94
/** 田、果樹、牲畜擺放的範圍（半徑，公尺）：收在小格的內切圓（約 216 公尺）裡。 */
const FIELD_RADIUS_METERS = 190
const FURROW_SPACING_METERS = 38
const FURROW_WIDTH_METERS = 14
const FURROW_HEIGHT_METERS = 3
const ORCHARD_SPACING_METERS = 72
const TREE_TRUNK = { radius: 6, height: 30 } as const
const TREE_CROWN_RADIUS_METERS = 26
const FENCE_HEIGHT_METERS = 16
const FENCE_POST_SPACING_METERS = 55
const SHEEP_PER_PASTURE = 5

export type Season = '春' | '夏' | '秋' | '冬'

/** 一個小格當下的季節：農田、果園、牧場的樣子跟著它變。 */
export interface SeasonState {
  season: Season
  /** 地面被雪蓋住。 */
  snow: boolean
}

const COLORS = {
  houseWall: 0xe6d8b8,
  houseRoof: 0xa2452e,
  castleStone: 0x9c9a90,
  castleRoof: 0x47587a,
  road: 0x9a8a70,
  bridge: 0xa07a50,
  snow: 0xeef2f5,
  snowFurrow: 0xc4ccd4,
  trunk: 0x6b4a2e,
  fence: 0x8a6440,
  sheep: 0xf1efe6,
  sheepHead: 0x3a3430,
  fruit: 0xd8442a,
} as const

/** 農田：地面與田壟的顏色（春綠、夏深綠、秋金黃、冬天空田）。 */
const FARM_COLORS: Record<Season, { ground: number; furrow: number }> = {
  春: { ground: 0x9cc95a, furrow: 0x6e9a3a },
  夏: { ground: 0x4f8f2f, furrow: 0x356b1f },
  秋: { ground: 0xd8b445, furrow: 0xa8862c },
  冬: { ground: 0x8a6a4a, furrow: 0x6a4e34 },
}
/** 果園：地面與樹冠（春天開花、秋天結果、冬天落葉沒有樹冠）。 */
const ORCHARD_COLORS: Record<Season, { ground: number; crown: number | null }> = {
  春: { ground: 0x7fae4a, crown: 0xf2c7d8 },
  夏: { ground: 0x6a9a3c, crown: 0x3f7a2a },
  秋: { ground: 0x9a9a48, crown: 0x5f8a2e },
  冬: { ground: 0x8f8460, crown: null },
}
/** 牧場的草地（冬天枯黃）。 */
const PASTURE_COLORS: Record<Season, number> = {
  春: 0x8cc456,
  夏: 0x6aa83c,
  秋: 0xa8a64c,
  冬: 0xb59f6a,
}

/** 道路會接到這些建築物：路通到房子、城堡門口，也接上橋。 */
const ROAD_CONNECTS_TO: ReadonlySet<StructureKind> = new Set(['Road', 'Bridge', 'House', 'Castle'])
/** 橋只接路與橋：橋接到房子沒有意義。 */
const BRIDGE_CONNECTS_TO: ReadonlySet<StructureKind> = new Set(['Road', 'Bridge'])
/** 會跟著季節變的建築物：季節變了要重建。 */
export const SEASONAL_STRUCTURES: ReadonlySet<StructureKind> = new Set(['Farm', 'Orchard', 'Pasture'])

/** 這一格的道路／橋要往哪些鄰格延伸。 */
export function connectedNeighbours(
  sub: SubTileDto,
  neighbours: readonly SubTileDto[],
): SubTileDto[] {
  const targets = sub.structure === 'Bridge' ? BRIDGE_CONNECTS_TO : ROAD_CONNECTS_TO
  return neighbours.filter((n) => n.structure !== undefined && targets.has(n.structure))
}

/**
 * 相鄰的城堡格連成一座城（後端限制最多 7 格）。回傳每座城的格子，第一格是主堡所在：
 * 城裡跟最多城堡格相鄰的那一格（同分取編號最小，結果固定）。
 */
export function castleGroups(
  subs: readonly SubTileDto[],
  neighboursOf: (sub: SubTileDto) => readonly SubTileDto[],
): SubTileDto[][] {
  const castleNeighbours = (sub: SubTileDto) => neighboursOf(sub).filter((n) => n.structure === 'Castle')
  const seen = new Set<number>()
  const groups: SubTileDto[][] = []
  for (const start of subs) {
    if (start.structure !== 'Castle' || seen.has(start.tileId)) continue
    const group: SubTileDto[] = []
    const queue = [start]
    seen.add(start.tileId)
    while (queue.length > 0) {
      const sub = queue.pop()!
      group.push(sub)
      for (const n of castleNeighbours(sub)) {
        if (seen.has(n.tileId)) continue
        seen.add(n.tileId)
        queue.push(n)
      }
    }
    const score = (s: SubTileDto) => castleNeighbours(s).length
    group.sort((a, b) => score(b) - score(a) || a.tileId - b.tileId)
    groups.push(group)
  }
  return groups
}

export interface ConstructionLayer {
  /** 加進場景的群組。 */
  readonly group: THREE.Group
  dispose(): void
}

/**
 * 建出一批小格上的所有建築物。
 * @param subs 這一批小格（只有 `structure` 有值的會畫）。
 * @param neighboursOf 同一批裡的鄰居（道路要知道往哪裡接、城牆要知道哪些邊是外緣）。
 * @param options 小格的尺寸換算（跟小格 mesh 同一份）。
 * @param seasonOf 農田、果園、牧場當下的季節；省略時當成夏天、沒有雪。
 */
export function buildConstructionLayer(
  subs: readonly SubTileDto[],
  neighboursOf: (sub: SubTileDto) => readonly SubTileDto[],
  options: TileMeshOptions,
  seasonOf: (sub: SubTileDto) => SeasonState = () => ({ season: '夏', snow: false }),
): ConstructionLayer {
  const metersToScene = options.renderRadius / options.planetRadiusMeters
  const m = (meters: number) => meters * metersToScene
  const parts = new Map<number, THREE.BufferGeometry[]>()
  const add = (color: number, geometry: THREE.BufferGeometry, matrix?: THREE.Matrix4) => {
    if (matrix) geometry.applyMatrix4(matrix)
    // 合併要求每一塊的屬性與索引一致：uv 統一拿掉；方塊、柱體有索引、球與地面沒有，統一攤平成沒有索引
    geometry.deleteAttribute('uv')
    const flat = geometry.index ? geometry.toNonIndexed() : geometry
    if (flat !== geometry) geometry.dispose()
    let list = parts.get(color)
    if (!list) parts.set(color, (list = []))
    list.push(flat)
  }

  // 城堡：整座城一起畫（城牆只畫外緣）
  for (const group of castleGroups(subs, neighboursOf)) {
    const members = new Set(group.map((s) => s.tileId))
    group.forEach((sub, index) => {
      const place = placer(sub, options)
      const polygon = computeTilePolygon(sub, options, neighboursOf(sub))
      const corners = polygon.ring.map((p) => place.local(p))
      const boundary = polygon.edgeNeighbours.map((n) => !n || !members.has(n.tileId))
      const count = corners.length
      for (let i = 0; i < count; i++) {
        if (!boundary[i]) continue
        const a = corners[i]!
        const b = corners[(i + 1) % count]!
        const offset = CASTLE_WALL_INSET_METERS + CASTLE_WALL_THICKNESS_METERS / 2
        const inward = inwardOffset(a, b, offset)
        const edge = Math.hypot(b.x - a.x, b.z - a.z)
        // 牆往內縮之後，兩端要跟下一段牆接上：凸角（兩邊都是這一格的外緣，內角 120°）縮短 offset/tan60°；
        // 凹角（外緣轉到相鄰的城堡格，內角 240°）要延長 offset·tan60°，不然兩格的牆在那裡斷開
        const endAt = (convex: boolean) => (convex ? -offset / Math.sqrt(3) : offset * Math.sqrt(3))
        const startExtra = endAt(boundary[(i - 1 + count) % count]!)
        const endExtra = endAt(boundary[(i + 1) % count]!)
        const length = edge + startExtra + endExtra
        const shift = (endExtra - startExtra) / 2 / edge // 沿邊往 b 移動的比例
        add(
          COLORS.castleStone,
          box(m(length), m(CASTLE_WALL_HEIGHT_METERS), m(CASTLE_WALL_THICKNESS_METERS)),
          place.at(
            place.surface + m(CASTLE_WALL_HEIGHT_METERS / 2),
            (a.x + b.x) / 2 + inward.x + (b.x - a.x) * shift,
            (a.z + b.z) / 2 + inward.z + (b.z - a.z) * shift,
            Math.atan2(b.z - a.z, b.x - a.x),
          ),
        )
      }
      // 塔：兩邊都是外緣的角（城的凸角）
      for (let i = 0; i < count; i++) {
        if (!boundary[i] || !boundary[(i - 1 + count) % count]) continue
        const corner = corners[i]!
        const toCenter = Math.hypot(corner.x, corner.z)
        const scale = (toCenter - CASTLE_WALL_INSET_METERS - CASTLE_TOWER_RADIUS_METERS * 0.6) / toCenter
        const x = corner.x * scale
        const z = corner.z * scale
        add(
          COLORS.castleStone,
          cylinder(m(CASTLE_TOWER_RADIUS_METERS), m(CASTLE_TOWER_HEIGHT_METERS)),
          place.at(place.surface + m(CASTLE_TOWER_HEIGHT_METERS / 2), x, z),
        )
        add(
          COLORS.castleRoof,
          cone(m(CASTLE_TOWER_RADIUS_METERS * 1.2), m(CASTLE_TOWER_ROOF_METERS)),
          place.at(place.surface + m(CASTLE_TOWER_HEIGHT_METERS + CASTLE_TOWER_ROOF_METERS / 2), x, z),
        )
      }
      if (index === 0) {
        const grow = 1 + CASTLE_KEEP_GROWTH_PER_CELL * (group.length - 1)
        const size = CASTLE_KEEP_SIZE_METERS * grow
        const height = CASTLE_KEEP_HEIGHT_METERS * Math.sqrt(grow)
        add(COLORS.castleStone, box(m(size), m(height), m(size)), place.at(place.surface + m(height / 2)))
        add(
          COLORS.castleRoof,
          pyramid(m(size * 0.78), m(CASTLE_KEEP_ROOF_METERS * grow)),
          place.at(place.surface + m(height + (CASTLE_KEEP_ROOF_METERS * grow) / 2)),
        )
      } else {
        // 城裡其他格：一棟大廳，朝向主堡
        const keep = place.local(latLonToVector3(group[0]!.latitude, group[0]!.longitude, 1))
        const rotation = Math.atan2(keep.z, keep.x)
        const hall = CASTLE_HALL_SIZE
        add(
          COLORS.castleStone,
          box(m(hall.width), m(hall.height), m(hall.depth)),
          place.at(place.surface + m(hall.height / 2), 0, 0, rotation),
        )
        add(
          COLORS.castleRoof,
          gable(m(hall.width), m(hall.roof), m(hall.depth)),
          place.at(place.surface + m(hall.height), 0, 0, rotation),
        )
      }
    })
  }

  for (const sub of subs) {
    if (!sub.structure || sub.structure === 'Castle') continue
    const place = placer(sub, options)
    const { surface, at } = place

    switch (sub.structure) {
      case 'House': {
        add(COLORS.houseWall, box(m(HOUSE_SIZE_METERS), m(HOUSE_WALL_METERS), m(HOUSE_SIZE_METERS)), at(surface + m(HOUSE_WALL_METERS / 2)))
        add(
          COLORS.houseRoof,
          pyramid(m(HOUSE_SIZE_METERS * 0.78), m(HOUSE_ROOF_METERS)),
          at(surface + m(HOUSE_WALL_METERS + HOUSE_ROOF_METERS / 2)),
        )
        break
      }
      case 'Road':
      case 'Bridge': {
        const isBridge = sub.structure === 'Bridge'
        const links = connectedNeighbours(sub, neighboursOf(sub))
        const width = isBridge ? BRIDGE_WIDTH_METERS : ROAD_WIDTH_METERS
        const thickness = isBridge ? BRIDGE_THICKNESS_METERS : ROAD_THICKNESS_METERS
        const top = isBridge
          ? bridgeDeckRadius(sub, neighboursOf(sub), options)
          : surface + m(ROAD_LIFT_METERS)
        const centerY = top - m(thickness / 2)
        const color = isBridge ? COLORS.bridge : COLORS.road
        // 中心：路是圓盤（轉彎不缺角），橋是方形的橋墩頂
        add(
          color,
          isBridge ? box(m(width), m(thickness), m(width)) : cylinder(m(ROAD_HUB_RADIUS_METERS), m(thickness), 16),
          at(centerY),
        )
        // 往每個相連的鄰格延伸到兩格的交界（鄰格自己畫另一半）
        for (const link of links) {
          const target = place.local(latLonToVector3(link.latitude, link.longitude, 1))
          const half = Math.hypot(target.x, target.z) / 2
          if (half === 0) continue
          add(
            color,
            box(m(half), m(thickness), m(width)),
            at(centerY, target.x / 4, target.z / 4, Math.atan2(target.z, target.x)), // 中心到交界的中點
          )
        }
        if (isBridge) {
          // 橋墩：從水底到橋面
          const pierHeight = centerY - surface
          if (pierHeight > 0)
            add(COLORS.bridge, cylinder(m(BRIDGE_PIER_RADIUS_METERS), pierHeight, 10), at(surface + pierHeight / 2))
        }
        break
      }
      case 'Farm': {
        const { season, snow } = seasonOf(sub)
        const colors = snow ? { ground: COLORS.snow, furrow: COLORS.snowFurrow } : FARM_COLORS[season]
        add(colors.ground, fieldPlate(sub, neighboursOf(sub), options))
        // 田壟：東西向的長條，長度依離中心的距離縮短，整片收在圓裡
        for (let z = -FIELD_RADIUS_METERS + FURROW_SPACING_METERS / 2; z < FIELD_RADIUS_METERS; z += FURROW_SPACING_METERS) {
          const length = 2 * Math.sqrt(FIELD_RADIUS_METERS ** 2 - z * z)
          if (length < FURROW_SPACING_METERS) continue
          add(
            colors.furrow,
            box(m(length), m(FURROW_HEIGHT_METERS), m(FURROW_WIDTH_METERS)),
            at(surface + m(FIELD_LIFT_METERS + FURROW_HEIGHT_METERS / 2), 0, z),
          )
        }
        break
      }
      case 'Orchard': {
        const { season, snow } = seasonOf(sub)
        const colors = ORCHARD_COLORS[season]
        add(snow ? COLORS.snow : colors.ground, fieldPlate(sub, neighboursOf(sub), options))
        const base = surface + m(FIELD_LIFT_METERS)
        for (const [x, z] of gridInCircle(ORCHARD_SPACING_METERS, FIELD_RADIUS_METERS - TREE_CROWN_RADIUS_METERS)) {
          add(COLORS.trunk, cylinder(m(TREE_TRUNK.radius), m(TREE_TRUNK.height), 6), at(base + m(TREE_TRUNK.height / 2), x, z))
          if (colors.crown === null) continue
          const crownY = base + m(TREE_TRUNK.height + TREE_CROWN_RADIUS_METERS * 0.7)
          add(colors.crown, blob(m(TREE_CROWN_RADIUS_METERS)), at(crownY, x, z))
          if (season === '秋') {
            // 結果：樹冠外側幾顆紅色的果子
            for (const angle of [0.4, 2.5, 4.4]) {
              add(
                COLORS.fruit,
                blob(m(7)),
                at(crownY - m(6), x + Math.cos(angle) * TREE_CROWN_RADIUS_METERS * 0.85, z + Math.sin(angle) * TREE_CROWN_RADIUS_METERS * 0.85),
              )
            }
          }
        }
        break
      }
      case 'Pasture': {
        const { season, snow } = seasonOf(sub)
        add(snow ? COLORS.snow : PASTURE_COLORS[season], fieldPlate(sub, neighboursOf(sub), options))
        // 圍欄：沿格子的邊，相鄰的牧場之間不圍（連成一大片）
        const polygon = computeTilePolygon(sub, options, neighboursOf(sub))
        const corners = polygon.ring.map((p) => place.local(p))
        const base = surface + m(FIELD_LIFT_METERS)
        for (let i = 0; i < corners.length; i++) {
          const neighbour = polygon.edgeNeighbours[i] as SubTileDto | null
          if (neighbour?.structure === 'Pasture') continue
          const a = corners[i]!
          const b = corners[(i + 1) % corners.length]!
          const inward = inwardOffset(a, b, 12)
          const ax = a.x * 0.97 + inward.x
          const az = a.z * 0.97 + inward.z
          const bx = b.x * 0.97 + inward.x
          const bz = b.z * 0.97 + inward.z
          const length = Math.hypot(bx - ax, bz - az)
          const rotation = Math.atan2(bz - az, bx - ax)
          for (const height of [FENCE_HEIGHT_METERS * 0.45, FENCE_HEIGHT_METERS * 0.9]) {
            add(COLORS.fence, box(m(length), m(2.5), m(2.5)), at(base + m(height), (ax + bx) / 2, (az + bz) / 2, rotation))
          }
          const posts = Math.max(2, Math.round(length / FENCE_POST_SPACING_METERS) + 1)
          for (let k = 0; k < posts; k++) {
            const t = k / (posts - 1)
            add(
              COLORS.fence,
              box(m(5), m(FENCE_HEIGHT_METERS), m(5)),
              at(base + m(FENCE_HEIGHT_METERS / 2), ax + (bx - ax) * t, az + (bz - az) * t),
            )
          }
        }
        // 牲畜：位置由小格編號決定，每次重建都在同一個地方
        const random = seededRandom(sub.tileId)
        for (let k = 0; k < SHEEP_PER_PASTURE; k++) {
          const radius = Math.sqrt(random()) * 130
          const angle = random() * Math.PI * 2
          const x = Math.cos(angle) * radius
          const z = Math.sin(angle) * radius
          const heading = random() * Math.PI * 2
          add(COLORS.sheep, box(m(28), m(14), m(16)), at(base + m(12), x, z, heading))
          add(
            COLORS.sheepHead,
            box(m(9), m(9), m(9)),
            at(base + m(18), x + Math.cos(heading) * 17, z + Math.sin(heading) * 17, heading),
          )
        }
        break
      }
    }
  }

  const group = new THREE.Group()
  group.name = 'constructions'
  const materials: THREE.Material[] = []
  for (const [color, geometries] of parts) {
    const merged = mergeGeometries(geometries, false)
    for (const geometry of geometries) geometry.dispose()
    if (!merged) continue
    const material = new THREE.MeshLambertMaterial({ color })
    materials.push(material)
    const mesh = new THREE.Mesh(merged, material)
    mesh.raycast = () => {} // 點選照舊點到底下的小格
    group.add(mesh)
  }

  return {
    group,
    dispose() {
      for (const child of group.children) (child as THREE.Mesh).geometry.dispose()
      for (const material of materials) material.dispose()
      group.clear()
    },
  }
}

/**
 * 橋面的半徑：水面以上 {@link BRIDGE_CLEARANCE_METERS}，而且不低於相連的岸上道路——
 * 挖出來的河道水面比兩岸低，橋面若只看水面，會比岸上的路還低。
 */
export function bridgeDeckRadius(
  sub: SubTileDto,
  neighbours: readonly SubTileDto[],
  options: TileMeshOptions,
): number {
  const metersToScene = options.renderRadius / options.planetRadiusMeters
  let deck = tileFloorRadius(sub, options) + BRIDGE_CLEARANCE_METERS * metersToScene
  for (const n of connectedNeighbours(sub, neighbours)) {
    if (n.structure === 'Road')
      deck = Math.max(deck, tileSurfaceRadius(n, options) + ROAD_LIFT_METERS * metersToScene)
  }
  return deck
}

/**
 * 一個小格的區域座標：x＝東、y＝上、z＝**南**——(東, 上, 北) 是左手系，模型會被鏡射、
 * 面朝內，看到的是發暗的內側（2026-11-08 踩過）。對外的參數仍然是往東 x、往北 z 公尺。
 */
function placer(sub: SubTileDto, options: TileMeshOptions) {
  const metersToScene = options.renderRadius / options.planetRadiusMeters
  const frame = tangentBasisAt(latLonToVector3(sub.latitude, sub.longitude, 1))
  const south = frame.north.clone().negate()
  const basis = new THREE.Matrix4().makeBasis(frame.east, frame.normal, south)
  return {
    surface: tileSurfaceRadius(sub, options),
    /** 離球心 `radius`、往東 `x` 北 `z` 公尺、繞上方轉 `rotation`（0＝長邊朝東、逆時針為正，從上往下看）。 */
    at(radius: number, x = 0, z = 0, rotation = 0): THREE.Matrix4 {
      return basis
        .clone()
        .setPosition(
          frame.normal
            .clone()
            .multiplyScalar(radius)
            .addScaledVector(frame.east, x * metersToScene)
            .addScaledVector(frame.north, z * metersToScene),
        )
        .multiply(new THREE.Matrix4().makeRotationY(rotation))
    },
    /** 一點（任意半徑）換算成這一格切平面上的往東、往北公尺數。 */
    local(point: THREE.Vector3): { x: number; z: number } {
      const offset = point.clone().normalize().sub(frame.normal)
      return {
        x: offset.dot(frame.east) * options.planetRadiusMeters,
        z: offset.dot(frame.north) * options.planetRadiusMeters,
      }
    },
  }
}

/** 邊 a→b 往格子中心（原點）那一側、距離 `distance` 公尺的位移。 */
function inwardOffset(a: { x: number; z: number }, b: { x: number; z: number }, distance: number) {
  const mx = (a.x + b.x) / 2
  const mz = (a.z + b.z) / 2
  const length = Math.hypot(mx, mz) || 1
  return { x: (-mx / length) * distance, z: (-mz / length) * distance }
}

/**
 * 農田、果園、牧場的地面：格子的多邊形縮小一點、抬高一點，蓋住底下的地形顏色。
 * 直接用世界座標建（多邊形本來就是），法向量朝上。
 */
function fieldPlate(
  sub: SubTileDto,
  neighbours: readonly SubTileDto[],
  options: TileMeshOptions,
): THREE.BufferGeometry {
  const polygon = computeTilePolygon(sub, options, neighbours)
  const lift = (FIELD_LIFT_METERS / options.planetRadiusMeters) * options.renderRadius
  const center = polygon.center.clone().addScaledVector(polygon.normal, lift)
  // 縮一圈留田埂，但跟同一種田相鄰的角不縮：相鄰的農田、牧場連成一整片，中間不夾一條草
  const count = polygon.ring.length
  const sameKind = (i: number) =>
    (polygon.edgeNeighbours[(i + count) % count] as SubTileDto | null)?.structure === sub.structure
  const ring = polygon.ring.map((p, i) =>
    center
      .clone()
      .lerp(p.clone().addScaledVector(polygon.normal, lift), sameKind(i) || sameKind(i - 1) ? 1 : FIELD_SHRINK),
  )
  const positions: number[] = []
  const normals: number[] = []
  for (let i = 0; i < ring.length; i++) {
    let a = ring[i]!
    let b = ring[(i + 1) % ring.length]!
    // 繞向要讓面朝上（從外面看是逆時針）
    const face = a.clone().sub(center).cross(b.clone().sub(center))
    if (face.dot(polygon.normal) < 0) [a, b] = [b, a]
    for (const p of [center, a, b]) {
      positions.push(p.x, p.y, p.z)
      normals.push(polygon.normal.x, polygon.normal.y, polygon.normal.z)
    }
  }
  const geometry = new THREE.BufferGeometry()
  geometry.setAttribute('position', new THREE.Float32BufferAttribute(positions, 3))
  geometry.setAttribute('normal', new THREE.Float32BufferAttribute(normals, 3))
  return geometry
}

/** 圓裡的六角格點（公尺）：果樹的位置。 */
function gridInCircle(spacing: number, radius: number): [number, number][] {
  const points: [number, number][] = []
  const rowHeight = (spacing * Math.sqrt(3)) / 2
  for (let row = -Math.floor(radius / rowHeight); row * rowHeight <= radius; row++) {
    const z = row * rowHeight
    const shift = row % 2 === 0 ? 0 : spacing / 2
    for (let x = -Math.floor(radius / spacing) * spacing + shift; x <= radius; x += spacing) {
      if (x * x + z * z <= radius * radius) points.push([x, z])
    }
  }
  return points
}

/** 由編號決定的亂數（每次重建同一格都一樣）。 */
function seededRandom(seed: number): () => number {
  let state = (seed % 2147483647) || 1
  return () => {
    state = (state * 48271) % 2147483647
    return state / 2147483647
  }
}

function box(width: number, height: number, depth: number): THREE.BufferGeometry {
  return new THREE.BoxGeometry(width, height, depth)
}

function cylinder(radius: number, height: number, segments = 12): THREE.BufferGeometry {
  return new THREE.CylinderGeometry(radius, radius, height, segments)
}

function cone(radius: number, height: number): THREE.BufferGeometry {
  return new THREE.ConeGeometry(radius, height, 12)
}

/** 樹冠、果子：低面數的球。 */
function blob(radius: number): THREE.BufferGeometry {
  return new THREE.IcosahedronGeometry(radius, 0)
}

/** 四角錐屋頂：底面的四個角對齊方塊的四個角。 */
function pyramid(halfDiagonal: number, height: number): THREE.BufferGeometry {
  return new THREE.ConeGeometry(halfDiagonal, height, 4).rotateY(Math.PI / 4)
}

/** 人字形屋頂（三角柱），底面中心在原點、屋脊沿 x 軸。 */
function gable(width: number, height: number, depth: number): THREE.BufferGeometry {
  const shape = new THREE.Shape()
  shape.moveTo(-depth / 2, 0)
  shape.lineTo(depth / 2, 0)
  shape.lineTo(0, height)
  shape.closePath()
  const geometry = new THREE.ExtrudeGeometry(shape, { depth: width, bevelEnabled: false })
  // 擠出方向是 z：轉成沿 x，再置中
  geometry.translate(0, 0, -width / 2)
  geometry.rotateY(Math.PI / 2)
  return geometry
}
