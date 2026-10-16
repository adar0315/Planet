import * as THREE from 'three'
import { northDirectionInto } from './sphereCoordinates'

/**
 * 在球面上連續移動的數學。全部是純函式，不碰場景、不碰 store。
 *
 * ## 面向存成「切向量」，不存「相對正北幾度」
 *
 * 角度表示法在極點會壞掉：正北在極點沒有定義（`tangentBasisAt` 在那裡只能換一個任意的替代方向），
 * 走過極點的那一瞬間，同一個角度會突然指向完全不同的方向。連續移動是真的會走到極點的。
 *
 * 切向量則永遠有定義：它是「玩家正前方」那個實際的方向，移動時跟著位置一起沿大圓**平行傳輸**，
 * 不需要參考北方。只有要給人看（HUD、存檔）時才換算成角度。
 *
 * ## 座標慣例
 *
 * `position` 是**單位向量**（星球表面上的點，與半徑無關），`forward` 是與它垂直的單位切向量。
 * 右方 = `forward × position`——跟 `sphereCoordinates.tangentBasisAt` 的 `east = north × normal`
 * 是同一個慣例，所以面向正北時右方就是正東。那個檔案的 handedness 踩過三次坑，這裡刻意沿用而不另訂。
 *
 * JavaScript 的 number 是 float64，所以單位向量的精度約 1e-16 × 1,852 公里 ≈ 0.2 奈米，
 * 完全不必擔心——要擔心的是存成**世界座標的 float32**（GPU 緩衝區那種），那在這個半徑上只剩約 0.1 公尺。
 */

const scratchRight = new THREE.Vector3()
const scratchDir = new THREE.Vector3()
const scratchAxis = new THREE.Vector3()
const scratchNorth = new THREE.Vector3()
const scratchEast = new THREE.Vector3()

/** 右方切向量（`forward × position`）。結果寫入 target。 */
export function rightOf(
  position: THREE.Vector3,
  forward: THREE.Vector3,
  target: THREE.Vector3 = new THREE.Vector3(),
): THREE.Vector3 {
  return target.crossVectors(forward, position).normalize()
}

/**
 * 原地轉身。正值＝右轉（從上方往下看是順時針），跟方位角「0=北、90=東」的增加方向一致。
 * 會**就地修改** forward。
 */
export function turnInPlace(position: THREE.Vector3, forward: THREE.Vector3, angleDeg: number): void {
  if (angleDeg === 0) return
  const rad = (angleDeg * Math.PI) / 180
  rightOf(position, forward, scratchRight)
  forward.multiplyScalar(Math.cos(rad)).addScaledVector(scratchRight, Math.sin(rad))
  orthonormalize(position, forward)
}

/**
 * 沿球面移動：往「前方 forwardMeters、右方 rightMeters」合成的方向，沿大圓走那段距離。
 * 會**就地修改** position 與 forward（面向跟著平行傳輸，所以一直往前走就是繞星球一圈的大圓）。
 *
 * 球面上的「直走」是大圓，不是緯線——沿著緯線走其實一直在轉彎（除了赤道）。
 *
 * @param radiusMeters 星球半徑，把公尺換算成弧度用。
 */
export function moveAlongSurface(
  position: THREE.Vector3,
  forward: THREE.Vector3,
  forwardMeters: number,
  rightMeters: number,
  radiusMeters: number,
): void {
  rightOf(position, forward, scratchRight)
  scratchDir.copy(forward).multiplyScalar(forwardMeters).addScaledVector(scratchRight, rightMeters)
  const distance = scratchDir.length()
  if (distance === 0) return
  scratchDir.divideScalar(distance)

  // 繞著「position × 移動方向」這根軸轉 distance/R 弧度：
  // position 會沿大圓往移動方向前進，forward 跟著同一個旋轉＝平行傳輸。
  scratchAxis.crossVectors(position, scratchDir).normalize()
  const angle = distance / radiusMeters
  position.applyAxisAngle(scratchAxis, angle).normalize()
  forward.applyAxisAngle(scratchAxis, angle)
  orthonormalize(position, forward)
}

/**
 * 把 forward 修回「與 position 垂直的單位向量」。
 *
 * 每次旋轉都會累積一點浮點誤差，長時間遊玩後 forward 會慢慢偏離切平面。
 * 每一步都修一次，誤差永遠停在 1e-16 的量級，不會累積。
 */
export function orthonormalize(position: THREE.Vector3, forward: THREE.Vector3): void {
  forward.addScaledVector(position, -forward.dot(position))
  if (forward.lengthSq() < 1e-20) {
    // 理論上不會發生（forward 一直被維持在切平面上），保險起見給一個合法的切向量
    northDirectionInto(position, forward)
    return
  }
  forward.normalize()
}

/**
 * 面向的方位角（0=北、90=東、順時針，範圍 [0, 360)）。只給人看與存檔用，**不要拿來做移動計算**。
 * 在極點方位角本身沒有定義，回傳值是任意的——這正是內部不用角度的原因。
 */
export function headingDegOf(position: THREE.Vector3, forward: THREE.Vector3): number {
  northDirectionInto(position, scratchNorth)
  scratchEast.crossVectors(scratchNorth, position).normalize()
  const deg = (Math.atan2(forward.dot(scratchEast), forward.dot(scratchNorth)) * 180) / Math.PI
  return ((deg % 360) + 360) % 360
}

/** headingDegOf 的反向：給定方位角，算出對應的切向量（讀存檔、初始化用）。 */
export function forwardForHeading(
  position: THREE.Vector3,
  headingDeg: number,
  target: THREE.Vector3 = new THREE.Vector3(),
): THREE.Vector3 {
  northDirectionInto(position, scratchNorth)
  scratchEast.crossVectors(scratchNorth, position).normalize()
  const rad = (headingDeg * Math.PI) / 180
  return target
    .copy(scratchNorth)
    .multiplyScalar(Math.cos(rad))
    .addScaledVector(scratchEast, Math.sin(rad))
    .normalize()
}

/** 單位向量 → 經緯度（度）。是 `latLonToVector3` 的反函式，Z 分量的負號要跟它一致。 */
export function latLonOf(position: THREE.Vector3): { latitude: number; longitude: number } {
  const len = position.length()
  const latitude = (Math.asin(THREE.MathUtils.clamp(position.y / len, -1, 1)) * 180) / Math.PI
  const longitude = (Math.atan2(-position.z, position.x) * 180) / Math.PI
  return { latitude, longitude }
}

/**
 * 候選格心裡離 position 最近的那一個（內積最大＝角距離最小）。回傳索引，空陣列回 -1。
 *
 * Goldberg 格是格心的球面 Voronoi 剖分，所以「離哪個格心最近」就等於「站在哪一格」，
 * 不需要做多邊形包含測試。
 */
export function nearestIndex(position: THREE.Vector3, centers: readonly THREE.Vector3[]): number {
  let best = -1
  let bestDot = -Infinity
  for (let i = 0; i < centers.length; i++) {
    const dot = position.dot(centers[i]!)
    if (dot > bestDot) {
      bestDot = dot
      best = i
    }
  }
  return best
}

/** 讓 current 往 target 靠近，但一次最多移動 maxStep。加減速用——起步與停下才不會生硬。 */
export function approach(current: number, target: number, maxStep: number): number {
  if (current < target) return Math.min(current + maxStep, target)
  if (current > target) return Math.max(current - maxStep, target)
  return current
}
