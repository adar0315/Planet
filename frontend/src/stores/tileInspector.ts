import { defineStore } from 'pinia'
import { shallowRef } from 'vue'
import type { SubTileDto, TileDto } from '@/types/tile'

/** 走動視角裡左鍵點到的方塊（地塊或近看的小格），資訊面板顯示它（2026-11-17）。 */
export const useTileInspectorStore = defineStore('tileInspector', () => {
  /** 點到的方塊；沒點到或關掉面板是 null。 */
  const selected = shallowRef<TileDto | SubTileDto | null>(null)
  /** 點到的是近看的小格（不是地塊）。 */
  const isSubTile = shallowRef(false)

  function select(tile: TileDto | SubTileDto, subTile: boolean) {
    isSubTile.value = subTile
    selected.value = tile
  }

  function clear() {
    selected.value = null
  }

  return { selected, isSubTile, select, clear }
})
