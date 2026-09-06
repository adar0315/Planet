import { describe, expect, it } from 'vitest'
import {
  normalizeLongitude,
  projectOrthographic,
  sampleWaterMask,
  smoothstep,
  sunCosZenith,
  unprojectOrthographic,
} from './globeIndicator'

/**
 * 這段數學錯了不會壞掉，只會「看起來像個地球但方向不對」——東西相反、正反面顛倒、
 * 晨昏線落在錯的地方，全都難以目視發現。所以用明確的已知答案把方向與邊界情況釘住。
 */

const CX = 60
const CY = 60
const R = 50

describe('projectOrthographic', () => {
  it('視角中心投影到圓心', () => {
    const p = projectOrthographic(0, 90, 90, CX, CY, R)
    expect(p.x).toBeCloseTo(CX)
    expect(p.y).toBeCloseTo(CY)
    expect(p.isFrontFacing).toBe(true)
  })

  it('東邊在右、西邊在左（跟一般地圖慣例一致）', () => {
    const east = projectOrthographic(0, 30, 0, CX, CY, R) // 中心以東30度
    const west = projectOrthographic(0, -30, 0, CX, CY, R)
    expect(east.x).toBeGreaterThan(CX)
    expect(west.x).toBeLessThan(CX)
  })

  it('北在上、南在下（畫布y向下，所以北的y比較小）', () => {
    const north = projectOrthographic(45, 0, 0, CX, CY, R)
    const south = projectOrthographic(-45, 0, 0, CX, CY, R)
    expect(north.y).toBeLessThan(CY)
    expect(south.y).toBeGreaterThan(CY)
  })

  it('極點落在圓的上下端', () => {
    expect(projectOrthographic(90, 0, 0, CX, CY, R).y).toBeCloseTo(CY - R)
    expect(projectOrthographic(-90, 0, 0, CX, CY, R).y).toBeCloseTo(CY + R)
  })

  it('相對中心±90度落在左右邊緣（silhouette）', () => {
    expect(projectOrthographic(0, 90, 0, CX, CY, R).x).toBeCloseTo(CX + R)
    expect(projectOrthographic(0, -90, 0, CX, CY, R).x).toBeCloseTo(CX - R)
  })

  it('分得出正反面——這正是舊版做不到的事', () => {
    expect(projectOrthographic(0, 0, 0, CX, CY, R).isFrontFacing).toBe(true)
    expect(projectOrthographic(0, 180, 0, CX, CY, R).isFrontFacing).toBe(false)
    expect(projectOrthographic(0, 200, 0, CX, CY, R).isFrontFacing).toBe(false)
  })

  it('正反面的兩點投影位置相同，只能靠 isFrontFacing 區分', () => {
    const front = projectOrthographic(0, 30, 0, CX, CY, R)
    const back = projectOrthographic(0, 150, 0, CX, CY, R) // 180−30
    expect(back.x).toBeCloseTo(front.x)
    expect(back.y).toBeCloseTo(front.y)
    expect(front.isFrontFacing).toBe(true)
    expect(back.isFrontFacing).toBe(false)
  })

  it('所有投影點都落在圓內', () => {
    for (let lat = -90; lat <= 90; lat += 15) {
      for (let lon = -180; lon < 180; lon += 15) {
        const p = projectOrthographic(lat, lon, 0, CX, CY, R)
        const d = Math.hypot(p.x - CX, p.y - CY)
        expect(d).toBeLessThanOrEqual(R + 1e-9)
      }
    }
  })
})

describe('unprojectOrthographic', () => {
  it('圓外回傳null', () => {
    expect(unprojectOrthographic(1.1, 0, 0)).toBeNull()
    expect(unprojectOrthographic(0.8, 0.8, 0)).toBeNull() // 0.8²+0.8²=1.28
  })

  it('圓心對應視角中心', () => {
    const p = unprojectOrthographic(0, 0, 42)!
    expect(p.latitudeDeg).toBeCloseTo(0)
    expect(p.longitudeDeg).toBeCloseTo(42)
  })

  it('與 projectOrthographic 互為反函式（正面的點）', () => {
    const refLon = 25
    for (const [lat, lon] of [
      [0, 25],
      [30, 40],
      [-45, 10],
      [60, -20],
      [10, 100],
    ]) {
      const p = projectOrthographic(lat!, lon!, refLon, 0, 0, 1)
      // 畫布y向下，反投影的v是向上，所以要翻號
      const back = unprojectOrthographic(p.x, -p.y, refLon)!
      expect(back.latitudeDeg).toBeCloseTo(lat!, 6)
      expect(back.longitudeDeg).toBeCloseTo(normalizeLongitude(lon!), 6)
    }
  })

  it('上緣是北極、下緣是南極', () => {
    expect(unprojectOrthographic(0, 1, 0)!.latitudeDeg).toBeCloseTo(90)
    expect(unprojectOrthographic(0, -1, 0)!.latitudeDeg).toBeCloseTo(-90)
  })
})

describe('sunCosZenith', () => {
  it('太陽正上方時為1', () => {
    expect(sunCosZenith(20, 50, 20, 50)).toBeCloseTo(1)
  })

  it('地球另一端為-1（半夜）', () => {
    expect(sunCosZenith(0, 180, 0, 0)).toBeCloseTo(-1)
  })

  it('相距90度時為0（日出/日落的瞬間）', () => {
    expect(sunCosZenith(0, 90, 0, 0)).toBeCloseTo(0)
    expect(sunCosZenith(90, 0, 0, 0)).toBeCloseTo(0)
  })

  it('白天為正、夜晚為負', () => {
    expect(sunCosZenith(0, 30, 0, 0)).toBeGreaterThan(0)
    expect(sunCosZenith(0, 120, 0, 0)).toBeLessThan(0)
  })
})

describe('smoothstep', () => {
  it('邊界外夾在0與1', () => {
    expect(smoothstep(-0.1, 0.1, -5)).toBe(0)
    expect(smoothstep(-0.1, 0.1, 5)).toBe(1)
  })

  it('中點為0.5且單調遞增', () => {
    expect(smoothstep(-0.1, 0.1, 0)).toBeCloseTo(0.5)
    expect(smoothstep(-0.1, 0.1, -0.05)).toBeLessThan(smoothstep(-0.1, 0.1, 0.05))
  })
})

describe('normalizeLongitude', () => {
  it('繞回到 [-180,180)', () => {
    expect(normalizeLongitude(190)).toBeCloseTo(-170)
    expect(normalizeLongitude(-190)).toBeCloseTo(170)
    expect(normalizeLongitude(540)).toBeCloseTo(-180)
    expect(normalizeLongitude(45)).toBeCloseTo(45)
  })
})

describe('sampleWaterMask', () => {
  // 4x2 遮罩：上排（北半球）全水，下排（南半球）全陸
  const mask = new Uint8Array([1, 1, 1, 1, 0, 0, 0, 0])

  it('北半球取到水、南半球取到陸', () => {
    expect(sampleWaterMask(mask, 4, 2, 45, 0)).toBe(true)
    expect(sampleWaterMask(mask, 4, 2, -45, 0)).toBe(false)
  })

  it('經度對應到正確的行（最左＝西經180）', () => {
    const lonMask = new Uint8Array([1, 0, 0, 0, 1, 0, 0, 0])
    expect(sampleWaterMask(lonMask, 4, 2, 45, -180)).toBe(true) // 第0行
    expect(sampleWaterMask(lonMask, 4, 2, 45, -90)).toBe(false) // 第1行
  })

  it('極點與換日線邊界不會取樣越界', () => {
    expect(() => sampleWaterMask(mask, 4, 2, 90, 180)).not.toThrow()
    expect(() => sampleWaterMask(mask, 4, 2, -90, -180)).not.toThrow()
    expect(sampleWaterMask(mask, 4, 2, 90, 180)).toBe(true)
    expect(sampleWaterMask(mask, 4, 2, -90, 179.999)).toBe(false)
  })
})
