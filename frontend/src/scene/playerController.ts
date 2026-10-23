import * as THREE from 'three'
import { requestMoveTo } from '@/network/signalrClient'
import { edgeDepthOf, hasCompleteNeighbors, neighborsOf } from '@/map/tilePatch'
import { latLonToVector3 } from '@/map/sphereCoordinates'
import {
  approach,
  forwardForHeading,
  headingDegOf,
  moveAlongSurface,
  nearestIndex,
  turnInPlace,
} from '@/map/surfaceMotion'
import {
  FLIGHT_TIERS,
  MAX_FLIGHT_ALTITUDE_METERS,
  MIN_FLIGHT_ALTITUDE_METERS,
  TAKEOFF_ALTITUDE_METERS,
  flightAccelerationMps2,
  steppedHoursPerCrossing,
  verticalSpeedAt,
} from '@/map/flightMotion'
import { tileFloorRadius, tileSurfaceRadius, type TileMeshOptions } from '@/map/tileMeshBuilder'
import { useGameStateStore } from '@/stores/gameState'
import type { BindableAction } from '@/stores/keyBindings'
import type { TileDto } from '@/types/tile'
import type { PatchManager } from './patchManager'
import {
  ACCELERATION_MPS2,
  BACK_SPEED_FACTOR,
  DECELERATION_MPS2,
  MAX_FRAME_DT_SEC,
  REFETCH_DEPTH_THRESHOLD,
  RUN_SPEED_MPS,
  STRAFE_SPEED_FACTOR,
  TURN_RATE_DEG_PER_SEC,
  WALK_SPEED_MPS,
} from './planetSceneConstants'

/**
 * 玩家的連續移動：位置、面向、速度、跨格偵測與伺服器驗證。
 *
 * 2026-10-16 取代原本的 playerMovement.ts（六方向跳格）。核心假設的改變是：
 * **玩家不再「站在某一格的中心」，而是在球面上任意一點；「所在格」由位置推導出來。**
 *
 * ## 跨格
 *
 * 每幀只比較「目前格＋它的鄰居」誰的格心最近（Goldberg 格是 Voronoi，最近的格心就是所在格）。
 * 這要求一幀之內不能跨超過一格——所以 dt 有上限（MAX_FRAME_DT_SEC），跑步一幀最多 60 公尺。
 *
 * 跨格之後做的事跟以前「走一步」完全相同：更新 gameState.currentTile、背景向伺服器驗證
 * （`MovementHub.MoveTo` 驗證的是「目標是不是鄰居」，連續移動仍然是一次跨一格，所以不必改）、
 * 移動模式下推進 1 小時、必要時補載 patch。
 *
 * ## 飛行（2026-10-23，docs/flight-and-god-view-plan.md）
 *
 * 飛行只是多了一個高度軸，水平移動與跨格偵測跟走路是同一套：一樣沿大圓、一樣一次跨一格、
 * 一樣每跨一格送一次 `MoveTo`。最高速 3 km/s × 每幀上限 0.1 秒＝300 公尺，仍然小於一格。
 *
 * 高度存成**絕對半徑**（`aircraftRadius`，場景單位），不是「離地多高」：
 * 平飛時越過山脈，飛機維持同一個高度、山從底下經過；只有地面高過「最低離地高度」時才把飛機往上推。
 * 存成離地高度的話，飛機會貼著每一格的高低起伏上下跳。
 *
 * ## 目前還沒有的（規劃的 M3 / M4）
 *
 * - 地形規則：爬坡上限、水、沿牆滑動——現在哪裡都能走
 * - 翻滾閃避
 */
export interface PlayerController {
  /** 腳下地表的場景座標（已含高度平滑）。鏡頭與角色每幀讀這個。 */
  readonly focusPoint: THREE.Vector3
  /** 面向（單位切向量，與位置垂直）。 */
  readonly forward: THREE.Vector3
  /** 這一幀有沒有在移動（給鏡頭決定要不要把環顧偏移回正）。 */
  readonly isMoving: boolean
  /** 正下方地面的場景座標（飛行時角色往下畫的那條線的終點；走路時等於 focusPoint）。 */
  readonly groundPoint: THREE.Vector3
  /** 是否在飛行中（含降落中）。 */
  readonly isFlying: boolean
  /** 地面按：起飛。空中按：開始降落（再按一次取消降落）。 */
  toggleFlight(): void
  /** 選速度檔（FLIGHT_TIERS 的索引）。只在飛行中有效。 */
  setFlightTier(index: number): void
  /** 把玩家放到某一格的中心（初次載入、伺服器拒絕時退回）。面向沿用 gameState.facingDeg。 */
  placeAtTile(tile: TileDto): void
  /** 某個動作的按鍵被按下／放開。只處理按住生效的動作（見 keyBindings.HELD_ACTIONS）。 */
  setHeld(action: BindableAction, held: boolean): void
  /** 跑步（Shift）。 */
  setRunning(running: boolean): void
  /** 視窗失去焦點時呼叫：把所有按住的狀態清掉，否則放開按鍵的事件收不到、角色會一直走。 */
  releaseAll(): void
  /** 轉身（滑鼠右鍵拖曳用）。正值＝右轉。 */
  turnBy(deltaDeg: number): void
  /** 轉成面向正北（指北針點擊）。 */
  faceNorth(): void
  /** 每幀呼叫。 */
  tick(dtSec: number): void
}

/**
 * @param planetRadiusMeters 星球半徑，公尺換算成弧度用。
 * @param meshOptions 地表高度換算用，跟地形 mesh 同一份——角色才會剛好站在格子頂面上。
 * @param edgeLengthMeters 細格邊長（公尺）。鄰居資料不齊時「最多能偏離格心多遠」用它換算。
 * @param setMoveWarning 回報移動相關的提示（被伺服器拒絕、離線未驗證），交給呼叫端顯示。
 */
export function createPlayerController(
  gameState: ReturnType<typeof useGameStateStore>,
  patchManager: PatchManager,
  planetRadiusMeters: number,
  meshOptions: TileMeshOptions,
  edgeLengthMeters: number,
  setMoveWarning: (message: string | null) => void,
): PlayerController {
  const position = new THREE.Vector3(1, 0, 0) // 單位向量
  const forward = new THREE.Vector3(0, 1, 0)
  const focusPoint = new THREE.Vector3()
  const groundPoint = new THREE.Vector3()
  const sceneToMeters = planetRadiusMeters / meshOptions.renderRadius

  // 本地座標的速度（公尺／秒）：沿面向、沿右方
  let forwardSpeed = 0
  let rightSpeed = 0
  let turnFromDrag = 0

  const held = new Set<BindableAction>()
  let running = false
  let isMoving = false

  // 飛行
  let flying = false
  let landing = false
  /** 剛起飛、還沒爬到起飛高度：自動爬升，玩家按了升降鍵就交還控制。 */
  let autoClimbing = false
  let tierIndex = 0
  /** 飛機的絕對半徑（場景單位）。見檔頭「飛行」。 */
  let aircraftRadius = meshOptions.renderRadius
  /** 飛行時的地面半徑（水面或格子頂面），跟 displayedRadius 一樣往目標平滑靠近。 */
  let floorRadius = meshOptions.renderRadius

  /** 顯示用的地表半徑（場景單位），往目前格的頂面平滑靠近——跨到不同高度的格子時才不會瞬移。 */
  let displayedRadius = meshOptions.renderRadius

  let isFetchingPatch = false
  // 每次退回就+1，讓還在路上的舊驗證結果失效（避免過期的回應把玩家彈到別的地方）。
  let moveGeneration = 0

  // 格心的單位向量快取。每幀都要比對 7 個格心，不快取的話每幀要配置 7 個新向量。
  const centerCache = new WeakMap<TileDto, THREE.Vector3>()
  function centerOf(tile: TileDto): THREE.Vector3 {
    let center = centerCache.get(tile)
    if (!center) {
      center = latLonToVector3(tile.latitude, tile.longitude, 1)
      centerCache.set(tile, center)
    }
    return center
  }

  function placeAtTile(tile: TileDto) {
    position.copy(centerOf(tile))
    forwardForHeading(position, gameState.facingDeg, forward)
    forwardSpeed = 0
    rightSpeed = 0
    displayedRadius = tileSurfaceRadius(tile, meshOptions)
    enterTile(tile)
    updateFocusPoint()
  }

  /** 成為「目前所在格」：更新狀態與外框。不做伺服器驗證——那只在真的跨格時做。 */
  function enterTile(tile: TileDto) {
    gameState.currentTile = tile
    patchManager.highlightNeighbors([tile])
  }

  function updateFocusPoint() {
    if (flying) {
      focusPoint.copy(position).multiplyScalar(aircraftRadius)
      groundPoint.copy(position).multiplyScalar(floorRadius)
    } else {
      focusPoint.copy(position).multiplyScalar(displayedRadius)
      groundPoint.copy(focusPoint)
    }
  }

  function toggleFlight() {
    const tile = gameState.currentTile
    if (!tile) return
    if (!flying) {
      flying = true
      landing = false
      autoClimbing = true
      floorRadius = tileFloorRadius(tile, meshOptions)
      // 從水底起飛時直接浮到水面（飛行的地面是水面，見 tileFloorRadius）
      aircraftRadius = Math.max(displayedRadius, floorRadius)
      return
    }
    landing = !landing
    autoClimbing = false
  }

  function land() {
    flying = false
    landing = false
    autoClimbing = false
    // 從飛機的高度平滑落到格子頂面（在海上會沉到水底——走路還沒有水的規則，見 M3）
    displayedRadius = aircraftRadius
    // 落地後不能維持 3 km/s 在地上滑行
    forwardSpeed = THREE.MathUtils.clamp(forwardSpeed, -RUN_SPEED_MPS, RUN_SPEED_MPS)
    rightSpeed = THREE.MathUtils.clamp(rightSpeed, -RUN_SPEED_MPS, RUN_SPEED_MPS)
  }

  /** 每幀的高度更新。只在飛行中呼叫。 */
  function updateAltitude(tile: TileDto | null, dt: number) {
    if (tile) {
      const target = tileFloorRadius(tile, meshOptions)
      floorRadius += (target - floorRadius) * (1 - Math.exp(-20 * dt))
    }

    let altitude = (aircraftRadius - floorRadius) * sceneToMeters
    const climbInput = axis('ascend', 'descend')
    if (climbInput !== 0) autoClimbing = false
    if (climbInput > 0) landing = false // 降落中按上升＝取消降落

    let vertical = climbInput
    if (landing) vertical = -1
    else if (autoClimbing) {
      if (altitude >= TAKEOFF_ALTITUDE_METERS) autoClimbing = false
      else vertical = 1
    }

    altitude += vertical * verticalSpeedAt(altitude) * dt
    if (autoClimbing) altitude = Math.min(altitude, TAKEOFF_ALTITUDE_METERS)
    // 降落與起飛爬升期間可以低於最低高度；平飛時地面一高過最低高度就把飛機往上推
    const floor = landing || autoClimbing ? 0 : MIN_FLIGHT_ALTITUDE_METERS
    altitude = THREE.MathUtils.clamp(altitude, floor, MAX_FLIGHT_ALTITUDE_METERS)
    aircraftRadius = floorRadius + altitude / sceneToMeters

    if (landing && altitude <= 0) land()
  }

  /** 寫回 HUD 的副本。量化過、變了才換新物件，避免 HUD 每幀重繪。 */
  function publishFlightState() {
    const speed = Math.round(Math.hypot(forwardSpeed, rightSpeed) / 10) * 10
    const altitude = flying
      ? Math.round(((aircraftRadius - floorRadius) * sceneToMeters) / 10) * 10
      : 0
    const current = gameState.flight
    if (
      current.isFlying !== flying ||
      current.isLanding !== landing ||
      current.tierIndex !== tierIndex ||
      current.speedMps !== speed ||
      current.altitudeMeters !== altitude
    ) {
      gameState.flight = {
        isFlying: flying,
        isLanding: landing,
        tierIndex,
        speedMps: speed,
        altitudeMeters: altitude,
      }
    }
  }

  /**
   * 背景向伺服器確認這次跨格合法。單人情境下幾乎不可能被拒——跨格偵測本來就只看伺服器
   * 算出來的鄰居——所以真的被拒代表鄰接資料有問題，這裡等於最高風險改動的回歸偵測器。
   */
  function validateCrossingInBackground(from: TileDto, to: TileDto) {
    const generation = moveGeneration
    requestMoveTo(from.tileId, to.tileId)
      .then((confirmed) => {
        if (generation !== moveGeneration) return
        if (confirmed) return

        moveGeneration++
        setMoveWarning('移動被伺服器拒絕，已退回上一格')
        const patch = patchManager.patch
        const restored = patch?.indexByTileId.has(from.tileId)
          ? patch.tiles[patch.indexByTileId.get(from.tileId)!]!
          : from
        placeAtTile(restored)
      })
      .catch(() => {
        // 連不上伺服器 ≠ 這步不合法，不可以因此退回（否則離線時玩家會被彈來彈去）。
        if (generation !== moveGeneration) return
        setMoveWarning('離線中，移動尚未經伺服器驗證')
      })
  }

  function onCrossed(from: TileDto, to: TileDto) {
    enterTile(to)
    if (gameState.mode === 'stepped') {
      gameState.advanceTime(
        steppedHoursPerCrossing(flying, Math.hypot(forwardSpeed, rightSpeed), WALK_SPEED_MPS),
      )
    }
    void ensurePatchAroundPlayer()
    validateCrossingInBackground(from, to)
  }

  /**
   * 依目前位置更新所在格。回傳 false 代表「走出了已知範圍」——鄰居資料不齊、
   * 而位置已經離開目前格太遠，呼叫端要把這一幀的移動撤銷。
   */
  function resolveCurrentTile(): boolean {
    const patch = patchManager.patch
    const start = gameState.currentTile
    if (!patch || !start) return true
    let current: TileDto = start

    // 一幀最多跨一格（dt 有上限），保險起見最多連跨三次
    for (let i = 0; i < 3; i++) {
      const neighbors = neighborsOf(patch, current.tileId)
      const candidates: TileDto[] = [current, ...neighbors]
      const best = nearestIndex(
        position,
        candidates.map((t) => centerOf(t)),
      )
      if (best <= 0) {
        // 還在目前這一格。若鄰居資料不齊（patch 邊緣、或後端沒回 neighborIds），
        // 少掉的那個鄰居可能才是最近的——走得太遠就擋下來，等 patch 補載完再說。
        if (!hasCompleteNeighbors(patch, current.tileId)) {
          const strayRad = position.angleTo(centerOf(current))
          const limitRad = (edgeLengthMeters * 0.9) / planetRadiusMeters
          if (strayRad > limitRad) return false
        }
        return true
      }
      const next: TileDto = candidates[best]!
      onCrossed(current, next)
      current = next
    }
    return true
  }

  async function ensurePatchAroundPlayer() {
    const patch = patchManager.patch
    const tile = gameState.currentTile
    if (!patch || !tile || isFetchingPatch) return
    if (edgeDepthOf(patch, tile.tileId) > REFETCH_DEPTH_THRESHOLD) return

    isFetchingPatch = true
    try {
      await patchManager.loadPatch(tile.latitude, tile.longitude)
      // 換完 patch 後，所在格要改用新 patch 裡的物件（同一個 tileId，但帶的是新的鄰接資料）。
      // 注意要用「當下」的所在格——補載期間玩家可能已經又走了好幾格。
      const freshPatch = patchManager.patch
      const nowTile = gameState.currentTile
      const index = nowTile ? freshPatch?.indexByTileId.get(nowTile.tileId) : undefined
      if (index !== undefined) enterTile(freshPatch!.tiles[index]!)
    } catch {
      /* 補載失敗就維持用舊 patch，下一次跨格會再試 */
    } finally {
      isFetchingPatch = false
    }
  }

  function axis(positive: BindableAction, negative: BindableAction): number {
    return (held.has(positive) ? 1 : 0) - (held.has(negative) ? 1 : 0)
  }

  const previousPosition = new THREE.Vector3()
  const previousForward = new THREE.Vector3()

  function tick(rawDt: number) {
    const dt = Math.min(Math.max(rawDt, 0), MAX_FRAME_DT_SEC)

    // 1. 轉向：鍵盤按住＋滑鼠拖曳累積的量
    const turnDeg = axis('turnRight', 'turnLeft') * TURN_RATE_DEG_PER_SEC * dt + turnFromDrag
    turnFromDrag = 0
    turnInPlace(position, forward, turnDeg)

    // 2. 目標速度。斜向（前＋右）要先正規化，否則斜走會是 √2 倍快
    let inForward = axis('moveForward', 'moveBack')
    let inRight = axis('strafeRight', 'strafeLeft')
    const inLength = Math.hypot(inForward, inRight)
    if (inLength > 1) {
      inForward /= inLength
      inRight /= inLength
    }
    // 飛行時速度只由檔位決定，Shift 沒有作用
    const top = flying
      ? FLIGHT_TIERS[tierIndex]!.speedMps
      : running
        ? RUN_SPEED_MPS
        : WALK_SPEED_MPS
    const targetForward = inForward * top * (inForward < 0 ? BACK_SPEED_FACTOR : 1)
    const targetRight = inRight * top * STRAFE_SPEED_FACTOR

    // 3. 加減速：往目標速度靠近。方向跟目前速度相同就是加速，否則是減速。
    //    飛行的加速度跟速度成比例（見 flightAccelerationMps2），走路是固定值
    const rateFor = (current: number, target: number) => {
      const accelerating =
        Math.abs(target) > Math.abs(current) && Math.sign(target) !== -Math.sign(current)
      if (flying) return flightAccelerationMps2(top, current, !accelerating)
      return accelerating ? ACCELERATION_MPS2 : DECELERATION_MPS2
    }
    forwardSpeed = approach(forwardSpeed, targetForward, rateFor(forwardSpeed, targetForward) * dt)
    rightSpeed = approach(rightSpeed, targetRight, rateFor(rightSpeed, targetRight) * dt)

    // 4. 位移（沿大圓）＋跨格偵測；走出已知範圍就撤銷這一幀的位移
    isMoving = forwardSpeed !== 0 || rightSpeed !== 0
    if (isMoving) {
      previousPosition.copy(position)
      previousForward.copy(forward)
      moveAlongSurface(position, forward, forwardSpeed * dt, rightSpeed * dt, planetRadiusMeters)
      if (!resolveCurrentTile()) {
        position.copy(previousPosition)
        forward.copy(previousForward)
        forwardSpeed = 0
        rightSpeed = 0
        isMoving = false
      }
    }

    // 5. 高度。走路：站立高度往目前格的頂面平滑靠近（約 0.15 秒收斂）。飛行：升降與地形下限
    const tile = gameState.currentTile
    if (flying) {
      updateAltitude(tile, dt)
    } else if (tile) {
      const targetRadius = tileSurfaceRadius(tile, meshOptions)
      displayedRadius += (targetRadius - displayedRadius) * (1 - Math.exp(-20 * dt))
    }
    updateFocusPoint()
    publishFlightState()

    // 6. 面向寫回 gameState（給 HUD 與存檔）。量化到 0.5°：這是每幀都跑的程式碼，
    //    每次寫入都會讓讀它的元件重繪
    const heading = Math.round(headingDegOf(position, forward) * 2) / 2
    if (heading !== gameState.facingDeg) gameState.setFacing(heading)
  }

  return {
    focusPoint,
    forward,
    get isMoving() {
      return isMoving
    },
    groundPoint,
    get isFlying() {
      return flying
    },
    toggleFlight,
    setFlightTier(index) {
      if (!flying || index < 0 || index >= FLIGHT_TIERS.length) return
      tierIndex = index
    },
    placeAtTile,
    setHeld(action, isHeld) {
      if (isHeld) held.add(action)
      else held.delete(action)
    },
    setRunning(value) {
      running = value
    },
    releaseAll() {
      held.clear()
      running = false
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
