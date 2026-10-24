import type { CellSeasonsDto, SubTileDto, TerrainType, TileDto } from '@/types/tile'

/**
 * 四季（水文地貌規劃 H8，2026-10-24）：依遊戲時間在相鄰兩個月之間內插，決定河寬、積雪、結冰。全部是純函式。
 *
 * 後端每個月的資料代表那個月的**月中**（一年中第 (m+0.5)·365/12 天，跟模擬的 Climate.SolveSeasons 同一個慣例），
 * 所以 1 月 1 日落在 12 月與 1 月之間。南北半球不用另外處理：每一格的 12 個月本來就是它自己的。
 */

/** 一年中的位置：介於第 month 與第 next 個月的月中之間，t 是往 next 走了多少（0~1）。 */
export interface MonthPosition {
  month: number
  next: number
  t: number
}

const DAYS_PER_YEAR = 365
const MONTHS = 12

/** 遊戲時間（UTC）→ 一年中的位置。 */
export function monthPosition(date: Date): MonthPosition {
  const startOfYear = Date.UTC(date.getUTCFullYear(), 0, 1)
  const day = (date.getTime() - startOfYear) / 86_400_000
  // 月中在 (m+0.5)·365/12，所以先往回退半個月再取整
  const x = (((day / DAYS_PER_YEAR) * MONTHS - 0.5) % MONTHS + MONTHS) % MONTHS
  const month = Math.floor(x) % MONTHS
  return { month, next: (month + 1) % MONTHS, t: x - Math.floor(x) }
}

/** 12 個月的值內插到這個時間點；陣列不是 12 個（例如程序化的星球沒有資料）回傳 fallback。 */
export function interpolateMonthly(values: readonly number[], position: MonthPosition, fallback = 0): number {
  if (values.length !== MONTHS) return fallback
  const a = values[position.month]!
  const b = values[position.next]!
  return a + (b - a) * position.t
}

/** 位元遮罩（第 m 位元＝第 m 個月）在這個時間點成不成立：取比較近的那個月。 */
export function monthFlag(mask: number, position: MonthPosition): boolean {
  const month = position.t < 0.5 ? position.month : position.next
  return ((mask >> month) & 1) === 1
}

/** 某個緯度在這個時間點的季節名稱（南半球相反）。 */
export function seasonName(latitude: number, date: Date): '春' | '夏' | '秋' | '冬' {
  const m = date.getUTCMonth() // 0 = 1 月
  const north = m >= 2 && m <= 4 ? '春' : m >= 5 && m <= 7 ? '夏' : m >= 8 && m <= 10 ? '秋' : '冬'
  if (latitude >= 0) return north
  return ({ 春: '秋', 夏: '冬', 秋: '春', 冬: '夏' } as const)[north]
}

/** 當月流量比例低於這個值，河床就是乾的（間歇河的枯水期）。 */
export const DRY_DISCHARGE_RATIO = 0.05

/** 洪峰：當月流量比例超過這個值，泛濫平原淹水。 */
export const FLOOD_DISCHARGE_RATIO = 1.6

/** 當月的河寬（公尺）：河寬約與流量的平方根成正比。乾掉回傳 0。 */
export function riverWidthNow(meanWidthMeters: number, dischargeRatio: number): number {
  if (!(meanWidthMeters > 0) || dischargeRatio < DRY_DISCHARGE_RATIO) return 0
  return meanWidthMeters * Math.sqrt(dischargeRatio)
}

/** 查四季資料的函式（沒有就回傳 undefined，例如程序化的星球、還沒載到）。 */
export type SeasonsLookup = (cellId: number) => CellSeasonsDto | undefined

const WATER: ReadonlySet<TerrainType> = new Set<TerrainType>(['Freshwater', 'Seawater'])
const NO_SNOW: ReadonlySet<TerrainType> = new Set<TerrainType>(['Freshwater', 'Seawater', 'Glacier', 'SnowField', 'CoralReef'])

/**
 * 小格的「河道」半寬至少這麼多（公尺）：小格約 250 公尺，河比小格窄的時候，離中心線最近的那一串小格仍然要是水，
 * 河才連得成一條線（使用者要的是看得到的河，2026-10-24）。約等於小格格心間距的一半。
 */
export const MIN_VISIBLE_RIVER_HALF_WIDTH_METERS = 220

/**
 * 這一格（地塊或小格）在這個時間點畫成什麼地形：積雪、結冰、乾河床、洪水、整格的河。
 * 不改資料本身，只決定顏色。
 *
 * @param tile 地塊或小格。
 * @param position 一年中的位置。
 * @param seasonsOf 查四季資料。
 * @param isSubTile 是近看細分的小格（河依中心線距離決定）；否則是地塊（範圍裡有河就整格是河，拉遠時用）。
 */
export function seasonalTerrain(
  tile: TileDto | SubTileDto,
  position: MonthPosition,
  seasonsOf: SeasonsLookup,
  isSubTile: boolean,
): TerrainType {
  const cell = tile.sourceCellId !== undefined ? seasonsOf(tile.sourceCellId) : undefined
  const river = tile.riverCellId !== undefined ? seasonsOf(tile.riverCellId) : undefined
  const ratio = river ? interpolateMonthly(river.dischargeRatio, position, 1) : 1
  const meanWidth = tile.riverWidthMeters ?? 0
  const frozen = cell ? monthFlag(cell.frozenMonths, position) : false

  let terrain = tile.terrain
  if (meanWidth > 0) {
    const width = riverWidthNow(meanWidth, ratio)
    if (isSubTile) {
      const sub = tile as SubTileDto
      const distance = sub.riverDistanceMeters ?? Number.POSITIVE_INFINITY
      const channelHalf = Math.max(meanWidth / 2, MIN_VISIBLE_RIVER_HALF_WIDTH_METERS)
      if (distance <= channelHalf) {
        // 河道：有水是淡水，乾掉是礫石河床
        terrain = width > 0 ? 'Freshwater' : 'Gravel'
      } else if (sub.floodplain && ratio >= FLOOD_DISCHARGE_RATIO) {
        terrain = 'Freshwater' // 洪峰淹了泛濫平原
      }
    } else if (!WATER.has(terrain)) {
      // 拉遠：範圍裡有河的地塊整格是河（乾掉是河床）
      terrain = width > 0 ? 'Freshwater' : 'Gravel'
    }
  }

  if (WATER.has(terrain) && frozen) return 'Glacier' // 結冰的河、湖、海
  if (cell && !NO_SNOW.has(terrain) && monthFlag(cell.snowMonths, position)) return 'SnowField'
  return terrain
}
