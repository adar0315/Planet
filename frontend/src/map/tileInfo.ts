import type { CellSeasonsDto, StructureKind, SubTileDto, TerrainEditKind, TileDto } from '@/types/tile'
import { METERS_PER_ELEVATION_LEVEL } from './tileMeshBuilder'
import { interpolateMonthly, monthFlag, monthPosition, seasonName } from './seasons'

/** 資訊面板的一列：標籤與值。 */
export interface TileInfoRow {
  label: string
  value: string
}

/** 建築物的名稱（跟編輯工具列的名稱一致）。 */
const STRUCTURE_LABELS: Record<StructureKind, string> = {
  House: '房子',
  Castle: '城堡',
  Road: '道路',
  Farm: '農田',
  Orchard: '果園',
  Pasture: '牧場',
  Bridge: '橋',
}

const TERRAIN_EDIT_LABELS: Record<TerrainEditKind, string> = {
  Channel: '挖出來的河道',
  Fill: '填平過',
}

/** CellSeasonsDto.flowRegime：0 不是河、1 常流河、2 季節河、3 間歇河。 */
const FLOW_REGIME_LABELS = ['', '常流河', '季節河', '間歇河'] as const

const FOREST_HEIGHTS = ['人高', '一層樓高', '二層樓高', '三層樓高'] as const
const GRASS_HEIGHTS = ['腳板', '小腿肚', '腰部', '肩膀', '高過頭'] as const
const SHRUB_HEIGHTS = ['小腿肚', '腰部', '肩膀', '高過頭'] as const

/**
 * 植被的高度與疏密（heightCategory 的意義依地形而定，見 types/tile.ts 的 TerrainType）。不是植被就回傳 null。
 * 林地＝高度（0~3）× 2 ＋ 疏密（0 疏、1 密），跟畫樹用的是同一個編碼（vegetationModels.forestTrees）。
 */
export function describeVegetation(terrain: TileDto['terrain'], heightCategory: number): string | null {
  switch (terrain) {
    case 'Forest': {
      const level = Math.min(Math.max(heightCategory, 0), 7)
      return `${FOREST_HEIGHTS[level >> 1]}的${(level & 1) === 1 ? '密林' : '疏林'}`
    }
    case 'Grassland':
      return `草高到${GRASS_HEIGHTS[Math.min(Math.max(heightCategory, 0), GRASS_HEIGHTS.length - 1)]}`
    case 'Shrubland':
      return `灌木高到${SHRUB_HEIGHTS[Math.min(Math.max(heightCategory, 0), SHRUB_HEIGHTS.length - 1)]}`
    default:
      return null
  }
}

/** 緯度、經度寫成「12.3456°N、45.6789°E」。 */
export function formatLatLon(latitude: number, longitude: number): string {
  const lat = `${Math.abs(latitude).toFixed(4)}°${latitude >= 0 ? 'N' : 'S'}`
  const lon = `${Math.abs(longitude).toFixed(4)}°${longitude >= 0 ? 'E' : 'W'}`
  return `${lat}、${lon}`
}

/** 公尺數加千分位。 */
function meters(value: number): string {
  return `${Math.round(value).toLocaleString('zh-TW')} 公尺`
}

/**
 * 點選的方塊（地塊或近看的小格）要顯示的資訊。純函式：地形名稱與四季資料由呼叫端提供，方便測試。
 *
 * @param tile 點到的地塊或小格。
 * @param isSubTile 是不是近看的小格（小格才有建築物、離河距離、泛濫平原）。
 * @param terrainName 地形的顯示名稱（`terrainStyles.getDisplayName`）。
 * @param seasons 這一格所屬模擬粗格的四季；程序化的星球沒有。
 * @param gameTime 目前的遊戲時間（決定季節與當月的數字）。
 */
export function describeTile(
  tile: TileDto | SubTileDto,
  isSubTile: boolean,
  terrainName: (terrain: TileDto['terrain']) => string,
  seasons: CellSeasonsDto | undefined,
  gameTime: Date,
): TileInfoRow[] {
  const rows: TileInfoRow[] = []
  const sub = isSubTile ? (tile as SubTileDto) : null

  rows.push({ label: '種類', value: sub ? `小格（屬於地塊 ${sub.parentTileId}）` : '地塊' })
  rows.push({ label: '編號', value: String(tile.tileId) })
  rows.push({ label: '座標', value: formatLatLon(tile.latitude, tile.longitude) })
  rows.push({ label: '地形', value: terrainName(tile.terrain) })
  const vegetation = describeVegetation(tile.terrain, tile.heightCategory)
  if (vegetation) rows.push({ label: '植被', value: vegetation })

  const elevation = tile.elevationLevel * METERS_PER_ELEVATION_LEVEL
  const depth = (tile.seaLevel - tile.elevationLevel) * METERS_PER_ELEVATION_LEVEL
  rows.push({
    label: depth > 0 ? '水深' : '海拔',
    value: depth > 0 ? `${meters(depth)}（底部海拔 ${meters(elevation)}）` : meters(elevation),
  })

  if (tile.riverWidthMeters !== undefined) {
    rows.push({ label: '河寬', value: `${meters(tile.riverWidthMeters)}（平均）` })
  }
  if (sub?.riverDistanceMeters !== undefined) {
    rows.push({ label: '離河中心', value: meters(sub.riverDistanceMeters) })
  }
  if (sub?.floodplain) rows.push({ label: '泛濫平原', value: '洪峰時會淹水' })
  if (sub?.structure) rows.push({ label: '建築物', value: STRUCTURE_LABELS[sub.structure] ?? sub.structure })
  if (sub?.terrainEdit) rows.push({ label: '地形修改', value: TERRAIN_EDIT_LABELS[sub.terrainEdit] ?? sub.terrainEdit })

  const season = `${seasonName(tile.latitude, gameTime)}季`
  if (!seasons) {
    rows.push({ label: '季節', value: season })
    return rows
  }
  const position = monthPosition(gameTime)
  const temperature = interpolateMonthly(seasons.temperature, position)
  const precipitation = interpolateMonthly(seasons.precipitation, position)
  const annual = seasons.precipitation.reduce((a, b) => a + b, 0) / seasons.precipitation.length
  const wetness = annual > 0 ? precipitation / annual : 1
  rows.push({ label: '季節', value: `${season}、${temperature.toFixed(1)}°C` })
  rows.push({ label: '降水', value: `當月是全年平均的 ${(wetness * 100).toFixed(0)}%` })
  const surface: string[] = []
  if (monthFlag(seasons.snowMonths, position)) surface.push('積雪')
  if (monthFlag(seasons.frozenMonths, position)) surface.push('結冰')
  // 跟畫面上色同一個判斷（monthFlag），面板說有雪的時候地上就是白的
  if (surface.length > 0) rows.push({ label: '地表', value: surface.join('、') })
  const regime = FLOW_REGIME_LABELS[seasons.flowRegime]
  if (regime) rows.push({ label: '河流', value: regime })
  rows.push({ label: '模擬粗格', value: String(seasons.cellId) })
  return rows
}
