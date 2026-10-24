// 對應後端 Planet.Shared 的 TileDto，前後端型別需保持同步

export type TerrainType =
  | 'Grassland' // 草地：heightCategory 0~4 = 腳板/小腿肚/腰部/肩膀/高過頭
  | 'Shrubland' // 灌木叢：heightCategory 0~3 = 小腿肚/腰部/肩膀/高過頭
  | 'Forest' // 林地：heightCategory 0~7 = (人高/一層樓/二層樓/三層樓) x (疏/密)
  | 'Freshwater' // 淡水：heightCategory 0~6 = 小腿肚/腰部/肩膀/過一人/過二人/過三人/過十人深
  | 'Seawater' // 海水：heightCategory 0~6，同淡水
  | 'Mud' // 泥地
  | 'Loess' // 黃土地
  | 'Rock' // 岩地
  | 'Sand' // 沙地/沙漠
  | 'SnowField' // 雪地
  | 'Glacier' // 冰原/冰河
  | 'Swamp' // 沼澤/濕地
  | 'SaltFlat' // 鹽鹼地/鹽田
  | 'VolcanicRock' // 火山岩地/熔岩地
  | 'CoralReef' // 珊瑚礁
  | 'Cliff' // 懸崖/峭壁
  | 'Tundra' // 苔原
  | 'Gravel' // 河床/礫灘

export type TileShape = 'Pentagon' | 'Hexagon'

export interface TileDto {
  tileId: number
  shape: TileShape
  faceIndex: number
  latitude: number
  longitude: number
  terrain: TerrainType
  /** 地表覆蓋物的高度/深度細分等級，意義依terrain而定（見TerrainType的說明），跟elevationLevel是獨立屬性。 */
  heightCategory: number
  /** 地形海拔等級，相對seaLevel，範圍-500~+500，每級5公尺。正值=海平面以上，負值=海平面以下。 */
  elevationLevel: number
  /** 該位置的海平面等級（跟elevationLevel同單位）。判斷是否淹沒在水下：elevationLevel < seaLevel。 */
  seaLevel: number
  /**
   * 這個Tile的鄰居tileId（六角6個、五角5個），順序對應後端 ITileNeighborResolver 的輸出。
   * null = 該鄰居確實存在於星球上，但不在這次回傳的patch範圍內（patch邊緣的格子會這樣）。
   *
   * 只有 GET /api/tiles/nearby 會帶這個欄位（它一次回傳一整片，才有辦法互相連結）；
   * 單一Tile查詢與SignalR回傳不會有，所以是optional。
   */
  neighborIds?: (number | null)[]
  /** 從哪一個模擬粗格放大來的（查四季的積雪、結冰）；程序化地形沒有。 */
  sourceCellId?: number
  /**
   * 這一格範圍裡最大那條河的平均河寬（公尺）；沒有河時省略（水文地貌規劃 H6）。
   * 河比一格窄時地塊本身仍是陸地，拉遠時整格畫成河水的藍色。
   */
  riverWidthMeters?: number
  /** 那條河從哪一個粗格流出來（查當月的流量）。 */
  riverCellId?: number
}

/**
 * 近看細分出來的小六角（約 250 公尺，水文地貌規劃 H7）。欄位意義同 TileDto，tileId 是小格編號（從一兆開始，不會跟地塊撞號）。
 * 後端算好鄰居一起送來（鄰居推導只准在後端一份，CLAUDE.md）。
 */
export interface SubTileDto extends TileDto {
  /** 屬於哪一個地塊。 */
  parentTileId: number
  /** 到附近那條河中心線的距離（公尺）；附近沒有河時省略。 */
  riverDistanceMeters?: number
  /** 在泛濫平原上：洪峰時會淹水。 */
  floodplain?: boolean
}

/** 一個模擬粗格的四季（水文地貌規劃 H8）。陣列都是 12 個月，0 是 1 月。 */
export interface CellSeasonsDto {
  cellId: number
  temperature: number[]
  precipitation: number[]
  /** 當月河流量相對全年平均的比例：當月河寬＝平均河寬 × √比例。 */
  dischargeRatio: number[]
  /** 第 m 位元＝第 m 個月地面被雪蓋住。 */
  snowMonths: number
  /** 第 m 位元＝第 m 個月結冰（河、湖、海冰）。 */
  frozenMonths: number
  /** 0 不是河、1 常流河、2 季節河、3 間歇河。 */
  flowRegime: number
}
