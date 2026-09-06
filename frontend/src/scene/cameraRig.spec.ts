import { describe, expect, it } from 'vitest'
import { shortestFacingDelta, smoothstep } from './cameraRig'

/**
 * 轉向動畫的核心是「往哪個方向轉」——這個專案已經因為方向的正負號踩過好幾次坑
 * （見 CLAUDE.md「鄰居座標的推導」與 sphereCoordinates.ts 的三次handedness坑）。
 * shortestFacingDelta 決定的正是這件事，所以獨立出來直接測，而不是只能靠瀏覽器裡
 * 目視轉向動畫「看起來對不對」。
 */
describe('shortestFacingDelta', () => {
  it('正常的單步右轉（60度）：delta是+60', () => {
    expect(shortestFacingDelta(0, 60)).toBe(60)
    expect(shortestFacingDelta(120, 180)).toBe(60)
  })

  it('正常的單步左轉（60度）：delta是-60', () => {
    expect(shortestFacingDelta(60, 0)).toBe(-60)
    expect(shortestFacingDelta(180, 120)).toBe(-60)
  })

  it('跨過0/360接縫右轉：從300往右轉一次，delta是+60（不是-300繞一大圈）', () => {
    expect(shortestFacingDelta(300, 0)).toBe(60)
  })

  it('跨過0/360接縫左轉：從0往左轉一次，delta是-60（不是+300繞一大圈）', () => {
    expect(shortestFacingDelta(0, 300)).toBe(-60)
  })

  it('起點是未取模的累加值（例如已經轉了好幾圈）：只看跟目標的最短路徑，不受圈數影響', () => {
    expect(shortestFacingDelta(720, 60)).toBe(60) // 720°≡0°，等同從0轉到60
    expect(shortestFacingDelta(-300, 60)).toBe(0) // -300°≡60°，已經在目標上，delta是0
  })

  it('起點已經等於正規化後的目標：delta是0（不會無謂地觸發一次動畫）', () => {
    expect(shortestFacingDelta(60, 60)).toBe(0)
  })

  it('完整轉一圈（360度）：等於沒轉，delta是0', () => {
    expect(shortestFacingDelta(0, 0)).toBe(0)
  })

  it('結果永遠落在[-180,180]範圍內（±180是同一個角度，邊界剛好差180度時二選一都合法）', () => {
    for (let from = 0; from < 360; from += 15) {
      for (let to = 0; to < 360; to += 15) {
        const delta = shortestFacingDelta(from, to)
        expect(delta).toBeGreaterThanOrEqual(-180)
        expect(delta).toBeLessThanOrEqual(180)
      }
    }
  })

  it('剛好差180度：固定回傳-180（跟+180是同一個角度，只是取一個確定的方向）', () => {
    expect(shortestFacingDelta(0, 180)).toBe(-180)
  })
})

describe('smoothstep', () => {
  it('端點：t=0時是0，t=1時是1', () => {
    expect(smoothstep(0)).toBe(0)
    expect(smoothstep(1)).toBe(1)
  })

  it('中點：t=0.5時是0.5（對稱曲線）', () => {
    expect(smoothstep(0.5)).toBeCloseTo(0.5, 10)
  })

  it('單調遞增：不會在中途反向', () => {
    let previous = -Infinity
    for (let t = 0; t <= 1; t += 0.05) {
      const value = smoothstep(t)
      expect(value).toBeGreaterThanOrEqual(previous)
      previous = value
    }
  })

  it('兩端附近的斜率比線性內插小（這是跟線性內插的關鍵差異——沒有速度突變）', () => {
    const nearStart = smoothstep(0.05) - smoothstep(0)
    const nearMiddle = smoothstep(0.525) - smoothstep(0.475)
    expect(nearStart).toBeLessThan(nearMiddle)
  })
})
