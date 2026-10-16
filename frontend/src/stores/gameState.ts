import { defineStore } from 'pinia'
import { ref, shallowRef, watch } from 'vue'
import type { TileDto } from '@/types/tile'

export type GameMode = 'observe' | 'stepped' | 'realtime'

/**
 * 三種視角，各自是**完全獨立**的 Three.js 場景與 render loop（用 v-if 互相切換）：
 *
 * - `walk`      一般走動視角：精細六角格 mesh，只載入玩家附近範圍
 * - `overview`  全星球總覽：低解析度貼圖球體，一次看到整顆星球
 * - `evolution` 地質演化：後端模擬的當下狀態，可以播放看星球長出大陸
 *
 * 前兩者看的是資料庫裡那顆**既有的**星球，`evolution` 看的是**記憶體裡正在跑的模擬**，
 * 兩邊目前還沒有接起來（把模擬結果寫回2,000萬格是規劃中的階段8）。
 */
export type ViewMode = 'walk' | 'overview' | 'evolution'

/** 視角在 UI 上顯示的名稱。 */
export const VIEW_MODE_LABELS: Record<ViewMode, string> = {
  walk: '走動視角',
  overview: '星球總覽',
  evolution: '地質演化',
}

const GAME_START_UTC = new Date(Date.UTC(2050, 0, 1, 8, 0, 0))

/** 八方位的中文名稱，給 HUD 顯示用。索引＝round(角度/45) mod 8。 */
const COMPASS_POINT_LABELS = ['北', '東北', '東', '東南', '南', '西南', '西', '西北'] as const

/** 方位角（度）→ 最接近的八方位名稱。 */
export function compassPointLabel(deg: number): string {
  const index = ((Math.round(deg / 45) % 8) + 8) % 8
  return COMPASS_POINT_LABELS[index]!
}

/**
 * 收斂到 [0, 360)（負數、超過360、壞掉的存檔值都會被拉回來）。
 *
 * 2026-10-16 之前面向只有 0/60/…/300 六個值（六角格跳格），現在是連續角度。
 * 舊存檔裡的六個值本來就落在這個範圍內，所以存檔格式不必改版。
 */
function normalizeFacing(deg: number): number {
  if (!Number.isFinite(deg)) return 0
  return ((deg % 360) + 360) % 360
}

// 單人原型沒有帳號系統，玩家狀態（位置/模式/時間）直接存瀏覽器localStorage——
// 重新整理頁面能回到上次的位置繼續玩，而不是每次都從(0,0)、起始時間重來。
// v2：地形系統改版後TileDto欄位變了（elevation/seaLevel從double改成elevationLevel/heightCategory/seaLevel
// 整數），版本號要跟著換，不然舊瀏覽器快取的舊格式currentTile會讓elevationLevel變成undefined，
// 渲染時算出NaN座標。
const STORAGE_KEY = 'planet.playerState.v2'

interface PersistedState {
  currentTile: TileDto | null
  mode: GameMode
  gameTimeIso: string
  /** 後來才加的欄位，舊存檔沒有——讀不到就當成面向正北，不需要換 STORAGE_KEY 版本。 */
  facingDeg?: number
}

function loadPersisted(): PersistedState | null {
  try {
    const raw = localStorage.getItem(STORAGE_KEY)
    if (!raw) return null
    return JSON.parse(raw) as PersistedState
  } catch {
    return null // localStorage不可用（無痕模式等）或資料壞掉，就當作沒有存檔
  }
}

export const useGameStateStore = defineStore('gameState', () => {
  const persisted = loadPersisted()

  /**
   * 玩家目前所在的Tile；如果有存檔，啟動時就從這裡的經緯度重新載入，而不是固定從(0,0)開始。
   *
   * 用 shallowRef：TileDto 一律「整個換掉」而不是就地改欄位，深層響應式只是白白為每個欄位
   * 建 proxy。**注意：因此不可以寫 `currentTile.value.terrain = x` 這種就地修改，畫面不會更新。**
   */
  const currentTile = shallowRef<TileDto | null>(persisted?.currentTile ?? null)

  /**
   * 目前載入的整批Tile。用 shallowRef：這個陣列有數千筆，而且完全沒有任何template在讀它
   * （只有PlanetScene的Three.js程式碼用），深層響應式等於每次載入都要為數千個物件建 proxy，
   * 純粹是浪費。同樣不可就地修改裡面的元素。
   */
  const visibleTiles = shallowRef<readonly TileDto[]>([])

  /**
   * 三種操作模式：
   * observe  = 時間靜止，可自由移動觀看每個地形（預設）
   * stepped  = 移動模式，每跨過一格（或按「原地等待」）時間+1小時
   * realtime = 每經過3秒，時間自動+1小時，移動不影響時間
   */
  const mode = ref<GameMode>(persisted?.mode ?? 'observe')
  const gameTime = ref(persisted ? new Date(persisted.gameTimeIso) : new Date(GAME_START_UTC))

  /** 目前鏡頭畫面中，「正北」方向相對螢幕正上方要順時針轉幾度（給指北針UI用，PlanetScene每幀更新）。 */
  const compassAngleDeg = ref(0)

  /**
   * 玩家目前的面向（0°=正北，順時針，連續角度）。
   *
   * **這是給人看與存檔用的副本**，移動計算用的是 playerController 裡的切向量——
   * 正北在極點沒有定義，用角度做移動會在極點出錯（見 map/surfaceMotion.ts）。
   * playerController 每幀把切向量換算回來寫進這裡，量化到 0.5° 才寫，避免 HUD 每幀重繪。
   */
  const facingDeg = ref(normalizeFacing(persisted?.facingDeg ?? 0))

  /** 鏡頭視角傾斜角度（度）：0°＝正上方俯視、角度越大越貼近地平線。PlanetScene每幀更新。 */
  const cameraTiltDeg = ref(0)

  /** 鏡頭到玩家所在格的直線距離，換算成星球實際比例的公尺數。PlanetScene每幀更新。 */
  const cameraDistanceMeters = ref(0)

  /**
   * 鏡頭在玩家所在格「地表之上」的高度（公尺）。傾斜時會明顯小於直線距離
   * （正上方俯視時兩者相同，越貼近地平線差越多）。PlanetScene每幀更新。
   */
  const cameraHeightMeters = ref(0)

  /** 目前縮放程度百分比：0＝拉到最近（controls.minDistance）、100＝拉到最遠（controls.maxDistance）。PlanetScene每幀更新。 */
  const cameraZoomPercent = ref(0)

  /** 每次+1就代表使用者要求「復位視角」（畫面上方=正北的俯視角，保留目前縮放）。PlanetScene會watch這個值來觸發。 */
  const resetViewSignal = ref(0)

  /**
   * 每次+1就代表使用者要求「完全回到預設視角」（傾斜角與鏡頭距離都重設）。
   * 跟 resetViewSignal 分開：指北針只轉回正北、保留縮放，這個則連距離一起還原。
   */
  const resetToDefaultViewSignal = ref(0)

  /** 目前的視角。三套視角各自獨立的Three.js場景，不共用render loop，切換時App.vue會整個換掉顯示的元件。不持久化，重新整理一律回到walk。 */
  const viewMode = ref<ViewMode>('walk')

  /** 直接切到指定視角。 */
  function setViewMode(next: ViewMode) {
    viewMode.value = next
  }

  function persist() {
    try {
      const data: PersistedState = {
        currentTile: currentTile.value,
        mode: mode.value,
        gameTimeIso: gameTime.value.toISOString(),
        facingDeg: facingDeg.value,
      }
      localStorage.setItem(STORAGE_KEY, JSON.stringify(data))
    } catch {
      // 存不進去（容量滿了、無痕模式關閉等）就放棄持久化，不影響遊戲本身能繼續玩
    }
  }

  // 去抖動：連續轉向時 facingDeg 每幀都在變，連續移動時 currentTile 每幾秒就換一次。
  // 原本是任何變動立刻寫 localStorage——跳格時代一秒頂多十幾次，連續轉向會變成一秒六十次。
  // 寫入本身是同步的字串序列化，在主執行緒上每幀做一次會直接吃掉畫面的時間預算。
  let persistTimer: ReturnType<typeof setTimeout> | null = null
  function schedulePersist() {
    if (persistTimer) clearTimeout(persistTimer)
    persistTimer = setTimeout(() => {
      persistTimer = null
      persist()
    }, 500)
  }
  watch([currentTile, mode, gameTime, facingDeg], schedulePersist)

  /** 設定面向（任意角度，會正規化到 [0, 360)）。 */
  function setFacing(deg: number) {
    facingDeg.value = normalizeFacing(deg)
  }

  function advanceTime(hours: number) {
    gameTime.value = new Date(gameTime.value.getTime() + hours * 60 * 60 * 1000)
  }

  function setMode(next: GameMode) {
    mode.value = next
  }

  function requestResetView() {
    resetViewSignal.value++
  }

  /** 完全回到預設視角（傾斜角＋距離都重設）。資訊欄的「Home」按鈕與 Home 鍵都走這裡。 */
  function requestResetToDefaultView() {
    resetToDefaultViewSignal.value++
  }

  return {
    currentTile,
    visibleTiles,
    mode,
    gameTime,
    compassAngleDeg,
    facingDeg,
    cameraTiltDeg,
    cameraDistanceMeters,
    cameraHeightMeters,
    cameraZoomPercent,
    resetViewSignal,
    resetToDefaultViewSignal,
    viewMode,
    setViewMode,
    setFacing,
    advanceTime,
    setMode,
    requestResetView,
    requestResetToDefaultView,
  }
})
