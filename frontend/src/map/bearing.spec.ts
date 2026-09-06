import { describe, expect, it } from 'vitest'
import {
  assignNeighborsToSlots,
  bearingDegrees,
  MOVEMENT_SLOT_ORDER,
  type MovementSlot,
} from './bearing'
import type { TileDto } from '@/types/tile'

/**
 * 方向鍵指派的回歸測試。
 *
 * 背景：六個鍵的角度並不平均（45/90/135/225/270/315，間隔45,45,90,45,45,90），而六角格鄰居
 * 是平均60度一個，兩者無法完美對齊。舊版用「逐配對貪婪指派」且沒有誤差上限，在某些旋轉相位下
 * 會把正北的鄰居配給 d（正東），實測誤差高達90度——症狀是「按D卻往北走」，而且完全沒有警示。
 *
 * 現在改成「保持循環順序」的最佳比對。以下測試鎖住兩件事：
 *   1. 最大誤差不得再出現90度那種等級（含當初出問題的相位）
 *   2. 六個鍵繞著玩家的排列順序必須跟實際地理順序一致
 */

/** 各插槽預期代表的（相對面向的）方位角。 */
const INTENDED: Record<MovementSlot, number> = {
  upLeft: 315,
  upRight: 45,
  right: 90,
  downRight: 135,
  downLeft: 225,
  left: 270,
}

/** 六個插槽順時針繞一圈的順序（從畫面正上方開始）。 */
const CLOCKWISE_SLOTS: MovementSlot[] = [
  'upRight',
  'right',
  'downRight',
  'downLeft',
  'left',
  'upLeft',
]

const ORIGIN: TileDto = {
  tileId: 0,
  shape: 'Hexagon',
  faceIndex: 0,
  latitude: 0.02,
  longitude: 0.06,
  terrain: 'Grassland',
  heightCategory: 0,
  elevationLevel: 100,
  seaLevel: 0,
}

/** 從起點沿指定方位角走指定角距離，算出目的地經緯度（球面標準公式）。 */
function destination(latDeg: number, lonDeg: number, bearingDeg: number, distanceDeg: number) {
  const rad = Math.PI / 180
  const deg = 180 / Math.PI
  const lat1 = latDeg * rad
  const lon1 = lonDeg * rad
  const b = bearingDeg * rad
  const d = distanceDeg * rad

  const lat2 = Math.asin(Math.sin(lat1) * Math.cos(d) + Math.cos(lat1) * Math.sin(d) * Math.cos(b))
  const lon2 =
    lon1 + Math.atan2(Math.sin(b) * Math.sin(d) * Math.cos(lat1), Math.cos(d) - Math.sin(lat1) * Math.sin(lat2))
  return { latitude: lat2 * deg, longitude: lon2 * deg }
}

/** 依指定的一組方位角，造出圍繞ORIGIN的鄰居。 */
function neighborsAtBearings(bearings: number[]): TileDto[] {
  return bearings.map((b, i) => ({ ...ORIGIN, tileId: i + 1, ...destination(ORIGIN.latitude, ORIGIN.longitude, b, 0.03) }))
}

function circularDiff(a: number, b: number): number {
  const diff = Math.abs(a - b) % 360
  return diff > 180 ? 360 - diff : diff
}

/** 這組指派裡，單一插槽最大偏離其代表方位幾度（facingDeg 要跟產生這組指派時用的一致）。 */
function worstError(map: Map<MovementSlot, TileDto>, facingDeg = 0): number {
  let worst = 0
  for (const [slot, tile] of map) {
    const actual =
      (bearingDegrees(ORIGIN.latitude, ORIGIN.longitude, tile.latitude, tile.longitude) -
        facingDeg +
        360) %
      360
    worst = Math.max(worst, circularDiff(actual, INTENDED[slot]))
  }
  return worst
}

/** 指派出來的插槽，依實際方位角排序後必須符合 CLOCKWISE_SLOTS 的循環順序。 */
function preservesCyclicOrder(map: Map<MovementSlot, TileDto>): boolean {
  const ranks = [...map.entries()]
    .map(([slot, tile]) => ({
      slot,
      bearing: bearingDegrees(ORIGIN.latitude, ORIGIN.longitude, tile.latitude, tile.longitude),
    }))
    .sort((p, q) => p.bearing - q.bearing)
    .map((p) => CLOCKWISE_SLOTS.indexOf(p.slot))

  // 循環序列繞一圈最多只能「從大跳回小」一次
  let wraps = 0
  for (let i = 0; i < ranks.length; i++) {
    if (ranks[i]! > ranks[(i + 1) % ranks.length]!) wraps++
  }
  return wraps <= 1
}

// 從相位0到55度，涵蓋六角格所有可能的旋轉相位（60度一個週期）
const PHASES = [0, 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55]

describe('bearingDegrees', () => {
  it('正北=0、正東=90、正南=180、正西=270', () => {
    const at = (b: number) => {
      const p = destination(ORIGIN.latitude, ORIGIN.longitude, b, 0.03)
      return bearingDegrees(ORIGIN.latitude, ORIGIN.longitude, p.latitude, p.longitude)
    }
    expect(at(0)).toBeCloseTo(0, 1)
    expect(at(90)).toBeCloseTo(90, 1)
    expect(at(180)).toBeCloseTo(180, 1)
    expect(at(270)).toBeCloseTo(270, 1)
  })
})

describe('assignNeighborsToSlots — 六角形', () => {
  it.each(PHASES)('相位%i度：六個鍵全部指派得到', (phase) => {
    const neighbors = neighborsAtBearings([0, 60, 120, 180, 240, 300].map((b) => b + phase))
    expect(assignNeighborsToSlots(ORIGIN, neighbors).size).toBe(6)
  })

  it.each(PHASES)('相位%i度：最大誤差不超過45度（舊版貪婪法會到90度）', (phase) => {
    const neighbors = neighborsAtBearings([0, 60, 120, 180, 240, 300].map((b) => b + phase))
    expect(worstError(assignNeighborsToSlots(ORIGIN, neighbors))).toBeLessThanOrEqual(45.5)
  })

  it.each(PHASES)('相位%i度：保持循環順序（六個鍵繞著玩家的順序跟地理一致）', (phase) => {
    const neighbors = neighborsAtBearings([0, 60, 120, 180, 240, 300].map((b) => b + phase))
    expect(preservesCyclicOrder(assignNeighborsToSlots(ORIGIN, neighbors))).toBe(true)
  })

  it('相位0度（當初出問題的相位）：d 不會被指去正北、a 不會被指去正南', () => {
    const neighbors = neighborsAtBearings([0, 60, 120, 180, 240, 300])
    const map = assignNeighborsToSlots(ORIGIN, neighbors)

    const bearingOf = (slot: MovementSlot) => {
      const t = map.get(slot)!
      return bearingDegrees(ORIGIN.latitude, ORIGIN.longitude, t.latitude, t.longitude)
    }
    expect(circularDiff(bearingOf('right'), 0)).toBeGreaterThan(45) // 右 不該是正北
    expect(circularDiff(bearingOf('left'), 180)).toBeGreaterThan(45) // 左 不該是正南
  })

  it('每個鄰居最多只被指派給一個鍵', () => {
    const neighbors = neighborsAtBearings([0, 60, 120, 180, 240, 300])
    const map = assignNeighborsToSlots(ORIGIN, neighbors)
    const ids = [...map.values()].map((t) => t.tileId)
    expect(new Set(ids).size).toBe(ids.length)
  })

  it('只用合法的插槽值', () => {
    const map = assignNeighborsToSlots(ORIGIN, neighborsAtBearings([0, 60, 120, 180, 240, 300]))
    for (const slot of map.keys()) expect(MOVEMENT_SLOT_ORDER).toContain(slot)
  })
})

describe('assignNeighborsToSlots — 五角形', () => {
  it.each([0, 15, 30, 45])('相位%i度：5個鄰居指派出5個鍵（空出一個）', (phase) => {
    const neighbors = neighborsAtBearings([0, 72, 144, 216, 288].map((b) => b + phase))
    const map = assignNeighborsToSlots(ORIGIN, neighbors)
    expect(map.size).toBe(5)
    expect(new Set([...map.values()].map((t) => t.tileId)).size).toBe(5)
  })

  it.each([0, 15, 30, 45])('相位%i度：保持循環順序', (phase) => {
    const neighbors = neighborsAtBearings([0, 72, 144, 216, 288].map((b) => b + phase))
    expect(preservesCyclicOrder(assignNeighborsToSlots(ORIGIN, neighbors))).toBe(true)
  })
})

describe('assignNeighborsToSlots — 邊界情況', () => {
  it('沒有鄰居時回傳空Map，不會爆', () => {
    expect(assignNeighborsToSlots(ORIGIN, []).size).toBe(0)
  })

  it('鄰居數量不足時也不會把同一格指派給多個鍵', () => {
    const map = assignNeighborsToSlots(ORIGIN, neighborsAtBearings([90, 270]))
    const ids = [...map.values()].map((t) => t.tileId)
    expect(new Set(ids).size).toBe(ids.length)
    expect(map.size).toBeLessThanOrEqual(2)
  })
})

/**
 * 轉向（面向）的指派測試。
 *
 * 面向的重點不是「數字有沒有變」，而是**畫面上看到的方向跟按鍵是不是還對得起來**：
 * 鏡頭會轉成「面向朝畫面上方」，所以指派必須用「地理方位角 − 面向」來比對。
 * 只要少減那個面向、或正負號弄反，症狀就是轉向之後「按右卻往左走」——
 * 這個專案已經因為方向的正負號踩過好幾次坑，所以這裡直接把方向釘死。
 */
const ALL_FACINGS = [0, 60, 120, 180, 240, 300]

/** 某個插槽對應的那一格，實際的地理方位角。 */
function geoBearingOf(map: Map<MovementSlot, TileDto>, slot: MovementSlot): number {
  const t = map.get(slot)!
  return bearingDegrees(ORIGIN.latitude, ORIGIN.longitude, t.latitude, t.longitude)
}

describe('assignNeighborsToSlots — 轉向（面向）', () => {
  const HEX_BEARINGS = [0, 60, 120, 180, 240, 300]

  it('面向0度＝沒有轉向：結果跟不傳 facingDeg 完全相同（不能有退化）', () => {
    const neighbors = neighborsAtBearings([15, 75, 135, 195, 255, 315])
    const withoutArg = assignNeighborsToSlots(ORIGIN, neighbors)
    const withZero = assignNeighborsToSlots(ORIGIN, neighbors, 0)

    expect([...withZero.keys()].sort()).toEqual([...withoutArg.keys()].sort())
    for (const [slot, tile] of withoutArg) expect(withZero.get(slot)!.tileId).toBe(tile.tileId)
  })

  it.each(ALL_FACINGS)('面向%i度：六個插槽仍然全部指派得到', (facing) => {
    const map = assignNeighborsToSlots(ORIGIN, neighborsAtBearings(HEX_BEARINGS), facing)
    expect(map.size).toBe(6)
  })

  it.each(ALL_FACINGS)('面向%i度：最大誤差跟沒轉向時一樣好（不超過45度）', (facing) => {
    const map = assignNeighborsToSlots(ORIGIN, neighborsAtBearings(HEX_BEARINGS), facing)
    expect(worstError(map, facing)).toBeLessThanOrEqual(45.5)
  })

  it.each(ALL_FACINGS)(
    '面向%i度：「右」那一格的地理方位角＝面向+90度（轉向會把整組方向跟著轉，方向不會轉錯邊）',
    (facing) => {
      const map = assignNeighborsToSlots(ORIGIN, neighborsAtBearings(HEX_BEARINGS), facing)
      expect(circularDiff(geoBearingOf(map, 'right'), (facing + 90) % 360)).toBeLessThanOrEqual(45.5)
    },
  )

  it('右轉60度：每個插槽對應的地理方位角剛好整體加60度（不是減60、也不是不動）', () => {
    const neighbors = neighborsAtBearings(HEX_BEARINGS)
    const facingNorth = assignNeighborsToSlots(ORIGIN, neighbors, 0)
    const facingNorthEast = assignNeighborsToSlots(ORIGIN, neighbors, 60)

    for (const slot of MOVEMENT_SLOT_ORDER) {
      const before = geoBearingOf(facingNorth, slot)
      const after = geoBearingOf(facingNorthEast, slot)
      expect(circularDiff(after, (before + 60) % 360)).toBeLessThan(1)
    }
  })

  it('左轉60度：整體減60度', () => {
    const neighbors = neighborsAtBearings(HEX_BEARINGS)
    const facingNorth = assignNeighborsToSlots(ORIGIN, neighbors, 0)
    const facingNorthWest = assignNeighborsToSlots(ORIGIN, neighbors, 300)

    for (const slot of MOVEMENT_SLOT_ORDER) {
      const before = geoBearingOf(facingNorth, slot)
      const after = geoBearingOf(facingNorthWest, slot)
      expect(circularDiff(after, (before + 300) % 360)).toBeLessThan(1)
    }
  })

  it('轉一圈六次回到原點：面向360度＝面向0度', () => {
    const neighbors = neighborsAtBearings(HEX_BEARINGS)
    const start = assignNeighborsToSlots(ORIGIN, neighbors, 0)
    const fullTurn = assignNeighborsToSlots(ORIGIN, neighbors, 360)
    for (const [slot, tile] of start) expect(fullTurn.get(slot)!.tileId).toBe(tile.tileId)
  })

  it.each(ALL_FACINGS)('面向%i度：五角格（5個鄰居）也能正常指派出5個插槽', (facing) => {
    const map = assignNeighborsToSlots(ORIGIN, neighborsAtBearings([0, 72, 144, 216, 288]), facing)
    expect(map.size).toBe(5)
    expect(new Set([...map.values()].map((t) => t.tileId)).size).toBe(5)
  })
})
