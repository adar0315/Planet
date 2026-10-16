import * as THREE from 'three'
import { northDirectionInto } from '@/map/sphereCoordinates'
import { useGameStateStore } from '@/stores/gameState'

/**
 * HUD 需要的鏡頭資訊：注視點與縮放範圍。
 * 刻意只要這三個欄位而不是整個鏡頭控制器——走動視角的追尾鏡頭（cameraRig.ts）與
 * 其他視角用的 OrbitControls 都滿足這個形狀。
 */
export interface CameraTargetInfo {
  readonly target: THREE.Vector3
  readonly minDistance: number
  readonly maxDistance: number
}

export interface HudStatsUpdater {
  /** 每幀呼叫一次：依目前鏡頭狀態算出指北針角度、視角傾斜、鏡頭距離/高度、縮放百分比，寫回 gameState。 */
  update(camera: THREE.PerspectiveCamera, controls: CameraTargetInfo, sceneToMeters: number): void
}

/**
 * 算出HUD要顯示的鏡頭相關數字（指北針角度、視角傾斜、距離、高度、縮放百分比）並寫回 gameState。
 *
 * 純粹是「讀鏡頭狀態→寫store」的單向資料流，不持有、也不修改鏡頭本身——鏡頭的定位/轉向邏輯
 * 在 cameraRig.ts，兩者刻意分開：這裡的計算錯了頂多HUD數字不準，cameraRig錯了會是「畫面本身」壞掉。
 *
 * 每幀配置的暫存向量（camRight/camUp/compassNorth/camToTarget/localUp）留在這個factory的閉包裡
 * 重複使用，避免每幀產生新的 Vector3 造成GC壓力。
 */
export function createHudStatsUpdater(
  gameState: ReturnType<typeof useGameStateStore>,
): HudStatsUpdater {
  const camRight = new THREE.Vector3()
  const camUp = new THREE.Vector3()
  const compassNorth = new THREE.Vector3()
  const camToTarget = new THREE.Vector3()
  const localUp = new THREE.Vector3()

  function update(camera: THREE.PerspectiveCamera, controls: CameraTargetInfo, sceneToMeters: number) {
    if (controls.target.lengthSq() === 0) return

    // 指北針：把目前位置的「正北」切線方向投影到鏡頭的螢幕平面，換算成螢幕上的角度。
    // 量化到0.5度、且只在真的變了才寫回store——這是每幀都會跑的程式碼，而 compassAngleDeg 一改
    // 就會讓 Compass.vue 重新render。原本無條件寫入浮點數，配上 enableDamping 的緩動尾巴，
    // 等於鏡頭只要動過就每秒重繪指北針60次。0.5度在64px的錶盤上是次像素等級，看不出差別。
    const north = northDirectionInto(controls.target, compassNorth)
    camRight.setFromMatrixColumn(camera.matrixWorld, 0)
    camUp.setFromMatrixColumn(camera.matrixWorld, 1)
    const angleDeg = Math.atan2(north.dot(camRight), north.dot(camUp)) * (180 / Math.PI)
    const quantized = Math.round(angleDeg * 2) / 2
    if (quantized !== gameState.compassAngleDeg) gameState.compassAngleDeg = quantized

    // 視角傾斜角度：鏡頭→焦點方向 與 焦點處「正上方」方向的夾角。0°=正上方俯視，角度越大越貼近地平線。
    // 焦點都在球心射線上（latLonToVector3沿法向量偏移海拔，偏移後仍在同一條射線上），
    // 所以「正上方」方向就是 controls.target 本身歸一化，不需要另外呼叫tangentBasisAt。
    camToTarget.copy(camera.position).sub(controls.target).normalize()
    localUp.copy(controls.target).normalize()
    const tiltDeg =
      Math.acos(THREE.MathUtils.clamp(camToTarget.dot(localUp), -1, 1)) * (180 / Math.PI)
    const quantizedTilt = Math.round(tiltDeg * 2) / 2
    if (quantizedTilt !== gameState.cameraTiltDeg) gameState.cameraTiltDeg = quantizedTilt

    // 鏡頭到玩家所在格的直線距離（換算成星球實際比例的公尺數）。
    const distanceScene = camera.position.distanceTo(controls.target)
    const quantizedDistance = Math.round((distanceScene * sceneToMeters) / 10) * 10
    if (quantizedDistance !== gameState.cameraDistanceMeters)
      gameState.cameraDistanceMeters = quantizedDistance

    // 鏡頭在該格「地表之上」的高度：兩者都是從星球中心量的半徑，相減就是高度。
    // 傾斜時會明顯小於直線距離（正上方俯視時兩者相同，越貼近地平線差越多）。
    const heightScene = camera.position.length() - controls.target.length()
    const quantizedHeight = Math.round((heightScene * sceneToMeters) / 10) * 10
    if (quantizedHeight !== gameState.cameraHeightMeters)
      gameState.cameraHeightMeters = quantizedHeight

    // 縮放百分比：相對目前min/max距離的位置。
    // min/max會隨初始/移動狀態變化，直接讀controls目前的值，不要自己另外假設一份範圍。
    const zoomRange = controls.maxDistance - controls.minDistance
    const zoomPercent =
      zoomRange > 0
        ? THREE.MathUtils.clamp(((distanceScene - controls.minDistance) / zoomRange) * 100, 0, 100)
        : 0
    const quantizedZoom = Math.round(zoomPercent)
    if (quantizedZoom !== gameState.cameraZoomPercent) gameState.cameraZoomPercent = quantizedZoom
  }

  return { update }
}
