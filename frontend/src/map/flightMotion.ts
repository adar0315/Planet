/**
 * 飛行的數學：速度檔、升降速度、加減速、步進模式的時間換算。全部是純函式，不碰場景、不碰 store。
 *
 * 規劃與拍板的數值見 docs/flight-and-god-view-plan.md（2026-10-23）。
 *
 * ## 高度是相對「畫面上的地表」
 *
 * 地形有 8 倍的高度誇張（`ELEVATION_EXAGGERATION`），真實 3 公里的山在畫面上有 24 公里高。
 * 飛行高度若用真實比例，飛機會穿進山裡，所以這裡所有的高度都是畫面上的公尺數
 * （場景單位乘上 `星球半徑 ÷ 顯示半徑`），跟地表 mesh 同一套尺度。
 */

/** 一個速度檔。 */
export interface FlightTier {
  /** HUD 顯示的名稱。 */
  readonly label: string
  /** 前進的最高速度（公尺／秒）。 */
  readonly speedMps: number
}

/**
 * 三個速度檔。最高 3 km/s：一幀上限 0.1 秒只飛 300 公尺，遠小於 1.7 公里的格心間距，
 * 所以「一幀最多跨一格」的跨格偵測不必改（見 playerController.ts）。
 */
export const FLIGHT_TIERS: readonly FlightTier[] = Object.freeze([
  { label: '巡航', speedMps: 250 },
  { label: '噴射', speedMps: 1_000 },
  { label: '高超音速', speedMps: 3_000 },
])

/** 起飛時自動爬升到的離地高度（公尺）。 */
export const TAKEOFF_ALTITUDE_METERS = 500

/** 飛行時的最低離地高度（公尺）。降落時不受這個限制。 */
export const MIN_FLIGHT_ALTITUDE_METERS = 100

/** 最高離地高度（公尺）。200 公里時地平線約在 860 公里外。 */
export const MAX_FLIGHT_ALTITUDE_METERS = 200_000

/**
 * 升降速度跟高度成比例：每秒變化目前高度的這個比例。
 * 指數變化在高低兩端的手感才一致——低空能微調、高空不必按很久（500 公尺爬到 200 公里約 7.5 秒）。
 */
export const VERTICAL_RATE_PER_SEC = 0.8

/** 升降速度的下限（公尺／秒）。沒有下限的話，貼地時速度趨近 0，永遠離不開地面。 */
export const MIN_VERTICAL_SPEED_MPS = 200

/** 空氣中的音速（公尺／秒），HUD 換算馬赫數用。 */
export const SPEED_OF_SOUND_MPS = 343

/** 某個離地高度下的升降速度（公尺／秒，恆為正）。 */
export function verticalSpeedAt(altitudeAboveGroundMeters: number): number {
  return Math.max(MIN_VERTICAL_SPEED_MPS, VERTICAL_RATE_PER_SEC * Math.max(0, altitudeAboveGroundMeters))
}

/**
 * 飛行的加速度與減速度（公尺／秒²）。
 *
 * 跟速度成比例：任何一檔大約 2 秒到全速。固定的加速度沒辦法同時適用 250 與 3,000 m/s——
 * 照巡航調會讓高超音速要等 24 秒，照高超音速調會讓巡航一起步就到頂。
 * 換低檔時要從目前的速度減下來，所以用「目標與目前速度的較大者」當尺度，
 * 否則從 3,000 換到 250 時會用巡航的減速度、要 15 秒才慢下來。
 */
export function flightAccelerationMps2(
  topSpeedMps: number,
  currentSpeedMps: number,
  isDecelerating: boolean,
): number {
  const scale = Math.max(topSpeedMps, Math.abs(currentSpeedMps))
  return scale * (isDecelerating ? 0.75 : 0.5)
}

/**
 * 步進模式下，跨一格要推進幾小時。
 *
 * 走路是「每跨一格 1 小時」，不論走或跑。飛行照實際飛行時間換算（2026-10-23 拍板）：
 * 用走路速度當基準，飛得越快、跨一格花的時間越少——高超音速每格約 4 分鐘。
 * 剛起飛、速度還比走路慢時不能多於 1 小時（否則飛行反而比走路更耗時間）。
 *
 * @param isFlying 不在飛行就照舊回傳 1。
 * @param horizontalSpeedMps 當下的水平速度。
 * @param walkSpeedMps 走路速度（基準）。
 */
export function steppedHoursPerCrossing(
  isFlying: boolean,
  horizontalSpeedMps: number,
  walkSpeedMps: number,
): number {
  if (!isFlying) return 1
  const speed = Math.abs(horizontalSpeedMps)
  if (!(speed > walkSpeedMps)) return 1
  return walkSpeedMps / speed
}
