import { describe, expect, it } from 'vitest'
import type { CellSeasonsDto, SubTileDto, TileDto } from '@/types/tile'
import { describeTile, describeVegetation, formatLatLon } from './tileInfo'

const tile: TileDto = {
  tileId: 17580634,
  shape: 'Hexagon',
  faceIndex: 17,
  latitude: -8.661383,
  longitude: -124.727505,
  terrain: 'Forest',
  heightCategory: 7,
  elevationLevel: 6,
  seaLevel: 0,
  sourceCellId: 198054,
}

const name = (t: string) => `名稱:${t}`
const july = new Date(Date.UTC(2026, 6, 15, 12))

function value(rows: { label: string; value: string }[], label: string): string | undefined {
  return rows.find((r) => r.label === label)?.value
}

describe('formatLatLon', () => {
  it('南緯西經用 S、W，取絕對值', () => {
    expect(formatLatLon(-8.661383, -124.727505)).toBe('8.6614°S、124.7275°W')
    expect(formatLatLon(12.5, 0)).toBe('12.5000°N、0.0000°E')
  })
})

describe('describeTile', () => {
  it('地塊：種類、編號、地形名稱、海拔（每級 5 公尺）', () => {
    const rows = describeTile(tile, false, name, undefined, july)
    expect(value(rows, '種類')).toBe('地塊')
    expect(value(rows, '編號')).toBe('17580634')
    expect(value(rows, '地形')).toBe('名稱:Forest')
    expect(value(rows, '海拔')).toBe('30 公尺')
    expect(value(rows, '水深')).toBeUndefined()
  })

  it('水下的格子顯示水深與底部海拔', () => {
    const rows = describeTile({ ...tile, terrain: 'Seawater', elevationLevel: -40, seaLevel: 0 }, false, name, undefined, july)
    expect(value(rows, '水深')).toBe('200 公尺（底部海拔 -200 公尺）')
    expect(value(rows, '海拔')).toBeUndefined()
  })

  it('沒有四季資料時只顯示季節名稱（南半球七月是冬季）', () => {
    const rows = describeTile(tile, false, name, undefined, july)
    expect(value(rows, '季節')).toBe('冬季')
    expect(value(rows, '降水')).toBeUndefined()
  })

  it('小格：所屬地塊、建築物、地形修改、離河距離、泛濫平原', () => {
    const sub: SubTileDto = {
      ...tile,
      tileId: 1000555357508,
      parentTileId: 17574965,
      structure: 'Castle',
      terrainEdit: 'Channel',
      riverDistanceMeters: 812.4,
      floodplain: true,
      riverWidthMeters: 120,
    }
    const rows = describeTile(sub, true, name, undefined, july)
    expect(value(rows, '種類')).toBe('小格（屬於地塊 17574965）')
    expect(value(rows, '建築物')).toBe('城堡')
    expect(value(rows, '地形修改')).toBe('挖出來的河道')
    expect(value(rows, '離河中心')).toBe('812 公尺')
    expect(value(rows, '泛濫平原')).toBe('洪峰時會淹水')
    expect(value(rows, '河寬')).toBe('120 公尺（平均）')
  })

  it('地塊不顯示小格才有的欄位', () => {
    const rows = describeTile({ ...tile, riverDistanceMeters: 5 } as TileDto, false, name, undefined, july)
    expect(value(rows, '離河中心')).toBeUndefined()
  })

  it('有四季資料：當月氣溫、降水比例、積雪、河流型態', () => {
    const seasons: CellSeasonsDto = {
      cellId: 198054,
      temperature: Array(12).fill(25),
      precipitation: [1, 1, 1, 1, 1, 1, 3, 3, 1, 1, 1, 1],
      dischargeRatio: Array(12).fill(1),
      snowMonths: 0b1111_1111_1111,
      frozenMonths: 0,
      flowRegime: 2,
    }
    // 八月初：在七月中與八月中之間，兩個月的降水都是 3（內插之後還是 3）
    const rows = describeTile(tile, false, name, seasons, new Date(Date.UTC(2026, 7, 1, 12)))
    expect(value(rows, '季節')).toBe('冬季、25.0°C')
    // 全年平均 (10 + 6) / 12 = 1.333，當月 3 → 225%
    expect(value(rows, '降水')).toBe('當月是全年平均的 225%')
    expect(value(rows, '地表')).toBe('積雪')
    expect(value(rows, '河流')).toBe('季節河')
    expect(value(rows, '模擬粗格')).toBe('198054')
  })
})

describe('describeVegetation', () => {
  it('林地：高度×2＋疏密', () => {
    expect(describeVegetation('Forest', 0)).toBe('人高的疏林')
    expect(describeVegetation('Forest', 5)).toBe('二層樓高的密林')
    expect(describeVegetation('Forest', 7)).toBe('三層樓高的密林')
  })

  it('草地與灌木叢照各自的分級', () => {
    expect(describeVegetation('Grassland', 2)).toBe('草高到腰部')
    expect(describeVegetation('Shrubland', 3)).toBe('灌木高到高過頭')
  })

  it('不是植被就沒有', () => {
    expect(describeVegetation('Seawater', 3)).toBeNull()
  })

  it('面板裡排在地形後面', () => {
    const rows = describeTile(tile, false, name, undefined, july)
    expect(rows.map((r) => r.label).slice(3, 5)).toEqual(['地形', '植被'])
    expect(value(rows, '植被')).toBe('三層樓高的密林')
  })
})
