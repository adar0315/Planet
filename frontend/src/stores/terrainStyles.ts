import { defineStore } from 'pinia'
import { ref } from 'vue'
import type { TerrainStyleDto } from '@/types/terrainStyle'
import type { TerrainType } from '@/types/tile'
import type { DayPeriod } from '@/map/solarTime'
import { fetchTerrainStyles } from '@/network/apiClient'
import { UNKNOWN_TERRAIN_COLOR } from '@/map/terrainColors'

/** 地形樣式（中英文名稱＋依時段變化的顏色）是全域共用的靜態參考資料，整個App只在啟動時讀一次。 */
export const useTerrainStyleStore = defineStore('terrainStyles', () => {
  const styles = ref<Map<TerrainType, TerrainStyleDto>>(new Map())
  const isLoaded = ref(false)
  /** 載入失敗的原因；非 null 時代表畫面上的地形顏色/名稱都不是真的，UI 應該明確提示。 */
  const loadError = ref<string | null>(null)

  // 「地形×時段 → 0xrrggbb」的預解析查表。地形樣式載入後就不會再變，
  // 但 getColorHex 是每格每次重新上色都會呼叫（一次數千次），沒必要每次都重跑
  // replace('#','') + parseInt，載入完成時一次算好18×4=72個值即可。
  let colorTable: Map<string, number> | null = null

  function buildColorTable() {
    const table = new Map<string, number>()
    for (const [terrain, style] of styles.value) {
      for (const period of ['dawn', 'day', 'dusk', 'night'] as const) {
        table.set(`${terrain}:${period}`, parseInt(colorForPeriod(style, period).replace('#', ''), 16))
      }
    }
    colorTable = table
  }

  // 同時有多個元件呼叫 load() 時共用同一個請求。只靠 isLoaded 擋不住併發：
  // 第一個請求還在路上時 isLoaded 仍是 false，後續呼叫會各自再發一次
  // （實測 PlanetScene 與 CoordinateHud 就讓 /api/terrain-styles 被抓了兩次）。
  let inFlight: Promise<void> | null = null

  function load(): Promise<void> {
    if (isLoaded.value) return Promise.resolve()
    inFlight ??= loadOnce().finally(() => {
      inFlight = null
    })
    return inFlight
  }

  async function loadOnce() {
    try {
      const list = await fetchTerrainStyles()
      styles.value = new Map(list.map((s) => [s.terrain, s]))
      buildColorTable()
      loadError.value = null
      isLoaded.value = true
    } catch (err) {
      // 不要靜默吞掉：拿不到樣式時整個畫面的地形顏色與名稱都是假的，
      // 而以前是安靜地退回一份硬編碼調色盤，看起來「正常」但其實已經跟資料庫脫節。
      // 現在記下原因讓 UI 明確提示，遊戲本身仍可繼續玩（地形會是中性色）。
      loadError.value = err instanceof Error ? err.message : String(err)
    }
  }

  /**
   * 指定地形在指定時段的顏色（0xrrggbb）。
   * 樣式還沒載入或載入失敗時一律回傳中性的「未知地形」色——刻意不在前端保留第二份調色盤，
   * 否則兩份資料會慢慢漂移而且平常看不出來（見 terrainColors.ts 的說明）。
   */
  function getColorHex(terrain: TerrainType, period: DayPeriod): number {
    return colorTable?.get(`${terrain}:${period}`) ?? UNKNOWN_TERRAIN_COLOR
  }

  /** 「英文 中文」的顯示名稱，查不到資料時退回原始的terrain列舉值字串。 */
  function getDisplayName(terrain: TerrainType): string {
    const style = styles.value.get(terrain)
    return style ? `${style.nameEn} ${style.nameZh}` : terrain
  }

  return { styles, isLoaded, loadError, load, getColorHex, getDisplayName }
})

function colorForPeriod(style: TerrainStyleDto, period: DayPeriod): string {
  switch (period) {
    case 'dawn':
      return style.colorDawn
    case 'day':
      return style.colorDay
    case 'dusk':
      return style.colorDusk
    case 'night':
      return style.colorNight
  }
}
