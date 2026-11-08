import * as THREE from 'three'
import { mergeGeometries } from 'three/examples/jsm/utils/BufferGeometryUtils.js'
import type { StructureKind, SubTileDto } from '@/types/tile'
import { latLonToVector3, tangentBasisAt } from './sphereCoordinates'
import { tileFloorRadius, tileSurfaceRadius, type TileMeshOptions } from './tileMeshBuilder'

/**
 * 編輯模式的建築物模型（2026-11-08）：房子、城堡、橋、路，用方塊與柱體拼成。
 *
 * 尺寸都是公尺。水平是實際比例（一個小格的外接圓半徑 250 公尺，內切圓約 216 公尺，模型都收在內切圓裡，不會伸進鄰格）；
 * 高度是**畫面上的**公尺數——地形的海拔有 8 倍誇張，建築物不乘，否則一棟房子會比山還高。
 *
 * 同一種材質的東西合成一個 mesh：一片地方可能有上百段路，各自一個 mesh 會變成上百次 draw call。
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

/** 城堡的城牆是正方形，邊長 300 公尺：角落離中心 212 公尺，剛好收在小格的內切圓（216 公尺）裡。 */
const CASTLE_SIZE_METERS = 300
const CASTLE_WALL_HEIGHT_METERS = 80
const CASTLE_WALL_THICKNESS_METERS = 26
const CASTLE_TOWER_RADIUS_METERS = 38
const CASTLE_TOWER_HEIGHT_METERS = 140
const CASTLE_TOWER_ROOF_METERS = 70
const CASTLE_KEEP_SIZE_METERS = 110
const CASTLE_KEEP_HEIGHT_METERS = 170
const CASTLE_KEEP_ROOF_METERS = 80

const COLORS = {
  houseWall: 0xe6d8b8,
  houseRoof: 0xa2452e,
  castleStone: 0x9c9a90,
  castleRoof: 0x47587a,
  road: 0x9a8a70,
  bridge: 0xa07a50,
} as const

type Part = keyof typeof COLORS

/** 道路會接到這些建築物：路通到房子、城堡門口，也接上橋。 */
const ROAD_CONNECTS_TO: ReadonlySet<StructureKind> = new Set(['Road', 'Bridge', 'House', 'Castle'])
/** 橋只接路與橋：橋接到房子沒有意義。 */
const BRIDGE_CONNECTS_TO: ReadonlySet<StructureKind> = new Set(['Road', 'Bridge'])

/** 這一格的道路／橋要往哪些鄰格延伸。 */
export function connectedNeighbours(
  sub: SubTileDto,
  neighbours: readonly SubTileDto[],
): SubTileDto[] {
  const targets = sub.structure === 'Bridge' ? BRIDGE_CONNECTS_TO : ROAD_CONNECTS_TO
  return neighbours.filter((n) => n.structure !== undefined && targets.has(n.structure))
}

export interface ConstructionLayer {
  /** 加進場景的群組。 */
  readonly group: THREE.Group
  dispose(): void
}

/**
 * 建出一批小格上的所有建築物。
 * @param subs 這一批小格（只有 `structure` 有值的會畫）。
 * @param neighboursOf 同一批裡的鄰居（道路要知道往哪裡接）。
 * @param options 小格的尺寸換算（跟小格 mesh 同一份）。
 */
export function buildConstructionLayer(
  subs: readonly SubTileDto[],
  neighboursOf: (sub: SubTileDto) => readonly SubTileDto[],
  options: TileMeshOptions,
): ConstructionLayer {
  const metersToScene = options.renderRadius / options.planetRadiusMeters
  const parts = new Map<Part, THREE.BufferGeometry[]>()
  const add = (part: Part, geometry: THREE.BufferGeometry, matrix: THREE.Matrix4) => {
    geometry.applyMatrix4(matrix)
    // 合併要求每一塊的屬性一樣：圓盤、柱體都有 uv，這裡統一拿掉
    geometry.deleteAttribute('uv')
    let list = parts.get(part)
    if (!list) parts.set(part, (list = []))
    list.push(geometry)
  }

  for (const sub of subs) {
    if (!sub.structure) continue
    const frame = localFrame(sub)
    // 區域座標 x＝東、y＝上、z＝南：(東, 上, 北) 是左手系，模型會被鏡射、面朝內，看到的是內側（2026-11-08 踩過）
    const south = frame.north.clone().negate()
    const surface = tileSurfaceRadius(sub, options)
    /** 在這一格的切平面上、離球心 `radius`、往東 `x` 北 `z` 公尺（已換算成場景單位）的變換。 */
    const at = (radius: number, x = 0, z = 0, rotationY = 0) =>
      new THREE.Matrix4()
        .makeBasis(frame.east, frame.normal, south)
        .setPosition(
          frame.normal
            .clone()
            .multiplyScalar(radius)
            .addScaledVector(frame.east, x * metersToScene)
            .addScaledVector(frame.north, z * metersToScene),
        )
        .multiply(new THREE.Matrix4().makeRotationY(rotationY))
    const m = (meters: number) => meters * metersToScene

    switch (sub.structure) {
      case 'House': {
        add('houseWall', box(m(HOUSE_SIZE_METERS), m(HOUSE_WALL_METERS), m(HOUSE_SIZE_METERS)), at(surface + m(HOUSE_WALL_METERS / 2)))
        add(
          'houseRoof',
          pyramid(m(HOUSE_SIZE_METERS * 0.78), m(HOUSE_ROOF_METERS)),
          at(surface + m(HOUSE_WALL_METERS + HOUSE_ROOF_METERS / 2)),
        )
        break
      }
      case 'Castle': {
        const half = CASTLE_SIZE_METERS / 2
        const wallY = surface + m(CASTLE_WALL_HEIGHT_METERS / 2)
        for (const [x, z, rotation] of [
          [0, half, 0],
          [0, -half, 0],
          [half, 0, Math.PI / 2],
          [-half, 0, Math.PI / 2],
        ] as const) {
          add(
            'castleStone',
            box(m(CASTLE_SIZE_METERS), m(CASTLE_WALL_HEIGHT_METERS), m(CASTLE_WALL_THICKNESS_METERS)),
            at(wallY, x, z, rotation),
          )
        }
        const towerInset = half - CASTLE_TOWER_RADIUS_METERS * 0.6
        for (const [x, z] of [
          [towerInset, towerInset],
          [towerInset, -towerInset],
          [-towerInset, towerInset],
          [-towerInset, -towerInset],
        ] as const) {
          add(
            'castleStone',
            cylinder(m(CASTLE_TOWER_RADIUS_METERS), m(CASTLE_TOWER_HEIGHT_METERS)),
            at(surface + m(CASTLE_TOWER_HEIGHT_METERS / 2), x, z),
          )
          add(
            'castleRoof',
            cone(m(CASTLE_TOWER_RADIUS_METERS * 1.2), m(CASTLE_TOWER_ROOF_METERS)),
            at(surface + m(CASTLE_TOWER_HEIGHT_METERS + CASTLE_TOWER_ROOF_METERS / 2), x, z),
          )
        }
        add(
          'castleStone',
          box(m(CASTLE_KEEP_SIZE_METERS), m(CASTLE_KEEP_HEIGHT_METERS), m(CASTLE_KEEP_SIZE_METERS)),
          at(surface + m(CASTLE_KEEP_HEIGHT_METERS / 2)),
        )
        add(
          'castleRoof',
          pyramid(m(CASTLE_KEEP_SIZE_METERS * 0.78), m(CASTLE_KEEP_ROOF_METERS)),
          at(surface + m(CASTLE_KEEP_HEIGHT_METERS + CASTLE_KEEP_ROOF_METERS / 2)),
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
        const part: Part = isBridge ? 'bridge' : 'road'
        // 中心：路是圓盤（轉彎不缺角），橋是方形的橋墩頂
        add(
          part,
          isBridge ? box(m(width), m(thickness), m(width)) : cylinder(m(ROAD_HUB_RADIUS_METERS), m(thickness), 16),
          at(centerY),
        )
        // 往每個相連的鄰格延伸到兩格的交界（鄰格自己畫另一半）
        for (const link of links) {
          const offset = latLonToVector3(link.latitude, link.longitude, 1).sub(frame.normal)
          // 單位球上的位移換算成公尺
          const east = offset.dot(frame.east) * options.planetRadiusMeters
          const north = offset.dot(frame.north) * options.planetRadiusMeters
          const half = Math.hypot(east, north) / 2
          if (half === 0) continue
          // 旋轉後區域的 z 軸（南）指向 (sinθ 東, −cosθ 北)，要對準往鄰格的方向
          const rotation = Math.atan2(east, -north)
          add(
            part,
            box(m(width), m(thickness), m(half)),
            at(centerY, east / 4, north / 4, rotation), // 中心到交界的中點
          )
        }
        if (isBridge) {
          // 橋墩：從水底到橋面
          const pierHeight = centerY - surface
          if (pierHeight > 0)
            add('bridge', cylinder(m(BRIDGE_PIER_RADIUS_METERS), pierHeight, 10), at(surface + pierHeight / 2))
        }
        break
      }
    }
  }

  const group = new THREE.Group()
  group.name = 'constructions'
  const materials: THREE.Material[] = []
  for (const [part, geometries] of parts) {
    const merged = mergeGeometries(geometries, false)
    for (const geometry of geometries) geometry.dispose()
    if (!merged) continue
    const material = new THREE.MeshLambertMaterial({ color: COLORS[part] })
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

function localFrame(sub: SubTileDto) {
  return tangentBasisAt(latLonToVector3(sub.latitude, sub.longitude, 1))
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

/** 四角錐屋頂：底面的四個角對齊方塊的四個角。 */
function pyramid(halfDiagonal: number, height: number): THREE.BufferGeometry {
  return new THREE.ConeGeometry(halfDiagonal, height, 4).rotateY(Math.PI / 4)
}
