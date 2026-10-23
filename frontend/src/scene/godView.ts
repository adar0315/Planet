import * as THREE from 'three'
import {
  forwardForHeading,
  latLonOf,
  moveAlongSurface,
  turnInPlace,
} from '@/map/surfaceMotion'
import { verticalSpeedAt } from '@/map/flightMotion'
import { edgeDepthOf, findNearestTile } from '@/map/tilePatch'
import { latLonToVector3 } from '@/map/sphereCoordinates'
import { tileFloorRadius, type TileMeshOptions } from '@/map/tileMeshBuilder'
import type { BindableAction } from '@/stores/keyBindings'
import type { TileDto } from '@/types/tile'
import type { CameraRig } from './cameraRig'
import type { PatchManager } from './patchManager'
import type { PlayerController } from './playerController'
import {
  GOD_VIEW_MAX_DISTANCE_METERS,
  GOD_VIEW_MIN_SPEED_MPS,
  GOD_VIEW_PATCH_CEILING_METERS,
  MAX_FRAME_DT_SEC,
  REFETCH_DEPTH_THRESHOLD,
  TURN_RATE_DEG_PER_SEC,
} from './planetSceneConstants'

/**
 * 上帝視角：鏡頭脫離角色自由移動，時間不動（2026-10-23，docs/flight-and-god-view-plan.md）。
 *
 * ## 跟走路／飛行的關係
 *
 * 角色留在原地、不 tick、不送 `MoveTo`、不改 `currentTile`。這裡只移動一個「注視點」：
 * 追尾鏡頭（cameraRig）照常跟著注視點，只是注視點換成這裡的，而不是角色腳下。
 *
 * **高度就是鏡頭距離**：注視點永遠在地面上，升降鍵與滾輪改的都是 cameraRig 的距離。
 * 這樣在 5,000 公里高空，鏡頭仍然往下看著地面上的一點，整顆星球都在畫面裡；
 * 若把注視點本身抬到高空，60° 傾斜的鏡頭會看向太空、星球反而跑出畫面外。
 *
 * ## 速度跟高度成比例
 *
 * 每秒移動約等於目前的鏡頭距離，所以低空能看細節、高空幾秒就能飛到星球另一邊。
 * 這裡沒有跨格偵測（不必一次只跨一格），速度沒有上限。
 *
 * ## 地塊
 *
 * 鏡頭距離低於 `GOD_VIEW_PATCH_CEILING_METERS` 時，地塊跟著注視點補載；更高就只看星球貼圖。
 * 補載會換掉整個 patch，**離開時一定要載回角色周圍**（exit 負責），否則角色站的格子不在 patch 裡、
 * 跨格偵測找不到鄰居，角色會走不動。
 */
export interface GodView {
  readonly active: boolean
  /** 注視點（地面上的場景座標）。 */
  readonly focusPoint: THREE.Vector3
  /** 面向（單位切向量）。 */
  readonly forward: THREE.Vector3
  readonly isMoving: boolean
  /** 從角色目前的位置與面向進入。 */
  enter(): void
  /** 離開：鏡頭回到角色身上，地塊載回角色周圍。回傳的 Promise 在地塊載好後才完成。 */
  exit(): Promise<void>
  setHeld(action: BindableAction, held: boolean): void
  releaseAll(): void
  /** 轉向（滑鼠右鍵拖曳）。正值＝右轉。 */
  turnBy(deltaDeg: number): void
  /** 轉成面向正北（指北針點擊）。 */
  faceNorth(): void
  /** 每幀呼叫。不在上帝視角時什麼都不做。 */
  tick(dtSec: number): void
}

/** 多久重新找一次注視點底下是哪一格（秒）。findNearestTile 掃 5 萬格，每幀做太浪費。 */
const GROUND_LOOKUP_INTERVAL_SEC = 0.2

/** 補載失敗或剛載完時，至少隔這麼久才再載一次（秒），避免連續打後端。 */
const MIN_RELOAD_INTERVAL_SEC = 1

export function createGodView(
  controller: PlayerController,
  patchManager: PatchManager,
  cameraRig: CameraRig,
  getCurrentTile: () => TileDto | null,
  planetRadiusMeters: number,
  meshOptions: TileMeshOptions,
): GodView {
  const sceneToMeters = planetRadiusMeters / meshOptions.renderRadius
  const position = new THREE.Vector3(1, 0, 0)
  const forward = new THREE.Vector3(0, 1, 0)
  const focusPoint = new THREE.Vector3()

  const held = new Set<BindableAction>()
  let active = false
  let isMoving = false
  let turnFromDrag = 0

  /** 地面半徑（場景單位），往注視點底下那一格的地面平滑靠近。 */
  let groundRadius = meshOptions.renderRadius
  let groundTarget = meshOptions.renderRadius
  let sinceGroundLookup = Infinity
  let sinceReload = Infinity

  let loading: Promise<unknown> | null = null
  /** 這一趟上帝視角有沒有換過 patch——沒換過，離開時就不必重新載入。 */
  let movedPatch = false

  // 進入前的鏡頭設定，離開時還原
  let savedMinDistance = 0
  let savedMaxDistance = 0
  let savedDistance = 0

  function axis(positive: BindableAction, negative: BindableAction): number {
    return (held.has(positive) ? 1 : 0) - (held.has(negative) ? 1 : 0)
  }

  function enter() {
    if (active) return
    active = true
    controller.halt()
    position.copy(controller.position)
    forward.copy(controller.forward)
    groundRadius = controller.groundPoint.length()
    groundTarget = groundRadius
    sinceGroundLookup = Infinity
    sinceReload = Infinity
    movedPatch = false

    savedMinDistance = cameraRig.minDistance
    savedMaxDistance = cameraRig.maxDistance
    savedDistance = cameraRig.distance
    // 下限沿用走路時的（受鏡頭 near 限制，見 GOD_VIEW_MAX_DISTANCE_METERS 的說明）
    cameraRig.setZoomLimits(savedMinDistance, GOD_VIEW_MAX_DISTANCE_METERS / sceneToMeters)
    updateFocusPoint()
  }

  async function exit() {
    if (!active) return
    active = false
    held.clear()
    isMoving = false

    cameraRig.setZoomLimits(savedMinDistance, savedMaxDistance)
    cameraRig.setDistance(savedDistance)

    // 還在載的那一批要等它換上去，否則它會在我們載回角色周圍之後才完成、又把 patch 換走
    if (loading) await loading.catch(() => undefined)

    const tile = getCurrentTile()
    const patch = patchManager.patch
    if (movedPatch && tile) {
      const needsReload =
        !patch ||
        !patch.indexByTileId.has(tile.tileId) ||
        edgeDepthOf(patch, tile.tileId) <= REFETCH_DEPTH_THRESHOLD
      if (needsReload) {
        try {
          await patchManager.loadPatch(tile.latitude, tile.longitude)
        } catch {
          /* 載不到就維持現狀；角色一移動，playerController 會自己再補載 */
        }
      }
    }
    controller.reattachToPatch()
  }

  function updateFocusPoint() {
    focusPoint.copy(position).multiplyScalar(groundRadius)
  }

  function startLoad(latitude: number, longitude: number) {
    sinceReload = 0
    movedPatch = true
    const request = patchManager.loadPatch(latitude, longitude)
    loading = request
    request
      .catch(() => undefined)
      .finally(() => {
        if (loading === request) loading = null
      })
  }

  /** 找注視點底下的格子：更新地面高度，必要時補載。 */
  function lookupGround(distanceMeters: number) {
    if (distanceMeters > GOD_VIEW_PATCH_CEILING_METERS) return
    const patch = patchManager.patch
    const { latitude, longitude } = latLonOf(position)

    const nearest = patch ? findNearestTile(patch.tiles, latitude, longitude) : null
    // 最近的格心離得太遠＝注視點已經在 patch 外（findNearestTile 只會回傳 patch 裡最近的一格）
    const limitRad = (2 * (meshOptions.edgeLengthMeters ?? 1000)) / planetRadiusMeters
    const inside =
      nearest !== null &&
      position.angleTo(latLonToVector3(nearest.latitude, nearest.longitude, 1)) < limitRad

    if (inside) groundTarget = tileFloorRadius(nearest, meshOptions)

    const nearEdge = !inside || edgeDepthOf(patch!, nearest.tileId) <= REFETCH_DEPTH_THRESHOLD
    if (nearEdge && !loading && sinceReload >= MIN_RELOAD_INTERVAL_SEC) {
      startLoad(latitude, longitude)
    }
  }

  function tick(rawDt: number) {
    if (!active) return
    const dt = Math.min(Math.max(rawDt, 0), MAX_FRAME_DT_SEC)
    sinceReload += dt

    // 轉向
    const turnDeg = axis('turnRight', 'turnLeft') * TURN_RATE_DEG_PER_SEC * dt + turnFromDrag
    turnFromDrag = 0
    turnInPlace(position, forward, turnDeg)

    // 升降＝改鏡頭距離。跟飛行同一套升降速度（跟高度成比例）
    let distanceMeters = cameraRig.distance * sceneToMeters
    const climb = axis('ascend', 'descend')
    if (climb !== 0) {
      distanceMeters += climb * verticalSpeedAt(distanceMeters) * dt
      cameraRig.setDistance(distanceMeters / sceneToMeters) // 會夾在上帝視角的範圍內
      distanceMeters = cameraRig.distance * sceneToMeters
    }

    // 水平移動：速度＝每秒移動目前的鏡頭距離。不加減速——速度隨高度連續變化，
    // 起步停下的生硬感遠小於走路，加了反而讓高空的大範圍移動拖泥帶水
    let inForward = axis('moveForward', 'moveBack')
    let inRight = axis('strafeRight', 'strafeLeft')
    const inLength = Math.hypot(inForward, inRight)
    if (inLength > 1) {
      inForward /= inLength
      inRight /= inLength
    }
    const speed = Math.max(GOD_VIEW_MIN_SPEED_MPS, distanceMeters)
    isMoving = inForward !== 0 || inRight !== 0 || climb !== 0
    if (inForward !== 0 || inRight !== 0) {
      moveAlongSurface(position, forward, inForward * speed * dt, inRight * speed * dt, planetRadiusMeters)
    }

    sinceGroundLookup += dt
    if (sinceGroundLookup >= GROUND_LOOKUP_INTERVAL_SEC) {
      sinceGroundLookup = 0
      lookupGround(distanceMeters)
    }
    groundRadius += (groundTarget - groundRadius) * (1 - Math.exp(-10 * dt))
    updateFocusPoint()
  }

  return {
    get active() {
      return active
    },
    focusPoint,
    forward,
    get isMoving() {
      return isMoving
    },
    enter,
    exit,
    setHeld(action, isHeld) {
      if (isHeld) held.add(action)
      else held.delete(action)
    },
    releaseAll() {
      held.clear()
    },
    turnBy(deltaDeg) {
      turnFromDrag += deltaDeg
    },
    faceNorth() {
      forwardForHeading(position, 0, forward)
    },
    tick,
  }
}
