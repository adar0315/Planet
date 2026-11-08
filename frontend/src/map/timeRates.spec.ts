import { describe, expect, it } from 'vitest'
import {
  DEFAULT_REALTIME_HOURS_PER_SECOND,
  DEFAULT_STEPPED_HOURS_PER_TILE,
  REALTIME_HOURS_PER_SECOND_OPTIONS,
  STEPPED_HOURS_PER_TILE_OPTIONS,
  labelOf,
  snapToOption,
} from './timeRates'

describe('snapToOption', () => {
  it('清單裡的值原樣回傳', () => {
    expect(snapToOption(6, STEPPED_HOURS_PER_TILE_OPTIONS, 1)).toBe(6)
    expect(snapToOption(5 / 60, STEPPED_HOURS_PER_TILE_OPTIONS, 1)).toBe(5 / 60)
  })

  it('不在清單、0、負數、非數字一律退回預設（時間不會停住或倒流）', () => {
    for (const bad of [0, -1, 3, Number.NaN, Infinity, '1', null, undefined]) {
      expect(snapToOption(bad, STEPPED_HOURS_PER_TILE_OPTIONS, DEFAULT_STEPPED_HOURS_PER_TILE)).toBe(DEFAULT_STEPPED_HOURS_PER_TILE)
    }
  })
})

describe('預設檔', () => {
  it('移動模式預設每格 1 小時、即時模式預設每秒 20 分鐘（跟改版前一樣）', () => {
    expect(DEFAULT_STEPPED_HOURS_PER_TILE).toBe(1)
    expect(DEFAULT_REALTIME_HOURS_PER_SECOND * 3).toBeCloseTo(1)
  })

  it('預設值都在清單裡', () => {
    expect(labelOf(DEFAULT_STEPPED_HOURS_PER_TILE, STEPPED_HOURS_PER_TILE_OPTIONS)).toBe('每格 1 小時')
    expect(labelOf(DEFAULT_REALTIME_HOURS_PER_SECOND, REALTIME_HOURS_PER_SECOND_OPTIONS)).toBe('每秒 20 分鐘')
  })

  it('清單由慢到快排列、全部大於 0', () => {
    for (const options of [STEPPED_HOURS_PER_TILE_OPTIONS, REALTIME_HOURS_PER_SECOND_OPTIONS]) {
      for (let i = 0; i < options.length; i++) {
        expect(options[i]!.hours).toBeGreaterThan(i === 0 ? 0 : options[i - 1]!.hours)
      }
    }
  })
})
