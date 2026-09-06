// 對應後端 Planet.Shared 的 TerrainStyleDto

import type { TerrainType } from './tile'

export interface TerrainStyleDto {
  terrain: TerrainType
  nameEn: string
  nameZh: string
  /** 清晨（日出後第一小時）顏色，格式 #rrggbb。 */
  colorDawn: string
  /** 白天顏色，格式 #rrggbb。 */
  colorDay: string
  /** 黃昏（日落前一小時）顏色，格式 #rrggbb。 */
  colorDusk: string
  /** 夜晚顏色，格式 #rrggbb。 */
  colorNight: string
}
