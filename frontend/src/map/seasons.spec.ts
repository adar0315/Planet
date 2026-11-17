import { describe, expect, it } from 'vitest'
import type { CellSeasonsDto, SubTileDto, TileDto } from '@/types/tile'
import {
  interpolateMonthly,
  monthFlag,
  monthPosition,
  riverCoversSubTile,
  riverWidthNow,
  seasonName,
  seasonalTerrain,
} from './seasons'

const months = (f: (m: number) => number) => Array.from({ length: 12 }, (_, m) => f(m))

describe('monthPosition', () => {
  it('月中剛好落在那個月', () => {
    // 12 個等長的月：7 月的月中是一年中第 197.7 天（7 月 17 日傍晚）
    const p = monthPosition(new Date(Date.UTC(2050, 6, 18)))
    expect(p.month).toBe(6)
    expect(p.t).toBeLessThan(0.1)
  })

  it('1 月 1 日在 12 月與 1 月之間', () => {
    const p = monthPosition(new Date(Date.UTC(2050, 0, 1)))
    expect(p.month).toBe(11)
    expect(p.next).toBe(0)
    expect(p.t).toBeGreaterThan(0.4)
  })
})

describe('interpolateMonthly', () => {
  it('在兩個月之間線性內插', () => {
    const values = months((m) => m * 10)
    expect(interpolateMonthly(values, { month: 3, next: 4, t: 0.25 })).toBeCloseTo(32.5)
  })

  it('沒有資料用 fallback', () => {
    expect(interpolateMonthly([], { month: 0, next: 1, t: 0 }, 7)).toBe(7)
  })
})

describe('monthFlag', () => {
  it('取比較近的那個月', () => {
    const mask = 1 << 0 // 只有 1 月
    expect(monthFlag(mask, { month: 0, next: 1, t: 0.4 })).toBe(true)
    expect(monthFlag(mask, { month: 0, next: 1, t: 0.6 })).toBe(false)
  })
})

describe('seasonName', () => {
  it('南北半球相反', () => {
    const july = new Date(Date.UTC(2050, 6, 1))
    expect(seasonName(40, july)).toBe('夏')
    expect(seasonName(-40, july)).toBe('冬')
  })
})

describe('riverWidthNow', () => {
  it('寬度跟流量的平方根成正比，乾掉是 0', () => {
    expect(riverWidthNow(100, 4)).toBeCloseTo(200)
    expect(riverWidthNow(100, 0.01)).toBe(0)
  })
})

describe('seasonalTerrain', () => {
  const dryInJuly: CellSeasonsDto = {
    cellId: 7,
    temperature: months(() => 10),
    precipitation: months(() => 1),
    dischargeRatio: months((m) => (m === 6 ? 0 : 1.1)),
    snowMonths: 1 << 0,
    frozenMonths: 0,
    flowRegime: 3,
  }
  const lookup = (id: number) => (id === 7 ? dryInJuly : undefined)
  const july = { month: 6, next: 7, t: 0 }
  const january = { month: 0, next: 1, t: 0 }
  const tile: TileDto = {
    tileId: 1,
    shape: 'Hexagon',
    faceIndex: 0,
    latitude: 0,
    longitude: 0,
    terrain: 'Grassland',
    heightCategory: 0,
    elevationLevel: 10,
    seaLevel: 0,
    sourceCellId: 7,
    riverWidthMeters: 50,
    riverCellId: 7,
  }

  it('拉遠：範圍裡有河的地塊整格是河，枯水期是河床', () => {
    expect(seasonalTerrain(tile, { month: 3, next: 4, t: 0 }, lookup, false)).toBe('Freshwater')
    expect(seasonalTerrain(tile, july, lookup, false)).toBe('Gravel')
  })

  it('近看：只有河道附近的小格是水', () => {
    const near: SubTileDto = { ...tile, parentTileId: 1, riverDistanceMeters: 100 }
    const far: SubTileDto = { ...tile, parentTileId: 1, riverDistanceMeters: 900 }
    const spring = { month: 3, next: 4, t: 0 }
    expect(seasonalTerrain(near, spring, lookup, true)).toBe('Freshwater')
    expect(seasonalTerrain(far, spring, lookup, true)).toBe('Grassland')
  })

  it('積雪蓋住陸地', () => {
    const land: TileDto = { ...tile, riverWidthMeters: undefined, riverCellId: undefined }
    expect(seasonalTerrain(land, january, lookup, false)).toBe('SnowField')
    expect(seasonalTerrain(land, july, lookup, false)).toBe('Grassland')
  })
})

describe('riverCoversSubTile（河裡不種樹）', () => {
  const july = { month: 6, next: 7, t: 0 }
  const forestSub = (extra: Partial<SubTileDto>): SubTileDto => ({
    tileId: 1,
    parentTileId: 2,
    shape: 'Hexagon',
    faceIndex: 0,
    latitude: 0,
    longitude: 0,
    terrain: 'Forest',
    heightCategory: 5,
    elevationLevel: 20,
    seaLevel: 0,
    ...extra,
  })
  const riverSeasons = (ratio: number): CellSeasonsDto => ({
    cellId: 9,
    temperature: months(() => 20),
    precipitation: months(() => 1),
    dischargeRatio: months(() => ratio),
    snowMonths: 0,
    frozenMonths: 0,
    flowRegime: 1,
  })

  it('沒有河：不蓋住', () => {
    expect(riverCoversSubTile(forestSub({}), july, () => undefined)).toBe(false)
  })

  it('河道（畫成河的那幾格，至少 220 公尺內）：蓋住，乾掉的河床也算', () => {
    const lookup = () => riverSeasons(0)
    expect(riverCoversSubTile(forestSub({ riverWidthMeters: 40, riverCellId: 9, riverDistanceMeters: 200 }), july, lookup)).toBe(true)
    expect(riverCoversSubTile(forestSub({ riverWidthMeters: 40, riverCellId: 9, riverDistanceMeters: 300 }), july, lookup)).toBe(false)
  })

  it('跟畫面上色一致：蓋住的格子 seasonalTerrain 不是林地', () => {
    const sub = forestSub({ riverWidthMeters: 40, riverCellId: 9, riverDistanceMeters: 100 })
    const lookup = () => riverSeasons(1)
    expect(riverCoversSubTile(sub, july, lookup)).toBe(true)
    expect(seasonalTerrain(sub, july, lookup, true)).toBe('Freshwater')
  })

  it('泛濫平原只在洪峰時蓋住', () => {
    const sub = forestSub({ riverWidthMeters: 40, riverCellId: 9, riverDistanceMeters: 600, floodplain: true })
    expect(riverCoversSubTile(sub, july, () => riverSeasons(2))).toBe(true)
    expect(riverCoversSubTile(sub, july, () => riverSeasons(1))).toBe(false)
  })
})
