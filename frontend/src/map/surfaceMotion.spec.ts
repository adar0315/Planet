import { describe, expect, it } from 'vitest'
import * as THREE from 'three'
import { latLonToVector3 } from './sphereCoordinates'
import {
  approach,
  forwardForHeading,
  headingDegOf,
  latLonOf,
  moveAlongSurface,
  nearestIndex,
  rightOf,
  turnInPlace,
} from './surfaceMotion'

/**
 * 連續移動的方向正負號是整個改版最容易錯、又最難用肉眼抓到的地方
 * （這個專案已經因為 handedness 踩過三次坑，見 CLAUDE.md）。
 * 所以每個方向都用「走一步之後經緯度怎麼變」來驗證，而不是只看向量本身。
 */

const R = 1_852_259.92

function at(lat: number, lon: number) {
  return latLonToVector3(lat, lon, 1)
}

function facing(position: THREE.Vector3, headingDeg: number) {
  return forwardForHeading(position, headingDeg)
}

describe('方向慣例', () => {
  it('面向正北時，右方是正東（經度增加）', () => {
    const p = at(10, 20)
    const f = facing(p, 0)
    const right = rightOf(p, f)
    const stepped = p.clone().addScaledVector(right, 1e-4).normalize()
    expect(latLonOf(stepped).longitude).toBeGreaterThan(20)
  })

  it('原地右轉 90° 會從面北變成面東', () => {
    const p = at(0, 0)
    const f = facing(p, 0)
    turnInPlace(p, f, 90)
    expect(headingDegOf(p, f)).toBeCloseTo(90, 6)
  })

  it('原地左轉（負角度）從面北變成面西', () => {
    const p = at(30, -45)
    const f = facing(p, 0)
    turnInPlace(p, f, -90)
    expect(headingDegOf(p, f)).toBeCloseTo(270, 6)
  })

  it('方位角換算來回不變形', () => {
    const p = at(-25, 130)
    for (const deg of [0, 37, 90, 179.5, 270, 359]) {
      expect(headingDegOf(p, facing(p, deg))).toBeCloseTo(deg, 6)
    }
  })
})

describe('moveAlongSurface', () => {
  it('在赤道面北前進，緯度增加的量等於 距離/半徑', () => {
    const p = at(0, 0)
    const f = facing(p, 0)
    moveAlongSurface(p, f, 10_000, 0, R)
    const { latitude, longitude } = latLonOf(p)
    expect(latitude).toBeCloseTo((10_000 / R) * (180 / Math.PI), 9)
    expect(longitude).toBeCloseTo(0, 9)
  })

  it('後退是負的前進：面北後退緯度減少', () => {
    const p = at(0, 0)
    const f = facing(p, 0)
    moveAlongSurface(p, f, -10_000, 0, R)
    expect(latLonOf(p).latitude).toBeLessThan(0)
    expect(headingDegOf(p, f)).toBeCloseTo(0, 6) // 後退不會讓人轉身
  })

  it('面北往右橫移，經度增加、面向不變', () => {
    const p = at(0, 0)
    const f = facing(p, 0)
    moveAlongSurface(p, f, 0, 10_000, R)
    expect(latLonOf(p).longitude).toBeGreaterThan(0)
    expect(latLonOf(p).latitude).toBeCloseTo(0, 9)
    expect(headingDegOf(p, f)).toBeCloseTo(0, 6)
  })

  it('斜向移動（前＋右）走的距離是合成向量的長度，不是兩者相加', () => {
    const p = at(0, 0)
    const start = p.clone()
    const f = facing(p, 0)
    moveAlongSurface(p, f, 3000, 4000, R)
    expect(start.angleTo(p) * R).toBeCloseTo(5000, 3)
  })

  it('一直往前走會沿大圓繞星球一圈回到原點，面向也回到原本的方向', () => {
    const p = at(12, 34)
    const f = facing(p, 57)
    const startP = p.clone()
    const startF = f.clone()
    const steps = 1000
    const circumference = 2 * Math.PI * R
    for (let i = 0; i < steps; i++) moveAlongSurface(p, f, circumference / steps, 0, R)
    expect(p.distanceTo(startP)).toBeLessThan(1e-9)
    expect(f.distanceTo(startF)).toBeLessThan(1e-9)
  })

  it('走過北極不會出現 NaN，而且會繼續往南（面向沿大圓平行傳輸）', () => {
    const p = at(89.99, 0)
    const f = facing(p, 0)
    // 0.01° 到極點，再多走一些
    moveAlongSurface(p, f, (0.05 * Math.PI * R) / 180, 0, R)
    expect(Number.isFinite(p.x) && Number.isFinite(f.x)).toBe(true)
    expect(latLonOf(p).latitude).toBeLessThan(90)
    expect(Math.abs(f.dot(p))).toBeLessThan(1e-12) // 仍然是切向量
    expect(f.length()).toBeCloseTo(1, 12)
    // 再往前走，緯度要下降（已經越過極點往另一側走）
    const latBefore = latLonOf(p).latitude
    moveAlongSurface(p, f, 1000, 0, R)
    expect(latLonOf(p).latitude).toBeLessThan(latBefore)
  })

  it('長時間反覆移動與轉向，面向仍是單位切向量（誤差不累積）', () => {
    const p = at(40, -100)
    const f = facing(p, 10)
    for (let i = 0; i < 100_000; i++) {
      moveAlongSurface(p, f, 37, (i % 7) - 3, R)
      turnInPlace(p, f, 0.3)
    }
    expect(p.length()).toBeCloseTo(1, 12)
    expect(f.length()).toBeCloseTo(1, 12)
    expect(Math.abs(f.dot(p))).toBeLessThan(1e-12)
  })
})

describe('nearestIndex', () => {
  it('回傳內積最大（角距離最小）的格心', () => {
    const p = at(0, 0)
    const centers = [at(1, 0), at(0, 0.1), at(-2, 3)]
    expect(nearestIndex(p, centers)).toBe(1)
  })

  it('空陣列回傳 -1', () => {
    expect(nearestIndex(at(0, 0), [])).toBe(-1)
  })
})

describe('approach', () => {
  it('每次最多靠近 maxStep，不會衝過頭', () => {
    expect(approach(0, 10, 3)).toBe(3)
    expect(approach(9, 10, 3)).toBe(10)
    expect(approach(10, 0, 4)).toBe(6)
    expect(approach(1, 0, 4)).toBe(0)
  })
})
