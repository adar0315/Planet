import { describe, expect, it } from 'vitest'
import { uvToLatLon } from './overviewPicking'

describe('uvToLatLon', () => {
  it('貼圖中心是經緯度 (0, 0)', () => {
    const { latitude, longitude } = uvToLatLon(0.5, 0.5)
    expect(latitude).toBeCloseTo(0)
    expect(longitude).toBeCloseTo(0)
  })

  it('v＝1 是北極、v＝0 是南極（貼圖設了 flipY）', () => {
    expect(uvToLatLon(0.3, 1).latitude).toBeCloseTo(90)
    expect(uvToLatLon(0.3, 0).latitude).toBeCloseTo(-90)
  })

  it('第 0 欄是經度 −180、往右是東經（跟後端 OverviewTextureGenerator 的像素一致）', () => {
    expect(uvToLatLon(0, 0.5).longitude).toBeCloseTo(-180)
    expect(uvToLatLon(0.75, 0.5).longitude).toBeCloseTo(90)
    expect(uvToLatLon(0.25, 0.5).longitude).toBeCloseTo(-90)
  })

  it('u＝1 繞回 −180，不會給出 180 以外的值', () => {
    expect(uvToLatLon(1, 0.5).longitude).toBeCloseTo(-180)
  })
})
