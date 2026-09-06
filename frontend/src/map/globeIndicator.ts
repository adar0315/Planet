/**
 * 右下角位置指示器的數學：以玩家為中心的正射投影，以及逐像素的陸海取樣與日照明暗。
 *
 * 刻意抽出成獨立模組（而不是留在 .vue 裡）：這段是整個元件裡最容易寫錯、又最難用肉眼
 * 看出錯誤的部分——投影方向錯了畫面依然「看起來像個地球」，只是東西相反或正反面顛倒。
 * 抽出來才測得到（見 globeIndicator.spec.ts）。
 */

const DEG2RAD = Math.PI / 180

/** 正射投影的結果（畫布/SVG 的座標系，y 向下為正）。 */
export interface ProjectedPoint {
  x: number
  y: number
  /**
   * 這個點是否位於「面向觀察者」的那半球。
   * 正射投影本身分不出正反面（`lon` 與 `180−lon` 會投影到同一個位置），
   * 需要這個旗標才能把背面的東西畫成空心／隱藏。
   */
  isFrontFacing: boolean
}

/**
 * 以 refLon 為視角中心的正射投影。
 * @param latitudeDeg 目標緯度。
 * @param longitudeDeg 目標經度。
 * @param refLonDeg 視角中心的經度（本元件用玩家的經度，所以玩家永遠在正中央）。
 * @param cx 圓心 x。
 * @param cy 圓心 y。
 * @param r 圓半徑。
 */
export function projectOrthographic(
  latitudeDeg: number,
  longitudeDeg: number,
  refLonDeg: number,
  cx: number,
  cy: number,
  r: number,
): ProjectedPoint {
  const lat = latitudeDeg * DEG2RAD
  const dLon = (longitudeDeg - refLonDeg) * DEG2RAD
  const cosLat = Math.cos(lat)

  return {
    // sin(dLon) > 0（往東）→ x 變大，也就是東在右，跟一般地圖慣例一致
    x: cx + r * cosLat * Math.sin(dLon),
    y: cy - r * Math.sin(lat),
    isFrontFacing: cosLat * Math.cos(dLon) >= 0,
  }
}

/**
 * 太陽在指定地點的天頂角餘弦：>0 代表太陽在地平線above（白天），<0 代表在地平線下（夜晚），
 * 0 就是日出/日落的瞬間。用來決定指示器上每個像素的明暗。
 *
 * @param latitudeDeg 地點緯度。
 * @param longitudeDeg 地點經度。
 * @param subsolarLatDeg 太陽直射點緯度（＝太陽赤緯）。
 * @param subsolarLonDeg 太陽直射點經度。
 */
export function sunCosZenith(
  latitudeDeg: number,
  longitudeDeg: number,
  subsolarLatDeg: number,
  subsolarLonDeg: number,
): number {
  const lat = latitudeDeg * DEG2RAD
  const subLat = subsolarLatDeg * DEG2RAD
  const dLon = (longitudeDeg - subsolarLonDeg) * DEG2RAD

  return Math.sin(lat) * Math.sin(subLat) + Math.cos(lat) * Math.cos(subLat) * Math.cos(dLon)
}

/** 在 edge0~edge1 之間做平滑過渡（超出範圍時夾在 0/1），用來讓晨昏線不是硬邊。 */
export function smoothstep(edge0: number, edge1: number, value: number): number {
  const t = Math.min(1, Math.max(0, (value - edge0) / (edge1 - edge0)))
  return t * t * (3 - 2 * t)
}

/** 把經度正規化到 [-180, 180)。 */
export function normalizeLongitude(longitudeDeg: number): number {
  return ((((longitudeDeg + 180) % 360) + 360) % 360) - 180
}

/**
 * 反向正射投影：從圓內的單位座標回推經緯度。
 * @param u 水平座標，圓心為0、右緣為+1。
 * @param v 垂直座標，圓心為0、**上緣為+1**（注意畫布的y是向下的，呼叫端要自己翻轉）。
 * @param refLonDeg 視角中心經度。
 * @returns 落在圓外（u²+v²>1）時回傳 null。
 */
export function unprojectOrthographic(
  u: number,
  v: number,
  refLonDeg: number,
): { latitudeDeg: number; longitudeDeg: number } | null {
  const rho2 = u * u + v * v
  if (rho2 > 1) return null

  // 球面上該點的第三個分量（指向觀察者），由單位球的條件反推
  const w = Math.sqrt(Math.max(0, 1 - rho2))

  return {
    latitudeDeg: Math.asin(v) / DEG2RAD,
    longitudeDeg: normalizeLongitude(refLonDeg + Math.atan2(u, w) / DEG2RAD),
  }
}

/** 在陸海遮罩上取樣（遮罩是等距柱狀投影：第0列＝北極、最左行＝西經180）。 */
export function sampleWaterMask(
  mask: Uint8Array,
  maskWidth: number,
  maskHeight: number,
  latitudeDeg: number,
  longitudeDeg: number,
): boolean {
  const xf = ((normalizeLongitude(longitudeDeg) + 180) / 360) * maskWidth
  const yf = ((90 - latitudeDeg) / 180) * maskHeight

  const x = Math.min(maskWidth - 1, Math.max(0, Math.floor(xf)))
  const y = Math.min(maskHeight - 1, Math.max(0, Math.floor(yf)))

  return mask[y * maskWidth + x] === 1
}
