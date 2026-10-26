import type { TileDto } from '@/types/tile'
import { METERS_PER_ELEVATION_LEVEL } from './tileMeshBuilder'

/**
 * 近看細分的小格亮度（2026-10-26，使用者回報「陸地一片平坦沒細節」）。
 *
 * 兩件事疊在一起：
 *   - 比四周高的格子偏亮、低的偏暗（山脊亮、谷底暗），不管太陽在哪都看得到；
 *   - 每格依編號小幅偏亮偏暗，同一種地形才看得出一格一格的紋理（純裝飾，不代表地形）。
 * 山坡陰影（坡向受光）在 mesh 的法向量做，見 `slopeNormalOf`。
 */

/** 比四周高（或低）這麼多公尺時，亮度調到最大幅度。 */
export const RELIEF_FULL_SCALE_METERS = 30

/** 高低造成的亮度調整幅度（±）。 */
export const RELIEF_BRIGHTNESS = 0.12

/** 每格隨機的亮度調整幅度（±）。 */
export const JITTER_BRIGHTNESS = 0.05

/**
 * 小格的亮度倍率（1＝不變）。水面（海、湖）不調：水面是平的，深淺斑駁看起來像髒污。
 * @param tile 小格。
 * @param neighbours 小格的鄰居（拿得到幾個算幾個）。
 */
export function subTileBrightness(tile: TileDto, neighbours: readonly TileDto[]): number {
  if (tile.seaLevel > tile.elevationLevel) return 1
  let relief = 0
  if (neighbours.length > 0) {
    let sum = 0
    for (const neighbour of neighbours) sum += neighbour.elevationLevel
    relief = (tile.elevationLevel - sum / neighbours.length) * METERS_PER_ELEVATION_LEVEL
  }
  const reliefTerm =
    Math.max(-1, Math.min(1, relief / RELIEF_FULL_SCALE_METERS)) * RELIEF_BRIGHTNESS
  const jitterTerm = (hash01(tile.tileId) * 2 - 1) * JITTER_BRIGHTNESS
  return 1 + reliefTerm + jitterTerm
}

/** 顏色（0xrrggbb）乘上亮度倍率，每個分量夾在 0~255。 */
export function scaleColorHex(hex: number, factor: number): number {
  const r = Math.min(255, Math.round(((hex >> 16) & 0xff) * factor))
  const g = Math.min(255, Math.round(((hex >> 8) & 0xff) * factor))
  const b = Math.min(255, Math.round((hex & 0xff) * factor))
  return (Math.max(0, r) << 16) | (Math.max(0, g) << 8) | Math.max(0, b)
}

/** 編號 → [0, 1) 的固定亂數。小格編號超過 32 位元，高低兩半先混在一起。 */
function hash01(id: number): number {
  let x = (Math.floor(id / 0x1_0000_0000) ^ (id >>> 0)) >>> 0
  x = Math.imul(x ^ (x >>> 16), 0x7feb352d)
  x = Math.imul(x ^ (x >>> 15), 0x846ca68b)
  x = (x ^ (x >>> 16)) >>> 0
  return x / 0x1_0000_0000
}
