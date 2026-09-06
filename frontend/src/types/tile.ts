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
}
