import * as THREE from 'three'
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js'
import { tileSurfaceRadius, type TileMeshOptions } from '@/map/tileMeshBuilder'
import { latLonToVector3, tangentBasisAt } from '@/map/sphereCoordinates'
import { useGameStateStore } from '@/stores/gameState'
import type { TileDto } from '@/types/tile'
import {
  DEFAULT_CAMERA_DISTANCE_METERS,
  DEFAULT_TILT_DEG,
  FACING_TURN_DURATION_MS,
  RENDER_RADIUS,
} from './planetSceneConstants'

/**
 * 從 fromDeg（可能是任意未取模、一路累加/遞減下去的角度）到「已正規化的目標角度」
 * toNormalizedDeg 的最短路徑signed差值，範圍(-180,180]。
 *
 * 用途：轉向動畫要接續「目前顯示中的角度」而不是每次都回到0~360正規化空間重新起跳，
 * 否則從300°右轉到0°（正規化後的0，代表360）會被誤判成「倒退300度」而反方向繞一大圈。
 * 先在正規化空間算出最短路徑的delta，再加回未取模的顯示角度上，方向才會永遠正確。
 *
 * 只有輸入剛好差180度時方向是任意的（+180跟-180是同一個角度，兩個方向轉過去視覺上一樣），
 * 這裡固定回傳-180；六角格每次只轉60度，只有連續3次同方向轉向被漏判成一次時才會撞到這個邊界，
 * 屬於可接受的極端情況。
 */
export function shortestFacingDelta(fromDeg: number, toNormalizedDeg: number): number {
  const fromNormalized = ((fromDeg % 360) + 360) % 360
  return ((((toNormalizedDeg - fromNormalized + 180) % 360) + 360) % 360) - 180
}

/**
 * smoothstep 緩動（3t²-2t³）：兩端速度都是0，看起來像「轉過去」而不是「跳過去再煞車」。
 * 比線性內插自然——線性內插在動畫開始/結束的瞬間會有速度的不連續。
 */
export function smoothstep(t: number): number {
  return t * t * (3 - 2 * t)
}

export interface CameraRig {
  /** 目前的 OrbitControls 實例。up軸每次變動就會整個重建（見 applyTopDownView 內的說明），所以用 getter 而不是固定物件。 */
  readonly controls: OrbitControls
  /** 復位視角：回到目前所在格正上方、正北朝上的俯視角，縮放距離維持使用者目前的值（指北針點擊用）。 */
  resetView(): void
  /** 完全回到預設視角：傾斜角與鏡頭距離都重設（Home鍵）。 */
  resetToDefaultView(): void
  /** 設定玩家目前所在格時鏡頭該怎麼跟：recenter（重新定向）或平移跟隨。 */
  followPlayerTile(tile: TileDto, opts: { recenter: boolean; distance?: number }): void
  /** 設定 controls 的縮放範圍。patch 初次載入完成、鏡頭要從「整顆星球」尺度換成「單一patch」尺度時用。 */
  setZoomLimits(minDistance: number, maxDistance: number): void
  /** 依目前的 gameState.facingDeg 開始一次轉向動畫（設定起訖角度），實際內插由 tickFacingAnimation 逐幀處理。 */
  beginFacingTurn(): void
  /** 每幀呼叫：若轉向動畫進行中，推進內插進度並重新定位鏡頭；否則不做任何事。 */
  tickFacingAnimation(): void
  dispose(): void
}

/**
 * 建立管理鏡頭視角的控制器：OrbitControls 的生命週期、面向朝上的俯視角定位、轉向動畫。
 *
 * @param camera 場景的相機（由呼叫端建立、擁有）。
 * @param domElement OrbitControls 綁定事件用的DOM元素（renderer.domElement）。
 * @param meshOptions 尺寸換算設定，跟地形mesh用同一份，焦點才會落在腳下格子的地表而不是海平面。
 * @param edgeLengthScene 一格邊長（場景單位），距離的保底/最小值換算會用到。
 * @param sceneToMeters 場景單位換算成星球實際比例公尺數的倍率，resetToDefaultView 換算固定的
 *   「預設鏡頭距離（公尺）」時要用。
 * @param gameState 讀 facingDeg／currentTile，寫 compassAngleDeg 之類的不在這裡（那是 hudStats 的事）。
 */
export function createCameraRig(
  camera: THREE.PerspectiveCamera,
  domElement: HTMLElement,
  meshOptions: TileMeshOptions,
  edgeLengthScene: number,
  sceneToMeters: number,
  gameState: ReturnType<typeof useGameStateStore>,
): CameraRig {
  // 初始的「整顆星球」縮放範圍。真正定位到某個Tile之前（loadPatch完成前）controls必須先存在，
  // 之後第一次 applyTopDownView 會把這個範圍替換成 patch 尺度的範圍（見 setZoomLimits 的呼叫時機——
  // 必須搶在第一次 followPlayerTile 之前，否則 controls.update() 的 clamp 會用這組舊範圍
  // 把算好的近距離鏡頭夾回去）。
  let controls = new OrbitControls(camera, domElement)
  controls.enableDamping = true
  controls.minDistance = RENDER_RADIUS * 1.05
  controls.maxDistance = RENDER_RADIUS * 6

  let lastFocusPoint: THREE.Vector3 | null = null

  // 轉向動畫狀態。displayedFacingDeg 是「目前鏡頭實際畫出來的角度」，刻意不做360取模
  // （可以一路累加/遞減下去），這樣連續右轉三次可以是 0→60→120→180 而不是繞回 0 附近，
  // 動畫才不會在跨過0/360接縫時反方向轉一大圈。跟 gameState.facingDeg（永遠是0~300的正規化值，
  // 存檔用）是兩個獨立的表示法，只有動畫觸發的當下才會互相換算一次。
  //
  // 讀存檔可能帶著非0的面向（上次玩到轉過向），顯示角度要從那裡接著算，不然重新整理後
  // 第一次轉向會從0開始，動畫跟畫面上實際看到的角度對不上。
  let displayedFacingDeg = gameState.facingDeg
  let turnAnimFromDeg = 0
  let turnAnimToDeg = 0
  let turnAnimStartAt = 0
  let turnAnimActive = false

  /**
   * 鏡頭的注視點：玩家腳下那一格的**地表**（含誇張後的海拔），不是海平面。
   *
   * 原本用的是海平面的點，在接近正上方俯視時看不出問題（偏移量剛好沿著視線方向），
   * 但傾斜角一大，那個偏移就變成垂直於視線，地形會整片被推出畫面外——
   * 海拔249級在誇張8倍後是將近10公里的偏移，對20公里的鏡頭距離來說非常巨大。
   * 順帶也修掉「在高山上拉近時鏡頭會鑽進地形裡」的問題。
   */
  function focusPointFor(tile: TileDto): THREE.Vector3 {
    return latLonToVector3(tile.latitude, tile.longitude, tileSurfaceRadius(tile, meshOptions))
  }

  /**
   * 面向朝上的預設俯視角：鏡頭在focusPoint上方、往「面向的反方向」偏斜DEFAULT_TILT_DEG度
   * （不是純垂直往下看），camera.up=玩家面向的切線方向。偏斜角讓畫面更有立體感、看得到地形起伏，
   * 同時螢幕上方永遠是玩家正在面對的方向。
   *
   * 面向0度（正北）時這支的行為跟加入轉向功能之前完全相同——面向只是把原本寫死的「正北」
   * 換成「正北繞著地表法線轉 facingDeg 度」。
   *
   * OrbitControls在建構子當下就把「camera.up→世界+Y」的轉換quaternion算好存起來，之後改camera.up
   * 它不會跟著重算（每個Tile的當地north都不一樣，等於每次都要換一次up軸）——結果是移動/縮放/復位視角時
   * 左右、上下會對不起來（實測發現「D應該往右卻感覺往左」）。修法：up軸每次變動，就整個重建OrbitControls，
   * 讓它的建構子用「新的」camera.up重新算一次轉換quaternion。
   *
   * 轉向動畫期間這支會被每幀呼叫、帶著中間角度（facingDegOverride），所以會連帶重建
   * OrbitControls 好幾次（220ms動畫約13~14幀）——這不是額外的風險，只是把「單次重建已驗證
   * 沒問題」的既有模式重複套用而已；重建本身很輕（主要是註冊事件監聽），這個頻率下可忽略。
   * @param facingDegOverride 不傳的話用 gameState.facingDeg（一般情況／復位視角用）；
   *   轉向動畫進行中會傳入尚未定案的中間角度，讓鏡頭跟著平滑轉過去。
   */
  function applyTopDownView(
    focusPoint: THREE.Vector3,
    distance: number,
    facingDegOverride?: number,
  ) {
    const { normal, north, east } = tangentBasisAt(focusPoint)
    // 面向方向＝從正北往順時針轉 facingDeg 度的切線方向。方位角的定義就是「0=北、90=東、順時針」，
    // 所以直接用 north·cos + east·sin，不必動用四元數（那反而容易把旋轉方向的正負號弄反）。
    // east 確實是地理東方（已用「沿 east 走一小步後回推方位角＝90°」在四個不同緯經度實測驗證）。
    const facingRad = ((facingDegOverride ?? gameState.facingDeg) * Math.PI) / 180
    const facing = north
      .clone()
      .multiplyScalar(Math.cos(facingRad))
      .addScaledVector(east, Math.sin(facingRad))
      .normalize()
    const tiltRad = (DEFAULT_TILT_DEG * Math.PI) / 180
    const viewDir = normal
      .clone()
      .multiplyScalar(Math.cos(tiltRad))
      .addScaledVector(facing, -Math.sin(tiltRad))
      .normalize()
    camera.up.copy(facing)
    camera.position.copy(focusPoint).addScaledVector(viewDir, distance)

    const savedEnableDamping = controls.enableDamping
    const savedMinDistance = controls.minDistance
    const savedMaxDistance = controls.maxDistance
    controls.dispose()
    controls = new OrbitControls(camera, domElement)
    controls.enableDamping = savedEnableDamping
    controls.minDistance = savedMinDistance
    controls.maxDistance = savedMaxDistance

    controls.target.copy(focusPoint)
    controls.update()
  }

  function resetView() {
    if (!gameState.currentTile) return
    const focusPoint = focusPointFor(gameState.currentTile)
    const distance = camera.position.distanceTo(controls.target) || edgeLengthScene * 6
    applyTopDownView(focusPoint, distance)
  }

  function resetToDefaultView() {
    if (!gameState.currentTile) return
    applyTopDownView(
      focusPointFor(gameState.currentTile),
      DEFAULT_CAMERA_DISTANCE_METERS / sceneToMeters,
    )
  }

  function followPlayerTile(tile: TileDto, opts: { recenter: boolean; distance?: number }) {
    const focusPoint = focusPointFor(tile)
    if (opts.recenter || !lastFocusPoint) {
      // distance沒指定時沿用目前的縮放距離（復位視角的情境）；
      // 初次載入必須明確指定，否則會沿用「看整顆星球」的初始鏡頭距離，patch只會是畫面中的一個小點。
      const distance = opts.distance ?? camera.position.distanceTo(controls.target) ?? 0
      applyTopDownView(focusPoint, distance || edgeLengthScene * 6)
    } else {
      // 移動時只平移鏡頭跟著玩家，不改變目前的視角方向/縮放——座標方向維持不變，玩家手動轉的角度也不會被打斷。
      // 相鄰格之間的角距離極小（約0.03度），球面切平面幾乎沒有旋轉，用平移取代重新定向完全足夠。
      const delta = focusPoint.clone().sub(lastFocusPoint)
      camera.position.add(delta)
      controls.target.copy(focusPoint)
      controls.update()
    }
    lastFocusPoint = focusPoint.clone()
  }

  function setZoomLimits(minDistance: number, maxDistance: number) {
    controls.minDistance = minDistance
    controls.maxDistance = maxDistance
  }

  function beginFacingTurn() {
    // 用「目前顯示中的角度」（可能還在動畫途中）當起點，取最短路徑接到新的目標角度，
    // 並保留原本的角度累加（不取360模），跨過0/360接縫時才不會反方向繞一大圈。
    const shortestDelta = shortestFacingDelta(displayedFacingDeg, gameState.facingDeg)
    turnAnimFromDeg = displayedFacingDeg
    turnAnimToDeg = displayedFacingDeg + shortestDelta
    turnAnimStartAt = performance.now()
    turnAnimActive = true
  }

  function tickFacingAnimation() {
    if (!turnAnimActive) return

    const tile = gameState.currentTile
    const elapsed = performance.now() - turnAnimStartAt
    const t = Math.min(elapsed / FACING_TURN_DURATION_MS, 1)
    displayedFacingDeg = turnAnimFromDeg + (turnAnimToDeg - turnAnimFromDeg) * smoothstep(t)
    if (t >= 1) {
      turnAnimActive = false
      // 動畫結束後把累加值收斂回目標附近（差值收斂到±180度以內），角度本身不變
      // （cos/sin是週期函式），純粹避免長時間遊玩、轉向次數極多時這個數字無限增長。
      displayedFacingDeg =
        gameState.facingDeg +
        ((((displayedFacingDeg - gameState.facingDeg + 180) % 360) + 360) % 360) -
        180
    }

    if (tile) {
      const distance = camera.position.distanceTo(controls.target) || edgeLengthScene * 6
      applyTopDownView(focusPointFor(tile), distance, displayedFacingDeg)
    }
  }

  function dispose() {
    controls.dispose()
  }

  return {
    get controls() {
      return controls
    },
    resetView,
    resetToDefaultView,
    followPlayerTile,
    setZoomLimits,
    beginFacingTurn,
    tickFacingAnimation,
    dispose,
  }
}
