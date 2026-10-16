import { defineStore } from 'pinia'
import { computed, ref, shallowRef } from 'vue'
import {
  fetchSimulationStatus,
  fetchSimulationTexture,
  resetSimulation,
  stepSimulation,
  type SimulationFieldName,
  type SimulationStatus,
} from '@/network/apiClient'

/** 可以顯示的欄位與它們的中文名稱，順序就是下拉選單的順序。 */
export const SIMULATION_FIELDS: { field: SimulationFieldName; label: string; hint: string }[] = [
  { field: 'ElevationWithPlateBoundaries', label: '地形＋板塊邊界', hint: '紅＝聚合、綠＝張裂、黃＝轉形' },
  { field: 'Elevation', label: '地形', hint: '深藍＝深海、灰白＝高地' },
  { field: 'CrustType', label: '地殼類型', hint: '藍＝海洋地殼、褐＝大陸地殼' },
  { field: 'CrustThickness', label: '地殼厚度', hint: '亮＝厚（山根與大陸）' },
  { field: 'CrustAge', label: '地殼年齡', hint: '紅＝新生、藍＝古老' },
  { field: 'PlateId', label: '板塊分布', hint: '每個板塊一個顏色' },
  { field: 'BoundaryType', label: '板塊邊界', hint: '只看邊界，不含地形' },
  { field: 'VolcanicActivity', label: '火山活動', hint: '中洋脊、島弧、地函熱柱' },
  { field: 'Temperature', label: '溫度', hint: '藍＝冷、紅＝熱' },
  { field: 'Precipitation', label: '降水', hint: '看得到赤道多雨帶與副熱帶沙漠' },
  { field: 'Runoff', label: '陸地逕流', hint: '階段4流水侵蝕的輸入' },
  { field: 'CoralReef', label: '珊瑚礁', hint: '亮白＝已經露出水面的珊瑚島' },
  { field: 'WaterDepth', label: '水深', hint: '檢查海陸分布與湖泊' },
  { field: 'SedimentThickness', label: '沉積物', hint: '沉積與有機物堆積' },
]

/** 播放時每次請求推進幾步。後端單次請求上限是2000。 */
const STEPS_PER_TICK = [10, 50, 200] as const

export const useSimulationStore = defineStore('simulation', () => {
  const status = ref<SimulationStatus | null>(null)
  const field = ref<SimulationFieldName>('ElevationWithPlateBoundaries')

  /**
   * 貼圖的原始RGBA位元組。用 shallowRef：這是2MB的 Uint8Array，
   * 深層響應式會為它建 proxy，純粹是浪費（而且只有Three.js在讀，沒有template在讀）。
   */
  const texture = shallowRef<Uint8Array | null>(null)

  /** 每次貼圖換新內容就+1，讓場景知道要重新上傳到GPU（位元組陣列本身是同一個參考時 watch 不會觸發）。 */
  const textureVersion = ref(0)

  const isPlaying = ref(false)

  /**
   * 停止條件成立時的提示。播放會自動停下，這段文字告訴使用者為什麼停了、自動存檔叫什麼。
   * 使用者按播放繼續之後就清掉（條件是邊緣觸發的，繼續跑不會立刻又停）。
   */
  const stopNotice = ref<string | null>(null)
  const speedIndex = ref(0)
  const isBusy = ref(false)
  const error = ref<string | null>(null)

  /** `?? STEPS_PER_TICK[0]` 只是讓型別完整——speedIndex 一律由 cycleSpeed 取模產生，不會越界。 */
  const stepsPerTick = computed<number>(() => STEPS_PER_TICK[speedIndex.value] ?? STEPS_PER_TICK[0])

  /** 顯示用的年份字串（億年／百萬年／年，依量級自動換單位）。 */
  const elapsedLabel = computed(() => {
    const years = status.value?.elapsedYears ?? 0
    if (years >= 1e8) return `${(years / 1e8).toFixed(2)} 億年`
    if (years >= 1e6) return `${(years / 1e6).toFixed(1)} 百萬年`
    return `${Math.round(years).toLocaleString()} 年`
  })

  async function refreshTexture() {
    const buffer = await fetchSimulationTexture(field.value)
    texture.value = new Uint8Array(buffer)
    textureVersion.value++
  }

  /** 第一次進入演化模式時呼叫：拿狀態＋第一張貼圖。 */
  async function initialize() {
    if (status.value) return
    await run(async () => {
      status.value = await fetchSimulationStatus()
      await refreshTexture()
    })
  }

  async function setField(next: SimulationFieldName) {
    if (field.value === next) return
    field.value = next
    await run(refreshTexture)
  }

  /** 推進一次（播放中由 tick 呼叫，也可以手動單步）。 */
  async function advance(steps = stepsPerTick.value) {
    await run(async () => {
      status.value = await stepSimulation(steps)
      if (status.value.stoppedByCondition) {
        isPlaying.value = false
        stopNotice.value =
          `陸地已經集中成兩大塊＋大島，自動暫停` +
          (status.value.autoSaveName ? `，已存檔為「${status.value.autoSaveName}」` : '')
      }
      await refreshTexture()
    })
  }

  async function reset() {
    isPlaying.value = false
    await run(async () => {
      status.value = await resetSimulation()
      await refreshTexture()
    })
  }

  function togglePlay() {
    isPlaying.value = !isPlaying.value
    if (isPlaying.value) stopNotice.value = null
  }

  function cycleSpeed() {
    speedIndex.value = (speedIndex.value + 1) % STEPS_PER_TICK.length
  }

  /**
   * 共用的「執行一次後端操作」包裝。
   *
   * `isBusy` 不只是給UI轉圈用——播放迴圈靠它避免**在上一次請求還沒回來就發下一次**。
   * 後端的模擬是 Singleton 而且整個包在同一把鎖裡，重疊的請求只會排隊、
   * 把畫面更新愈拖愈久，看起來像當掉。
   */
  async function run(action: () => Promise<void>) {
    if (isBusy.value) return
    isBusy.value = true
    try {
      await action()
      error.value = null
    } catch (err) {
      error.value = err instanceof Error ? err.message : '模擬操作失敗'
      isPlaying.value = false // 出錯就停下來，不要一直重試同一個失敗的請求
    } finally {
      isBusy.value = false
    }
  }

  return {
    status,
    field,
    texture,
    textureVersion,
    isPlaying,
    stopNotice,
    isBusy,
    error,
    stepsPerTick,
    speedIndex,
    elapsedLabel,
    initialize,
    setField,
    advance,
    reset,
    togglePlay,
    cycleSpeed,
  }
})
