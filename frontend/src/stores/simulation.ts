import { defineStore } from 'pinia'
import { computed, ref, shallowRef } from 'vue'
import {
  deleteSimulationSave,
  fetchMaterializationStatus,
  fetchSimulationSaves,
  fetchSimulationStatus,
  fetchSimulationTexture,
  loadSimulation,
  resetSimulation,
  saveSimulation,
  startMaterialization,
  stepSimulation,
  type MaterializationStatus,
  type SimulationFieldName,
  type SimulationStatus,
  type SnapshotInfo,
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
  { field: 'RiversAndLakes', label: '河流與湖', hint: '亮青＝湖、藍＝河（越深越大）；集水面積 1 萬平方公里以上才畫' },
  { field: 'IceThickness', label: '冰蓋', hint: '冰鎖住的水會讓海平面下降' },
]

/** 放大作業的進度多久查一次（毫秒）。整個作業約 10 分鐘。 */
const MATERIALIZE_POLL_MS = 3000

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

  /** 忙碌時換了欄位、還沒補抓貼圖（見 setField）。 */
  let fieldRefreshPending = false

  async function refreshTexture() {
    fieldRefreshPending = false
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

  /**
   * 換欄位時剛好有別的請求在跑（例如存檔清單還在讀），`run` 會直接跳過、貼圖就停在舊欄位——
   * 選單顯示新欄位、球上是舊圖。所以記下來，等手上那個請求結束再補抓一次。
   */
  async function setField(next: SimulationFieldName) {
    if (field.value === next) return
    field.value = next
    if (isBusy.value) {
      fieldRefreshPending = true
      return
    }
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
        saves.value = await fetchSimulationSaves()
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

  // -------------------------------------------------------------------------
  // 存檔管理（v2 規劃 P9）：列表、手動存、載入（從檢查點分岔）、刪除
  // -------------------------------------------------------------------------

  const saves = ref<SnapshotInfo[]>([])

  /**
   * 存檔清單是獨立的讀取（不碰模擬引擎），不走 `run`：面板一打開就要抓，那時常常剛好在抓狀態或貼圖，
   * 走 `run` 會被「忙碌中」直接跳過、清單一直是空的。
   */
  async function refreshSaves() {
    try {
      saves.value = await fetchSimulationSaves()
    } catch (err) {
      error.value = err instanceof Error ? err.message : '取得存檔清單失敗'
    }
  }

  /** 把目前的模擬存成指定名稱。從檢查點分岔就是：載入檢查點 → 用新名字存 → 繼續跑。 */
  async function saveAs(name: string) {
    await run(async () => {
      await saveSimulation(name)
      saves.value = await fetchSimulationSaves()
    })
  }

  async function load(name: string) {
    isPlaying.value = false
    await run(async () => {
      status.value = await loadSimulation(name)
      stopNotice.value = null
      await refreshTexture()
    })
  }

  async function remove(name: string) {
    await run(async () => {
      await deleteSimulationSave(name)
      saves.value = await fetchSimulationSaves()
    })
  }

  // -------------------------------------------------------------------------
  // 清碎島並放大成走動地圖（管理端點）。金鑰只放在這個變數裡、不寫進瀏覽器儲存，重新整理就要重打。
  // -------------------------------------------------------------------------

  const materialization = ref<MaterializationStatus | null>(null)
  const materializeError = ref<string | null>(null)
  let materializeKey: string | null = null
  let materializeTimer: ReturnType<typeof setTimeout> | null = null

  async function pollMaterialization() {
    if (materializeKey === null) return
    try {
      materialization.value = await fetchMaterializationStatus(materializeKey)
      materializeError.value = materialization.value.error
      if (materialization.value.isRunning) {
        materializeTimer = setTimeout(pollMaterialization, MATERIALIZE_POLL_MS)
      } else {
        materializeKey = null
      }
    } catch (err) {
      materializeError.value = err instanceof Error ? err.message : '取得放大進度失敗'
      materializeKey = null
    }
  }

  /** 開始放大（呼叫端要先跟使用者確認——會覆蓋目前整顆星球的地塊）。 */
  async function materialize(name: string, adminKey: string) {
    materializeError.value = null
    try {
      await startMaterialization(name, adminKey)
      materializeKey = adminKey
      if (materializeTimer !== null) clearTimeout(materializeTimer)
      await pollMaterialization()
    } catch (err) {
      materializeError.value = err instanceof Error ? err.message : '開始放大失敗'
    }
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
    // 忙碌時換了欄位（見 setField）：補抓一次。播放中不必，下一次推進本來就會用新欄位抓貼圖
    if (fieldRefreshPending && !isPlaying.value) {
      fieldRefreshPending = false
      await run(refreshTexture)
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
    saves,
    refreshSaves,
    saveAs,
    load,
    remove,
    materialization,
    materializeError,
    materialize,
  }
})
