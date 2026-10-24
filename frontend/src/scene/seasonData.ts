import { fetchCellSeasons } from '@/network/apiClient'
import type { SeasonsLookup } from '@/map/seasons'
import type { CellSeasonsDto, TileDto } from '@/types/tile'

/** 一次最多要幾個粗格（後端的上限）。 */
const MAX_CELLS_PER_REQUEST = 5_000

/**
 * 模擬粗格的四季資料快取（水文地貌規劃 H8）。地塊與小格只帶粗格編號，12 個月的資料一個粗格只抓一次——
 * 一個粗格底下約 89 個地塊，塞進每個地塊會是兩千萬列的重複資料。
 */
export interface SeasonData {
  readonly seasonsOf: SeasonsLookup
  /**
   * 確保這些地塊（或小格）用到的粗格都抓過了。有抓到新資料回傳 true（呼叫端要重新上色）。
   * 失敗時回傳 false、不丟例外：沒有四季資料只是少了季節變化，地形照常畫。
   */
  ensure(tiles: readonly TileDto[]): Promise<boolean>
}

export function createSeasonData(): SeasonData {
  const cache = new Map<number, CellSeasonsDto>()
  const requested = new Set<number>()

  async function ensure(tiles: readonly TileDto[]): Promise<boolean> {
    const missing: number[] = []
    for (const tile of tiles) {
      for (const id of [tile.sourceCellId, tile.riverCellId]) {
        if (id === undefined || requested.has(id)) continue
        requested.add(id)
        missing.push(id)
      }
    }
    if (missing.length === 0) return false

    let loaded = false
    for (let i = 0; i < missing.length; i += MAX_CELLS_PER_REQUEST) {
      const chunk = missing.slice(i, i + MAX_CELLS_PER_REQUEST)
      try {
        for (const seasons of await fetchCellSeasons(chunk)) {
          cache.set(seasons.cellId, seasons)
          loaded = true
        }
      } catch {
        // 抓不到就讓下次再試
        for (const id of chunk) requested.delete(id)
      }
    }
    return loaded
  }

  return {
    seasonsOf: (cellId) => cache.get(cellId),
    ensure,
  }
}
