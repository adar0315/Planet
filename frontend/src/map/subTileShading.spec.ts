import { describe, expect, it } from 'vitest'
import * as THREE from 'three'
import {
  JITTER_BRIGHTNESS,
  RELIEF_BRIGHTNESS,
  scaleColorHex,
  subTileBrightness,
} from './subTileShading'
import { slopeNormalOf, type TileMeshOptions } from './tileMeshBuilder'
import { latLonToVector3, tangentBasisAt } from './sphereCoordinates'
import type { TileDto } from '@/types/tile'

const OPTIONS: TileMeshOptions = {
  renderRadius: 50,
  planetRadiusMeters: 1_851_000,
  edgeLengthMeters: 250,
}

function tile(id: number, lat: number, lon: number, elevationLevel = 10, seaLevel = 0): TileDto {
  return {
    tileId: id,
    shape: 'Hexagon',
    faceIndex: 0,
    latitude: lat,
    longitude: lon,
    terrain: 'Forest',
    heightCategory: 0,
    elevationLevel,
    seaLevel,
  }
}

/** 中心格＋六個環繞的鄰居，鄰居高度由 levelOf(東向分量, 北向分量) 決定。 */
function ring(levelOf: (east: number, north: number) => number, spacingDeg = 0.004) {
  return Array.from({ length: 6 }, (_, i) => {
    const angle = (i / 6) * Math.PI * 2
    const east = Math.cos(angle)
    const north = Math.sin(angle)
    return tile(i + 1, spacingDeg * north, spacingDeg * east, levelOf(east, north))
  })
}

describe('slopeNormalOf', () => {
  it('四周一樣高時朝正上方', () => {
    const normal = slopeNormalOf(
      tile(0, 0, 0),
      ring(() => 10),
      OPTIONS,
    )
    const up = latLonToVector3(0, 0, 1)
    expect(normal.angleTo(up)).toBeLessThan(1e-6)
  })

  it('東邊高、西邊低時往西傾（坡面朝下坡那一側）', () => {
    const centre = tile(0, 0, 0)
    const normal = slopeNormalOf(
      centre,
      ring((east) => 10 + Math.round(east * 4)),
      OPTIONS,
    )
    const { east, north } = tangentBasisAt(latLonToVector3(0, 0, 1))
    expect(normal.dot(east)).toBeLessThan(-0.05)
    expect(Math.abs(normal.dot(north))).toBeLessThan(0.02)
    expect(normal.length()).toBeCloseTo(1, 6)
  })

  it('不會改到傳進來的 target 以外的東西，且回傳的就是 target', () => {
    const target = new THREE.Vector3()
    expect(
      slopeNormalOf(
        tile(0, 0, 0),
        ring(() => 10),
        OPTIONS,
        target,
      ),
    ).toBe(target)
  })
})

describe('subTileBrightness', () => {
  it('比四周高的偏亮、低的偏暗，差距超過隨機紋理的幅度', () => {
    const neighbours = ring(() => 10)
    const high = subTileBrightness(tile(0, 0, 0, 20), neighbours)
    const low = subTileBrightness(tile(0, 0, 0, 0), neighbours)
    expect(high).toBeGreaterThan(1 + RELIEF_BRIGHTNESS - JITTER_BRIGHTNESS - 1e-9)
    expect(low).toBeLessThan(1 - RELIEF_BRIGHTNESS + JITTER_BRIGHTNESS + 1e-9)
  })

  it('同樣高度時只差隨機紋理，且同一格每次都一樣', () => {
    const neighbours = ring(() => 10)
    const values = Array.from({ length: 50 }, (_, i) =>
      subTileBrightness(tile(1_000_000_000_000 + i, 0, 0), neighbours),
    )
    for (const value of values) expect(Math.abs(value - 1)).toBeLessThanOrEqual(JITTER_BRIGHTNESS)
    expect(new Set(values).size).toBeGreaterThan(40)
    expect(subTileBrightness(tile(1_000_000_000_007, 0, 0), neighbours)).toBe(values[7])
  })

  it('水面不調', () => {
    expect(
      subTileBrightness(
        tile(0, 0, 0, 5, 10),
        ring(() => 0),
      ),
    ).toBe(1)
  })
})

describe('scaleColorHex', () => {
  it('每個分量乘上倍率、夾在 0~255', () => {
    expect(scaleColorHex(0x204060, 1)).toBe(0x204060)
    expect(scaleColorHex(0x204060, 0.5)).toBe(0x102030)
    expect(scaleColorHex(0xf0f0f0, 2)).toBe(0xffffff)
  })
})
