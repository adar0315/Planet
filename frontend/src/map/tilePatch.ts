import type { TileDto } from '@/types/tile'

/**
 * 一批已載入的Tile（一個「patch」）的純資料索引：查表、鄰接圖、離邊緣多遠。
 * 刻意不依賴 THREE 或 Vue，方便單獨測試與推理——渲染的部分在 tilePatchMesh.ts。
 *
 * 存在的理由：讓「按鍵當下就能決定走到哪一格」不需要任何網路往返。
 * 鄰接關係是後端在 nearby 回應裡一起送來的（見 TileDto.neighborIds），
 * 前端只是查表，不重新推導座標——跨面/五角形的座標數學一律留在後端。
 */
/**
 * 「離邊緣無限遠」的哨兵值（Int32 上限）。不能用 Number.MAX_SAFE_INTEGER——
 * edgeDepth 是 Int32Array，超出範圍會被截斷成 -1。
 */
export const UNREACHABLE_DEPTH = 0x7fffffff

export interface TilePatch {
  readonly tiles: readonly TileDto[]
  /** tileId → 在 tiles 中的索引。 */
  readonly indexByTileId: ReadonlyMap<number, number>
  /**
   * 每格「距離patch邊緣還有幾步」（多源BFS）。
   * 0 = 這格本身就有鄰居在patch外（最外圈）；數字越大代表越安全。
   * 查不到的格子視為 0。
   */
  readonly edgeDepth: Int32Array
}

/** 用 nearby 回傳的一批Tile建立索引。 */
export function createTilePatch(tiles: readonly TileDto[]): TilePatch {
  const indexByTileId = new Map<number, number>()
  for (let i = 0; i < tiles.length; i++) {
    indexByTileId.set(tiles[i]!.tileId, i)
  }
  return { tiles, indexByTileId, edgeDepth: computeEdgeDepth(tiles, indexByTileId) }
}

/**
 * 多源BFS：從「有鄰居在patch外」的格子往內擴散，算出每格離邊緣幾步。
 *
 * 用拓樸距離而不是幾何距離，是因為patch的形狀並不可靠——高緯度會被經度換算拉成橢圓，
 * 靠近換日線時甚至可能缺一邊。用「還剩幾步會走出已載入範圍」判斷才是精確的。
 */
function computeEdgeDepth(tiles: readonly TileDto[], indexByTileId: ReadonlyMap<number, number>): Int32Array {
  const depth = new Int32Array(tiles.length).fill(-1)
  const queue: number[] = []

  for (let i = 0; i < tiles.length; i++) {
    const neighborIds = tiles[i]!.neighborIds
    // 沒有鄰居資訊（舊版後端或單一Tile查詢）時，保守地當成邊緣，寧可多載入也不要走出範圍。
    const isEdge =
      !neighborIds || neighborIds.some((id) => id === null || !indexByTileId.has(id))
    if (isEdge) {
      depth[i] = 0
      queue.push(i)
    }
  }

  for (let head = 0; head < queue.length; head++) {
    const index = queue[head]!
    const neighborIds = tiles[index]!.neighborIds
    if (!neighborIds) continue

    for (const id of neighborIds) {
      if (id === null) continue
      const next = indexByTileId.get(id)
      if (next === undefined || depth[next] !== -1) continue
      depth[next] = depth[index]! + 1
      queue.push(next)
    }
  }

  // 完全沒有邊緣（例如整顆星球都載進來了）時BFS走不到，維持初始的-1，這裡改成「無限深」。
  // 注意必須用 Int32 放得下的值：depth 是 Int32Array，塞 Number.MAX_SAFE_INTEGER 會被截斷成 -1，
  // 結果反而變成「深度小於門檻」而每走一步都觸發補載（這個坑已由 tilePatch.spec.ts 鎖住）。
  for (let i = 0; i < depth.length; i++) {
    if (depth[i] === -1) depth[i] = UNREACHABLE_DEPTH
  }
  return depth
}

/** 取得某格的鄰居Tile（略過不在patch內的）。查不到這格時回傳空陣列。 */
export function neighborsOf(patch: TilePatch, tileId: number): TileDto[] {
  const index = patch.indexByTileId.get(tileId)
  if (index === undefined) return []

  const neighborIds = patch.tiles[index]!.neighborIds
  if (!neighborIds) return []

  const result: TileDto[] = []
  for (const id of neighborIds) {
    if (id === null) continue
    const neighborIndex = patch.indexByTileId.get(id)
    if (neighborIndex !== undefined) result.push(patch.tiles[neighborIndex]!)
  }
  return result
}

/** 這格的鄰居是否在patch裡全部都找得到（不完整就不該拿來建方向鍵對應）。 */
export function hasCompleteNeighbors(patch: TilePatch, tileId: number): boolean {
  const index = patch.indexByTileId.get(tileId)
  if (index === undefined) return false

  const neighborIds = patch.tiles[index]!.neighborIds
  if (!neighborIds || neighborIds.length === 0) return false

  return neighborIds.every((id) => id !== null && patch.indexByTileId.has(id))
}

/** 這格離patch邊緣還有幾步；查不到時回傳0（當作已經在邊緣）。 */
export function edgeDepthOf(patch: TilePatch, tileId: number): number {
  const index = patch.indexByTileId.get(tileId)
  return index === undefined ? 0 : patch.edgeDepth[index]!
}

/**
 * 在patch裡找離指定經緯度最近的Tile（大圓距離）。
 *
 * 不能直接拿 nearby 回傳的第一筆當「最近」——後端是用原始度數的
 * (Δlat)²+(Δlon)² 排序，離赤道越遠、經度一度的實際距離越短，那個排序就越不是真的距離。
 */
export function findNearestTile(
  tiles: readonly TileDto[],
  latitude: number,
  longitude: number,
): TileDto | null {
  const toRad = (deg: number) => (deg * Math.PI) / 180
  const lat1 = toRad(latitude)
  const cosLat1 = Math.cos(lat1)
  const sinLat1 = Math.sin(lat1)

  let best: TileDto | null = null
  let bestCos = -Infinity // 夾角越小 cos 越大

  for (const tile of tiles) {
    const lat2 = toRad(tile.latitude)
    const deltaLon = toRad(tile.longitude - longitude)
    // 兩點單位向量的內積 = cos(夾角)，夾角越小距離越近
    const cosAngle = sinLat1 * Math.sin(lat2) + cosLat1 * Math.cos(lat2) * Math.cos(deltaLon)
    if (cosAngle > bestCos) {
      bestCos = cosAngle
      best = tile
    }
  }
  return best
}
