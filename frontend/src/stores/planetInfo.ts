import { defineStore } from 'pinia'
import { ref, shallowRef } from 'vue'
import { fetchPlanetInfo, fetchWaterMask, type PlanetInfoDto } from '@/network/apiClient'

/**
 * 星球的基本尺寸（半徑、Tile邊長、總覽貼圖規格）。
 *
 * 這些原本是前端寫死的常數（`PLANET_RADIUS_METERS = 1_851_000` 之類），但它們在後端是由
 * 細分頻率**推導**出來的——寫死等於同一份事實存在兩個地方，改了其中一邊另一邊不會知道，
 * 而且症狀是「格子大小安靜地錯掉」，不會有任何錯誤訊息。現在一律向後端要。
 *
 * 由 PlanetLoadingGate 在畫面顯示之前載入，所以場景元件掛載時可以直接同步讀取。
 */
export const usePlanetInfoStore = defineStore('planetInfo', () => {
  const info = shallowRef<PlanetInfoDto | null>(null)
  /** 全星球陸海遮罩（每像素1位元組），供位置指示器畫大陸輪廓。拿不到時為 null，指示器會退成純色球面。 */
  const waterMask = shallowRef<Uint8Array | null>(null)
  const isLoaded = ref(false)

  // 與 terrainStyles 相同：只靠 isLoaded 擋不住併發呼叫，要共用同一個請求。
  let inFlight: Promise<void> | null = null

  function load(): Promise<void> {
    if (isLoaded.value) return Promise.resolve()
    inFlight ??= loadOnce().finally(() => {
      inFlight = null
    })
    return inFlight
  }

  async function loadOnce() {
    info.value = await fetchPlanetInfo()
    // 遮罩只是指示器的裝飾，拿不到不該擋住整個遊戲——失敗就讓指示器退成純色球面。
    // 星球尺寸（上面那個）則相反，缺了會讓地形用錯比例畫，所以不在這裡吞掉例外。
    try {
      waterMask.value = await fetchWaterMask()
    } catch {
      waterMask.value = null
    }
    isLoaded.value = true
  }

  /**
   * 已載入的星球資訊。呼叫前必須確定已經 load 過（PlanetLoadingGate 會保證這件事），
   * 否則直接丟例外——與其讓場景用一組猜測的尺寸把地形畫錯，不如當場失敗。
   */
  function require(): PlanetInfoDto {
    if (!info.value) {
      throw new Error('星球資訊尚未載入：場景元件必須在 PlanetLoadingGate 內才能掛載')
    }
    return info.value
  }

  return { info, waterMask, isLoaded, load, require }
})
