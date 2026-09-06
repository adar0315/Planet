import * as THREE from 'three'

/**
 * 經緯度轉三維座標。用Y當極軸（配合Three.js/OrbitControls慣例的Y-up），
 * 跟後端「Z當極軸」的內部表示法不同，只是渲染座標系的選擇，經緯度數值本身不變。
 */
export function latLonToVector3(latitudeDegrees: number, longitudeDegrees: number, radius: number): THREE.Vector3 {
  return latLonToVector3Into(latitudeDegrees, longitudeDegrees, radius, new THREE.Vector3())
}

/**
 * 與 <see cref="latLonToVector3"/> 相同，但寫進既有的向量、不配置新物件（給每幀都要算的地方用）。
 * handedness的邏輯只寫在這裡一份，latLonToVector3 也是轉呼叫這支——這個負號是踩過三次坑的地方，
 * 絕對不要在別處複製一份公式。
 * @param target 結果寫入這個向量，同時作為回傳值。
 */
export function latLonToVector3Into(
  latitudeDegrees: number,
  longitudeDegrees: number,
  radius: number,
  target: THREE.Vector3,
): THREE.Vector3 {
  const lat = (latitudeDegrees * Math.PI) / 180
  const lon = (longitudeDegrees * Math.PI) / 180
  const cosLat = Math.cos(lat)

  // Z分量取負：讓「鏡頭在正上方、camera.up=當地正北」這組合下，畫面呈現標準地圖方向（北上東右）。
  // 原本的+號版本數學上自洽，但跟這個camera設定搭配起來，算出來的畫面是東西鏡像的
  // （Three.js的lookAt公式決定camera右方向＝cross(up,eye-target)，這個叉積的方向跟原本的
  // 經緯度→XYZ handedness對不起來），實測「按D想往右卻往左」就是這個鏡像造成的。
  return target.set(cosLat * Math.cos(lon), Math.sin(lat), -cosLat * Math.sin(lon)).multiplyScalar(radius)
}

/** 在球面上某點的東/北切線方向（局部平面近似，Tile相對星球半徑非常小，誤差可忽略）。極點沒有明確的東西方向，用固定切線頂替。 */
export function tangentBasisAt(position: THREE.Vector3): { normal: THREE.Vector3; north: THREE.Vector3; east: THREE.Vector3 } {
  const normal = position.clone().normalize()
  const worldUp = new THREE.Vector3(0, 1, 0)

  let north = worldUp.clone().sub(normal.clone().multiplyScalar(worldUp.dot(normal)))
  if (north.lengthSq() < 1e-8) {
    // 在極點：world up跟法向量幾乎平行，改用固定方向當切線基準
    north = new THREE.Vector3(1, 0, 0).sub(normal.clone().multiplyScalar(normal.x))
  }
  north.normalize()

  const east = new THREE.Vector3().crossVectors(north, normal).normalize()

  return { normal, north, east }
}

// 給 northDirectionInto 用的暫存向量（module-level重複使用，避免每幀配置新物件）。
const scratchNormal = new THREE.Vector3()
const scratchWorldUp = new THREE.Vector3(0, 1, 0)

/**
 * 只取「正北」切線方向，且完全不配置新物件（結果寫進target）。
 * 專門給每幀都要跑的指北針計算用——tangentBasisAt每次呼叫會配置5~6個Vector3，
 * 一秒60幀就是每秒數百個短命物件，純粹增加GC壓力。
 * @param position 球面上的位置。
 * @param target 結果寫入這個向量，同時作為回傳值。
 */
export function northDirectionInto(position: THREE.Vector3, target: THREE.Vector3): THREE.Vector3 {
  scratchNormal.copy(position).normalize()

  target.copy(scratchWorldUp).addScaledVector(scratchNormal, -scratchWorldUp.dot(scratchNormal))
  if (target.lengthSq() < 1e-8) {
    // 在極點：world up跟法向量幾乎平行，改用固定方向當切線基準（與tangentBasisAt同一套規則）
    target.set(1, 0, 0).addScaledVector(scratchNormal, -scratchNormal.x)
  }
  return target.normalize()
}
