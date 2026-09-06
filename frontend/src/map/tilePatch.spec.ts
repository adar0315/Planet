import { describe, expect, it } from 'vitest'
import {
  createTilePatch,
  edgeDepthOf,
  findNearestTile,
  hasCompleteNeighbors,
  neighborsOf,
  UNREACHABLE_DEPTH,
} from './tilePatch'
import type { TileDto } from '@/types/tile'

/**
 * patch 的鄰接索引與「離邊緣還有幾步」。
 *
 * edgeDepth 是邊緣觸發補載的判斷依據：算錯會導致玩家在補載完成前就走出已載入範圍，
 * 那一刻方向鍵會全部失效。所以這裡用一條可以手算的鏈狀地圖，把每一格的深度值都驗出來。
 */

function tile(id: number, neighborIds: (number | null)[], latitude = 0, longitude = 0): TileDto {
  return {
    tileId: id,
    shape: 'Hexagon',
    faceIndex: 0,
    latitude,
    longitude,
    terrain: 'Grassland',
    heightCategory: 0,
    elevationLevel: 0,
    seaLevel: 0,
    neighborIds,
  }
}

/**
 * 造一條鏈：0 - 1 - 2 - ... - (n-1)。
 * 兩端各有一個 null 鄰居（代表鏈繼續延伸出patch外），所以兩端的 edgeDepth 是0，往中間遞增。
 */
function makeChain(n: number): TileDto[] {
  return Array.from({ length: n }, (_, i) =>
    tile(i, [i === 0 ? null : i - 1, i === n - 1 ? null : i + 1], 0, i * 0.03),
  )
}

describe('createTilePatch / edgeDepthOf', () => {
  it('建立 tileId → 索引 的對照', () => {
    const patch = createTilePatch(makeChain(5))
    expect(patch.indexByTileId.get(0)).toBe(0)
    expect(patch.indexByTileId.get(4)).toBe(4)
    expect(patch.indexByTileId.get(99)).toBeUndefined()
  })

  it('鏈狀地圖：兩端深度0，往中間逐步遞增', () => {
    const patch = createTilePatch(makeChain(7))
    expect([0, 1, 2, 3, 4, 5, 6].map((id) => edgeDepthOf(patch, id))).toEqual([0, 1, 2, 3, 2, 1, 0])
  })

  it('鄰居指向不在patch裡的id，一樣算邊緣', () => {
    const tiles = [
      tile(0, [1, 12345]), // 12345 不在 patch 裡
      tile(1, [0, 2]),
      tile(2, [1, null]),
    ]
    const patch = createTilePatch(tiles)
    expect(edgeDepthOf(patch, 0)).toBe(0)
    expect(edgeDepthOf(patch, 2)).toBe(0)
    expect(edgeDepthOf(patch, 1)).toBe(1)
  })

  it('完全沒有neighborIds（舊後端或單格查詢）時保守地一律當成邊緣', () => {
    const tiles: TileDto[] = [
      { ...tile(0, []), neighborIds: undefined },
      { ...tile(1, []), neighborIds: undefined },
    ]
    const patch = createTilePatch(tiles)
    expect(edgeDepthOf(patch, 0)).toBe(0)
    expect(edgeDepthOf(patch, 1)).toBe(0)
  })

  it('沒有任何邊緣（鄰接封閉）時深度視為無限大，不會誤判成0而狂補載', () => {
    // 三格互相連成環，沒有任何 null 或外部參照
    const tiles = [tile(0, [1, 2]), tile(1, [0, 2]), tile(2, [0, 1])]
    const patch = createTilePatch(tiles)
    // 哨兵值必須是 Int32 放得下的：edgeDepth 是 Int32Array，
    // 用 Number.MAX_SAFE_INTEGER 會被截斷成 -1，反而讓「深度 > 門檻」永遠不成立、每步都補載。
    expect(edgeDepthOf(patch, 0)).toBe(UNREACHABLE_DEPTH)
    expect(edgeDepthOf(patch, 0)).toBeGreaterThan(1000)
  })

  it('查不到的tileId回傳0（當成已在邊緣，寧可多補載）', () => {
    const patch = createTilePatch(makeChain(3))
    expect(edgeDepthOf(patch, 999)).toBe(0)
  })

  it('空patch不會爆', () => {
    const patch = createTilePatch([])
    expect(patch.tiles).toHaveLength(0)
    expect(edgeDepthOf(patch, 1)).toBe(0)
  })
})

describe('neighborsOf', () => {
  it('回傳patch內實際存在的鄰居，略過null與範圍外的id', () => {
    const patch = createTilePatch([tile(0, [1, null, 999]), tile(1, [0, null])])
    expect(neighborsOf(patch, 0).map((t) => t.tileId)).toEqual([1])
  })

  it('查不到的格子回傳空陣列', () => {
    expect(neighborsOf(createTilePatch(makeChain(3)), 999)).toEqual([])
  })
})

describe('hasCompleteNeighbors', () => {
  it('鄰居全部在patch內才算完整', () => {
    const patch = createTilePatch([tile(0, [1, 2]), tile(1, [0, 2]), tile(2, [0, 1])])
    expect(hasCompleteNeighbors(patch, 0)).toBe(true)
  })

  it('有null就不算完整（不能拿殘缺清單去建方向鍵）', () => {
    const patch = createTilePatch([tile(0, [1, null]), tile(1, [0, null])])
    expect(hasCompleteNeighbors(patch, 0)).toBe(false)
  })

  it('鄰居指向patch外也不算完整', () => {
    const patch = createTilePatch([tile(0, [1, 999]), tile(1, [0, null])])
    expect(hasCompleteNeighbors(patch, 0)).toBe(false)
  })

  it('沒有鄰居資訊時不算完整', () => {
    const patch = createTilePatch([{ ...tile(0, []), neighborIds: undefined }])
    expect(hasCompleteNeighbors(patch, 0)).toBe(false)
  })
})

describe('findNearestTile', () => {
  it('用大圓距離找最近的一格', () => {
    const tiles = [
      tile(0, [], 0, 0),
      tile(1, [], 0, 1),
      tile(2, [], 0, 5),
    ]
    expect(findNearestTile(tiles, 0, 0.9)!.tileId).toBe(1)
  })

  it('跨換日線時仍然找得到真正最近的（179.9 與 -179.9 只差0.2度）', () => {
    const tiles = [
      tile(0, [], 0, -179.9), // 距離 179.9 只有 0.2 度
      tile(1, [], 0, 150), // 差 29.9 度
    ]
    expect(findNearestTile(tiles, 0, 179.9)!.tileId).toBe(0)
  })

  it('高緯度時經度差的實際距離會縮短，不能只看度數差', () => {
    // 在緯度80度，經度差5度的實際距離約 5*cos(80) ≈ 0.87 度，比緯度差2度更近
    const tiles = [
      tile(0, [], 80, 5), // 同緯度、經度差5度
      tile(1, [], 78, 0), // 同經度、緯度差2度
    ]
    expect(findNearestTile(tiles, 80, 0)!.tileId).toBe(0)
  })

  it('空清單回傳null', () => {
    expect(findNearestTile([], 0, 0)).toBeNull()
  })
})
