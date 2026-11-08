/**
 * 時間流逝的速度（2026-11-08 使用者要求可以設定）。
 *
 * - 移動模式：每跨一格（或原地等待一次）過多久。飛行照舊依速度按比例減少（見 steppedHoursPerCrossing），
 *   乘上這個設定——設成每格 6 小時，高超音速飛一格就是 6 小時 × 走路速度 ÷ 飛行速度。
 * - 即時模式：現實每一秒遊戲過多久。以前固定「每 3 秒 +1 小時」，也就是每秒 20 分鐘，那是預設檔。
 *
 * 只能從預設檔裡選：存檔讀回來的值不在清單裡（手改過、或以後換了檔位）就退回預設，不會出現 0 或負數讓時間停住或倒流。
 */

/** 一個速度檔：數值（小時）與顯示的名稱。 */
export interface TimeRateOption {
  hours: number
  label: string
}

/** 移動模式：每跨一格過幾小時。 */
export const STEPPED_HOURS_PER_TILE_OPTIONS: readonly TimeRateOption[] = [
  { hours: 5 / 60, label: '每格 5 分鐘' },
  { hours: 15 / 60, label: '每格 15 分鐘' },
  { hours: 0.5, label: '每格 30 分鐘' },
  { hours: 1, label: '每格 1 小時' },
  { hours: 2, label: '每格 2 小時' },
  { hours: 6, label: '每格 6 小時' },
  { hours: 12, label: '每格 12 小時' },
  { hours: 24, label: '每格 1 天' },
]

/** 移動模式的預設：每格 1 小時（原本的行為）。 */
export const DEFAULT_STEPPED_HOURS_PER_TILE = 1

/** 即時模式：現實每秒過幾小時。 */
export const REALTIME_HOURS_PER_SECOND_OPTIONS: readonly TimeRateOption[] = [
  { hours: 1 / 60, label: '每秒 1 分鐘' },
  { hours: 5 / 60, label: '每秒 5 分鐘' },
  { hours: 20 / 60, label: '每秒 20 分鐘' },
  { hours: 1, label: '每秒 1 小時' },
  { hours: 6, label: '每秒 6 小時' },
]

/** 即時模式的預設：每秒 20 分鐘（原本的「每 3 秒 +1 小時」）。 */
export const DEFAULT_REALTIME_HOURS_PER_SECOND = 20 / 60

/** 即時模式多久推進一次（毫秒）。每次推進「每秒的量 × 這個間隔」。 */
export const REALTIME_TICK_MS = 1000

/** 值在清單裡就用它（容許浮點誤差），不在就回傳預設。 */
export function snapToOption(value: unknown, options: readonly TimeRateOption[], fallback: number): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) return fallback
  const match = options.find((option) => Math.abs(option.hours - value) < 1e-9)
  return match ? match.hours : fallback
}

/** 數值對應的顯示名稱（給 HUD）。 */
export function labelOf(value: number, options: readonly TimeRateOption[]): string {
  return options.find((option) => Math.abs(option.hours - value) < 1e-9)?.label ?? `${value} 小時`
}
