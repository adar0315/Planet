import type { TileDto } from '@/types/tile'

/** 大圓方位角（0°=正北，順時針增加到360°）。 */
export function bearingDegrees(
  fromLat: number,
  fromLon: number,
  toLat: number,
  toLon: number,
): number {
  const toRad = (deg: number) => (deg * Math.PI) / 180
  const lat1 = toRad(fromLat)
  const lat2 = toRad(toLat)
  const deltaLon = toRad(toLon - fromLon)

  const y = Math.sin(deltaLon) * Math.cos(lat2)
  const x = Math.cos(lat1) * Math.sin(lat2) - Math.sin(lat1) * Math.cos(lat2) * Math.cos(deltaLon)

  const bearing = (Math.atan2(y, x) * 180) / Math.PI
  return (bearing + 360) % 360
}

function circularDiff(a: number, b: number): number {
  const diff = Math.abs(a - b) % 360
  return diff > 180 ? 360 - diff : diff
}

/**
 * 六個移動方向「在畫面上的位置」，對應鍵盤上的相對排列：
 *   upLeft(左上)  upRight(右上)
 * left(左)  〔原地〕  right(右)
 *   downLeft(左下)  downRight(右下)
 *
 * 刻意用位置命名而不是用按鍵字母（w/e/a/d/z/x）命名：按鍵是可以自訂的
 * （見 stores/keyBindings.ts），把插槽叫做「w」在玩家把它改綁成 i 之後就會變成謊話。
 * 也刻意不用地理方位（NW/NE…）命名：面向不是北的時候，左上那一格根本不是西北。
 */
export const MOVEMENT_SLOT_ORDER = [
  'upLeft',
  'upRight',
  'right',
  'downRight',
  'downLeft',
  'left',
] as const
export type MovementSlot = (typeof MOVEMENT_SLOT_ORDER)[number]

/**
 * 每個插槽在畫面上的方位角（0°=畫面上方，順時針）。
 *
 * 注意這是**相對於玩家面向**的角度，不是地理方位角——面向正北時兩者剛好相同
 * （所以這組數字跟加入轉向功能之前完全一樣，面向0度的行為沒有任何改變），
 * 面向轉到60度時，畫面上方代表的就是地理上的東北。
 *
 * 六角格鄰居大致每60°一個，但實際方位角是球面幾何算出來的，不會剛好對在這6個角度上，
 * 所以下面用「保持循環順序」的最佳比對來指派。
 */
const CANONICAL_DIRECTIONS = [
  { slot: 'upLeft', bearing: 315 },
  { slot: 'upRight', bearing: 45 },
  { slot: 'right', bearing: 90 },
  { slot: 'downRight', bearing: 135 },
  { slot: 'downLeft', bearing: 225 },
  { slot: 'left', bearing: 270 },
] as const

/**
 * 單一插槽允許的最大方位誤差，超過就不指派（寧可「按了沒反應」也不要把玩家送到完全不符的方向）。
 *
 * 設50度是有原因的：六個插槽的角度並不平均（45/90/135/225/270/315，間隔是45,45,90,45,45,90），
 * 而六角格鄰居是平均60度一個，兩者無法完美對齊——即使用下面「保持循環順序」的最佳比對，
 * 最差的相位（鄰居剛好落在正上/正下，而鍵盤在0°和180°上沒有按鍵）仍會有45度誤差。
 * 門檻若壓在45度以下，實測會讓整片區域（約12%的格子）少掉2個方向鍵、那些格子變成走不到，
 * 反而比「方向偏45度」更難用。所以門檻只當作抓真正異常狀況的保險，不用來修正正常的相位差。
 */
const MAX_DIRECTION_ERROR_DEG = 50

/** 依方位角排序後的正規方向（給循環比對用）。 */
const DIRECTIONS_BY_BEARING = [...CANONICAL_DIRECTIONS].sort((a, b) => a.bearing - b.bearing)

/**
 * 把鄰居分配到六個移動插槽。
 *
 * 用「保持循環順序」的比對，而不是逐配對的貪婪指派：先把鄰居與正規方向各自依方位角排序，
 * 再試所有的旋轉對齊，取總誤差最小的那組，讓六個插槽在玩家周圍的排列順序一定跟實際地理順序一致。
 *
 * 為什麼不能用貪婪：CANONICAL_DIRECTIONS 的間隔並不平均（45,45,90,45,45,90，而六角格需要的是60），
 * 遇到某些旋轉相位時，貪婪法會先讓幾個鄰居搶走最接近的插槽，剩下的鄰居只能配到誤差90°的插槽，
 * 而且完全沒有誤差上限——實際後果是「按右卻往北走」，且不會有任何警示。
 *
 * @param facingDeg 玩家目前面向（0°=正北，順時針）。鏡頭會轉成「面向朝畫面上方」，
 *   所以指派要用**相對面向**的角度來比對，畫面上看到的右邊才會是「右」那一格。
 */
export function assignNeighborsToSlots(
  current: TileDto,
  neighbors: TileDto[],
  facingDeg = 0,
): Map<MovementSlot, TileDto> {
  const map = new Map<MovementSlot, TileDto>()
  if (neighbors.length === 0) return map

  const candidates = neighbors
    .map((tile) => ({
      tile,
      // 地理方位角減掉面向＝這一格出現在畫面上的方位角。
      bearing:
        (bearingDegrees(current.latitude, current.longitude, tile.latitude, tile.longitude) -
          facingDeg +
          360) %
        360,
    }))
    .sort((a, b) => a.bearing - b.bearing)

  const directionCount = DIRECTIONS_BY_BEARING.length

  // 五角格只有5個鄰居、卻有6個插槽，所以要一併決定「空出哪一個」；
  // 六角格則是6對6，omit迴圈只會跑到一個有效解。
  let best: { pairs: { slot: MovementSlot; tile: TileDto; diff: number }[]; total: number } | null =
    null

  const omitOptions = candidates.length < directionCount ? directionCount : 1
  for (let omit = 0; omit < omitOptions; omit++) {
    const usable =
      candidates.length < directionCount
        ? DIRECTIONS_BY_BEARING.filter((_, i) => i !== omit)
        : DIRECTIONS_BY_BEARING
    if (usable.length !== candidates.length) continue

    for (let rotation = 0; rotation < usable.length; rotation++) {
      const pairs = candidates.map((candidate, i) => {
        const direction = usable[(i + rotation) % usable.length]!
        return {
          slot: direction.slot as MovementSlot,
          tile: candidate.tile,
          diff: circularDiff(candidate.bearing, direction.bearing),
        }
      })
      const total = pairs.reduce((sum, p) => sum + p.diff, 0)
      if (!best || total < best.total) best = { pairs, total }
    }
  }

  if (!best) return map

  for (const pair of best.pairs) {
    // 誤差過大的個別配對寧可不給——通常代表這格的鄰居分布跟插槽配置對不上。
    if (pair.diff > MAX_DIRECTION_ERROR_DEG) continue
    map.set(pair.slot, pair.tile)
  }
  return map
}
