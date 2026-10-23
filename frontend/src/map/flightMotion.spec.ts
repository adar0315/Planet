import { describe, expect, it } from 'vitest'
import {
  FLIGHT_TIERS,
  MAX_FLIGHT_ALTITUDE_METERS,
  MIN_VERTICAL_SPEED_MPS,
  TAKEOFF_ALTITUDE_METERS,
  VERTICAL_RATE_PER_SEC,
  flightAccelerationMps2,
  steppedHoursPerCrossing,
  verticalSpeedAt,
} from './flightMotion'

const WALK = 200
/** 格心間距（公尺），跟 playerController 的跨格偵測同一個前提。 */
const CELL_SPACING_METERS = 1_700
const MAX_FRAME_DT_SEC = 0.1

describe('FLIGHT_TIERS', () => {
  it('一幀最多跨一格：最高速 × 每幀上限遠小於格心間距', () => {
    const top = Math.max(...FLIGHT_TIERS.map((t) => t.speedMps))
    expect(top * MAX_FRAME_DT_SEC).toBeLessThan(CELL_SPACING_METERS / 2)
  })

  it('由慢到快排列', () => {
    for (let i = 1; i < FLIGHT_TIERS.length; i++) {
      expect(FLIGHT_TIERS[i]!.speedMps).toBeGreaterThan(FLIGHT_TIERS[i - 1]!.speedMps)
    }
  })
})

describe('verticalSpeedAt', () => {
  it('貼地時有下限，離得開地面', () => {
    expect(verticalSpeedAt(0)).toBe(MIN_VERTICAL_SPEED_MPS)
    expect(verticalSpeedAt(-50)).toBe(MIN_VERTICAL_SPEED_MPS)
  })

  it('高空時跟高度成比例', () => {
    expect(verticalSpeedAt(100_000)).toBeCloseTo(100_000 * VERTICAL_RATE_PER_SEC)
  })

  it('從起飛高度爬到最高約 8 秒', () => {
    let altitude = TAKEOFF_ALTITUDE_METERS
    let seconds = 0
    const dt = 1 / 60
    while (altitude < MAX_FLIGHT_ALTITUDE_METERS && seconds < 60) {
      altitude += verticalSpeedAt(altitude) * dt
      seconds += dt
    }
    expect(seconds).toBeGreaterThan(5)
    expect(seconds).toBeLessThan(10)
  })
})

describe('flightAccelerationMps2', () => {
  it('每一檔約 2 秒到全速', () => {
    for (const tier of FLIGHT_TIERS) {
      expect(tier.speedMps / flightAccelerationMps2(tier.speedMps, 0, false)).toBeCloseTo(2)
    }
  })

  it('換低檔時用目前速度當尺度，不會拖很久', () => {
    const fast = FLIGHT_TIERS[2]!.speedMps
    const slow = FLIGHT_TIERS[0]!.speedMps
    const seconds = (fast - slow) / flightAccelerationMps2(slow, fast, true)
    expect(seconds).toBeLessThan(2)
  })
})

describe('steppedHoursPerCrossing', () => {
  it('走路每格 1 小時，不論速度', () => {
    expect(steppedHoursPerCrossing(false, 600, WALK)).toBe(1)
    expect(steppedHoursPerCrossing(false, 3_000, WALK)).toBe(1)
  })

  it('飛得越快，每格的時間越少', () => {
    expect(steppedHoursPerCrossing(true, 250, WALK)).toBeCloseTo(0.8) // 48 分鐘
    expect(steppedHoursPerCrossing(true, 3_000, WALK) * 60).toBeCloseTo(4) // 4 分鐘
  })

  it('比走路慢時不超過 1 小時', () => {
    expect(steppedHoursPerCrossing(true, 50, WALK)).toBe(1)
    expect(steppedHoursPerCrossing(true, 0, WALK)).toBe(1)
    expect(steppedHoursPerCrossing(true, Number.NaN, WALK)).toBe(1)
  })

  it('後退（負速度）用絕對值', () => {
    expect(steppedHoursPerCrossing(true, -1_000, WALK)).toBeCloseTo(0.2)
  })
})
