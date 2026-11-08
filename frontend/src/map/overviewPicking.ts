/**
 * 星球總覽的點選：點到球體的貼圖座標 → 經緯度。
 *
 * 直接用貼圖座標（Raycaster 回傳的 uv）換算，不從 3D 座標反推：總覽的球體是 Three.js 的 SphereGeometry，
 * 它的經度起點與方向跟 `latLonToVector3` 的慣例不一樣，從 3D 座標反推要再對一次 handedness（那是踩過三次坑的地方）。
 * 貼圖座標跟後端 OverviewTextureGenerator 的像素一一對應——第 0 欄是經度 −180°、往右遞增；
 * 貼圖設了 flipY，所以 v＝1 是北極——點到的就是畫面上看到的那個位置。
 */

/** 貼圖座標（u、v 都在 0~1）→ 緯度（北正）、經度（東正，−180~180）。 */
export function uvToLatLon(u: number, v: number): { latitude: number; longitude: number } {
  const latitude = Math.min(90, Math.max(-90, v * 180 - 90))
  let longitude = u * 360 - 180
  longitude = ((((longitude + 180) % 360) + 360) % 360) - 180
  return { latitude, longitude }
}

/** 按下與放開之間滑鼠移動超過這麼多像素就算拖曳（旋轉星球），不算點選。 */
export const CLICK_MAX_MOVE_PX = 5
