import * as THREE from 'three'
import { DEFAULT_TILT_DEG } from './planetSceneConstants'

/**
 * 走動視角的追尾鏡頭：跟在玩家後上方，畫面上方永遠是玩家面向（加上環顧的偏移）。
 *
 * ## 為什麼不再用 OrbitControls
 *
 * 跳格時代面向只有六個值，每次轉向重建一次 OrbitControls（它在建構子裡就把 camera.up 換算成
 * 內部 quaternion，之後改 up 不會跟著重算，只能整個重建）。一次轉向動畫約重建 13 次，還算可以。
 * 連續轉向等於**每一幀**都在換 up 軸，每幀重建一個會註冊事件監聽的控制器不是可以接受的做法，
 * 而且重建會把使用者正在拖曳的狀態整個丟掉。
 *
 * 追尾鏡頭要的東西其實很少：一個距離、一個傾斜角、一個環顧偏移，全部自己算比較直接。
 *
 * ## 操作
 *
 * - 滾輪：縮放
 * - 左鍵拖曳：環顧（左右看，不轉身）與傾斜；一開始移動，環顧偏移會自己回正
 * - 右鍵拖曳：**轉身**（左右）與傾斜——交給 onTurnDrag，由 playerController 真的轉動玩家
 */
export interface CameraRig {
  /** 鏡頭注視點（玩家腳下的地表）。hudStats 用它算指北針、傾斜與距離。 */
  readonly target: THREE.Vector3
  readonly minDistance: number
  readonly maxDistance: number
  /** 目前鏡頭到注視點的距離（場景單位）。 */
  readonly distance: number
  /** 設定縮放範圍。patch 初次載入、鏡頭從「整顆星球」尺度換成「玩家附近」尺度時呼叫。 */
  setZoomLimits(minDistance: number, maxDistance: number): void
  /** 直接設定鏡頭距離（場景單位），會夾在縮放範圍內。 */
  setDistance(distance: number): void
  /**
   * 每幀呼叫：依玩家位置與面向擺放鏡頭。
   * @param focus 玩家腳下地表的場景座標。
   * @param forward 玩家面向（單位切向量）。
   * @param isMoving 玩家正在移動時，環顧偏移會逐漸回正，鏡頭重新對準前方。
   * @param dt 距上一幀幾秒。
   */
  update(focus: THREE.Vector3, forward: THREE.Vector3, isMoving: boolean, dt: number): void
  /** 環顧偏移歸零（鏡頭回到玩家正後方）。 */
  resetView(): void
  /** 完全回到預設：環顧歸零、傾斜與距離回預設值。 */
  resetToDefaultView(): void
  dispose(): void
}

/** 鏡頭傾斜的範圍（0=正上方俯視）。上限不能到 90：鏡頭會跟 up 軸平行，lookAt 退化。 */
const MIN_TILT_DEG = 0
const MAX_TILT_DEG = 85

/** 每拖曳一個像素轉幾度。 */
const DRAG_DEG_PER_PIXEL = 0.25

/** 滾輪縮放的靈敏度：距離乘上 exp(deltaY × 這個值)。指數縮放在遠近兩端的手感才一致。 */
const WHEEL_ZOOM_RATE = 0.001

/** 移動時環顧偏移回正的速度（每秒衰減的比例常數）。 */
const LOOK_RECENTER_RATE = 3

/**
 * @param camera 場景的相機（由呼叫端建立、擁有）。
 * @param domElement 綁定滑鼠事件的元素（renderer.domElement）。
 * @param defaultDistance resetToDefaultView 回到的距離（場景單位）。
 * @param onTurnDrag 右鍵拖曳時呼叫，參數是要轉的角度（正值＝右轉）。
 */
export function createCameraRig(
  camera: THREE.PerspectiveCamera,
  domElement: HTMLElement,
  defaultDistance: number,
  onTurnDrag: (deltaDeg: number) => void,
): CameraRig {
  const target = new THREE.Vector3()
  let minDistance = defaultDistance * 0.05
  let maxDistance = defaultDistance * 20
  let distance = defaultDistance
  let tiltDeg = DEFAULT_TILT_DEG
  let lookOffsetDeg = 0

  let dragButton: number | null = null
  let lastPointerX = 0
  let lastPointerY = 0

  // 每幀重複使用的暫存向量
  const normal = new THREE.Vector3()
  const lookDir = new THREE.Vector3()
  const viewDir = new THREE.Vector3()

  function clampDistance(d: number) {
    return THREE.MathUtils.clamp(d, minDistance, maxDistance)
  }

  function onPointerDown(event: PointerEvent) {
    if (event.button !== 0 && event.button !== 2) return
    dragButton = event.button
    lastPointerX = event.clientX
    lastPointerY = event.clientY
    domElement.setPointerCapture(event.pointerId)
  }

  function onPointerMove(event: PointerEvent) {
    if (dragButton === null) return
    const dx = event.clientX - lastPointerX
    const dy = event.clientY - lastPointerY
    lastPointerX = event.clientX
    lastPointerY = event.clientY

    // 往上拖＝看向地平線、往下拖＝看向腳下，跟原本 OrbitControls 的方向一致
    tiltDeg = THREE.MathUtils.clamp(tiltDeg - dy * DRAG_DEG_PER_PIXEL, MIN_TILT_DEG, MAX_TILT_DEG)

    if (dragButton === 2) onTurnDrag(dx * DRAG_DEG_PER_PIXEL)
    else lookOffsetDeg += dx * DRAG_DEG_PER_PIXEL
  }

  function onPointerUp(event: PointerEvent) {
    if (event.button !== dragButton) return
    dragButton = null
    if (domElement.hasPointerCapture(event.pointerId)) {
      domElement.releasePointerCapture(event.pointerId)
    }
  }

  function onWheel(event: WheelEvent) {
    event.preventDefault() // 不要讓頁面跟著捲動
    distance = clampDistance(distance * Math.exp(event.deltaY * WHEEL_ZOOM_RATE))
  }

  // 右鍵拖曳要轉身，不能跳出瀏覽器的右鍵選單
  function onContextMenu(event: MouseEvent) {
    event.preventDefault()
  }

  domElement.addEventListener('pointerdown', onPointerDown)
  domElement.addEventListener('pointermove', onPointerMove)
  domElement.addEventListener('pointerup', onPointerUp)
  domElement.addEventListener('pointercancel', onPointerUp)
  domElement.addEventListener('wheel', onWheel, { passive: false })
  domElement.addEventListener('contextmenu', onContextMenu)

  function update(focus: THREE.Vector3, forward: THREE.Vector3, isMoving: boolean, dt: number) {
    target.copy(focus)

    // 一開始移動就讓環顧偏移回正——邊走邊看旁邊的話，鏡頭不回來會讓人分不清前方在哪
    if (isMoving && lookOffsetDeg !== 0 && dragButton !== 0) {
      lookOffsetDeg *= Math.exp(-LOOK_RECENTER_RATE * dt)
      if (Math.abs(lookOffsetDeg) < 0.01) lookOffsetDeg = 0
    }

    normal.copy(focus).normalize()
    lookDir.copy(forward).applyAxisAngle(normal, (-lookOffsetDeg * Math.PI) / 180)

    // 鏡頭在注視點上方、往「看的方向」的反方向偏斜 tiltDeg 度
    const tiltRad = (tiltDeg * Math.PI) / 180
    viewDir
      .copy(normal)
      .multiplyScalar(Math.cos(tiltRad))
      .addScaledVector(lookDir, -Math.sin(tiltRad))
      .normalize()

    camera.position.copy(focus).addScaledVector(viewDir, distance)
    camera.up.copy(lookDir) // 畫面上方＝看的方向
    camera.lookAt(focus)
  }

  return {
    target,
    get minDistance() {
      return minDistance
    },
    get maxDistance() {
      return maxDistance
    },
    get distance() {
      return distance
    },
    setZoomLimits(min: number, max: number) {
      minDistance = min
      maxDistance = max
      distance = clampDistance(distance)
    },
    setDistance(d: number) {
      distance = clampDistance(d)
    },
    update,
    resetView() {
      lookOffsetDeg = 0
    },
    resetToDefaultView() {
      lookOffsetDeg = 0
      tiltDeg = DEFAULT_TILT_DEG
      distance = clampDistance(defaultDistance)
    },
    dispose() {
      domElement.removeEventListener('pointerdown', onPointerDown)
      domElement.removeEventListener('pointermove', onPointerMove)
      domElement.removeEventListener('pointerup', onPointerUp)
      domElement.removeEventListener('pointercancel', onPointerUp)
      domElement.removeEventListener('wheel', onWheel)
      domElement.removeEventListener('contextmenu', onContextMenu)
    },
  }
}
