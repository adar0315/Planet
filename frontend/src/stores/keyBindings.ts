import { defineStore } from 'pinia'
import { ref } from 'vue'

/**
 * 可自訂按鍵的動作。
 *
 * 2026-10-16 從「六個方向插槽跳格」改成連續移動：前進／後退／左右橫移是**按住**生效的，
 * 轉向也是按住以固定角速度轉（見 scene/playerController.ts）。
 * 名稱一律是**相對面向**的動作，不是字母、也不是地理方位（CLAUDE.md「移動方向是相對面向的」）。
 *
 * 跑步用 Shift，不在這張表裡：這裡只接受單一字元的按鍵（見 isBindableKey），
 * 而 Shift 是修飾鍵。按住 Shift 時字母鍵的 event.key 會變成大寫——actionFor 本來就不分大小寫，
 * 所以「Shift＋W」自然就是「跑步＋前進」。
 */
export type BindableAction =
  | 'moveForward'
  | 'moveBack'
  | 'strafeLeft'
  | 'strafeRight'
  | 'turnLeft'
  | 'turnRight'
  | 'wait'
  | 'toggleFlight'
  | 'ascend'
  | 'descend'
  | 'flightTier1'
  | 'flightTier2'
  | 'flightTier3'

/** 面板上的顯示順序與名稱。 */
export const ACTION_LABELS: { action: BindableAction; label: string }[] = [
  { action: 'moveForward', label: '前進' },
  { action: 'moveBack', label: '後退' },
  { action: 'strafeLeft', label: '向左橫移' },
  { action: 'strafeRight', label: '向右橫移' },
  { action: 'turnLeft', label: '左轉' },
  { action: 'turnRight', label: '右轉' },
  { action: 'wait', label: '原地等待' },
  { action: 'toggleFlight', label: '起飛／降落' },
  { action: 'ascend', label: '上升' },
  { action: 'descend', label: '下降' },
  { action: 'flightTier1', label: '飛行：巡航' },
  { action: 'flightTier2', label: '飛行：噴射' },
  { action: 'flightTier3', label: '飛行：高超音速' },
]

/**
 * 預設按鍵：動作遊戲的慣例配置。
 *
 *   Q(左轉) W(前進) E(右轉)
 *   A(左移) S(後退) D(右移)
 *   Z(原地等待)
 *
 * 飛行（2026-10-23，docs/flight-and-god-view-plan.md）：
 *
 *   T(起飛／降落)  R(上升)  F(下降)  1/2/3(速度檔)
 *
 * 升降用 R/F 而不是空白鍵／Ctrl：isBindableKey 只接受單一可見字元，空白鍵與修飾鍵都綁不了。
 *
 * 轉向也可以按住滑鼠右鍵拖曳（見 cameraRig.ts）。
 *
 * 新增動作時舊的自訂配置不用遷移：loadPersisted 從這份預設值出發再套用存檔，
 * 存檔裡沒有的動作自然拿到預設鍵；若玩家早就把某個動作綁到 T/R/F，衝突會依互換規則解掉。
 */
export const DEFAULT_BINDINGS: Readonly<Record<BindableAction, string>> = Object.freeze({
  moveForward: 'w',
  moveBack: 's',
  strafeLeft: 'a',
  strafeRight: 'd',
  turnLeft: 'q',
  turnRight: 'e',
  wait: 'z',
  toggleFlight: 't',
  ascend: 'r',
  descend: 'f',
  flightTier1: '1',
  flightTier2: '2',
  flightTier3: '3',
})

/** 這些動作是「按住期間持續生效」的，需要 keyup 才知道何時停。wait 是按一下就做一次。 */
export const HELD_ACTIONS: ReadonlySet<BindableAction> = new Set<BindableAction>([
  'moveForward',
  'moveBack',
  'strafeLeft',
  'strafeRight',
  'turnLeft',
  'turnRight',
  'ascend',
  'descend',
])

/**
 * v2：動作集整個換掉（六個方向插槽 → 前後左右＋轉向）。
 * v1 的配置大部分已經沒有意義，只把**語意沒變**的帶過來（見 loadPersisted）。
 */
const STORAGE_KEY = 'planet.keyBindings.v2'
const LEGACY_STORAGE_KEY_V1 = 'planet.keyBindings.v1'

/**
 * v1 → v2 動作對照：只列語意沒變的。六個方向插槽沒有對應——跳格的「左上」在連續移動裡不存在。
 *
 * **舊的轉向鍵刻意不帶過來**：v1 的轉向是「按一下轉 60°」，v2 是「按住持續轉」，
 * 而 v1 預設的 `,` `.` 是給前者挑的位置（不佔字母區）；連續轉向要跟 WASD 放在一起才按得順手。
 * 帶過來的話，沒改過設定的人會拿到 `,` `.` 而不是新的 Q/E。
 */
const V1_TO_V2: Readonly<Record<string, BindableAction>> = {
  stay: 'wait',
}

/**
 * 只接受單一字元的按鍵（字母、數字、標點）。
 *
 * 這是刻意的限制：`event.key` 對修飾鍵/功能鍵會回傳 'Shift'、'F5'、'Control' 這種多字元名稱，
 * 讓玩家綁到那些鍵只會製造「按了沒反應」或跟瀏覽器打架的問題。Home 鍵（復位視角）也刻意
 * 不開放重綁，它跟資訊欄的 Home 按鈕是同一條路徑。
 */
export function isBindableKey(key: string): boolean {
  return [...key].length === 1 && key.trim().length === 1
}

/** 顯示用：字母轉大寫（`,` `.` 這類標點維持原樣）。 */
export function displayKey(key: string): string {
  return key.toUpperCase()
}

function readJson(key: string): Record<string, unknown> | null {
  try {
    const raw = localStorage.getItem(key)
    if (!raw) return null
    const parsed = JSON.parse(raw) as unknown
    return parsed && typeof parsed === 'object' ? (parsed as Record<string, unknown>) : null
  } catch {
    return null // localStorage 不可用（無痕模式等）或資料壞掉
  }
}

/**
 * 從預設值出發，依序套用 overrides；遇到衝突一律互換，維持「每個動作都有鍵、每個鍵只對一個動作」。
 * 給讀存檔與 v1 遷移共用——兩者都可能帶進跟預設值撞鍵的設定。
 */
function applyOverrides(
  overrides: Iterable<[BindableAction, string]>,
): Record<BindableAction, string> {
  const result: Record<BindableAction, string> = { ...DEFAULT_BINDINGS }
  for (const [action, key] of overrides) {
    if (!isBindableKey(key)) continue
    const normalized = key.toLowerCase()
    const conflicting = (Object.keys(result) as BindableAction[]).find(
      (other) => other !== action && result[other] === normalized,
    )
    if (conflicting) result[conflicting] = result[action]
    result[action] = normalized
  }
  return result
}

function loadPersisted(): Record<BindableAction, string> {
  const current = readJson(STORAGE_KEY)
  if (current) {
    // 逐欄位驗證而不是整包信任：localStorage 是使用者可以直接編輯的，
    // 壞掉的值（多字元、非字串）混進來會變成「某個動作永遠按不到」這種難查的狀況。
    const overrides: [BindableAction, string][] = []
    for (const action of Object.keys(DEFAULT_BINDINGS) as BindableAction[]) {
      const value = current[action]
      if (typeof value === 'string') overrides.push([action, value])
    }
    return applyOverrides(overrides)
  }

  const legacy = readJson(LEGACY_STORAGE_KEY_V1)
  if (legacy) {
    const overrides: [BindableAction, string][] = []
    for (const [oldAction, newAction] of Object.entries(V1_TO_V2)) {
      const value = legacy[oldAction]
      if (typeof value === 'string') overrides.push([newAction, value])
    }
    return applyOverrides(overrides)
  }

  return { ...DEFAULT_BINDINGS }
}

/**
 * 玩家自訂的按鍵配置。存在 localStorage，重新整理後保留。
 *
 * 綁定衝突一律用**互換**處理而不是「把舊的清掉」：清掉會留下一個沒有按鍵的動作，
 * 玩家得自己發現並補綁；互換則永遠維持「每個動作都有鍵、每個鍵只對一個動作」。
 */
export const useKeyBindingsStore = defineStore('keyBindings', () => {
  const bindings = ref<Record<BindableAction, string>>(loadPersisted())

  /**
   * 正在等待玩家按下要綁定的鍵。設定面板開著等待輸入時，PlanetScene 的全域移動監聽必須整個停手，
   * 否則玩家為了改綁而按的每一個鍵都會同時把角色走掉（CLAUDE.md 記載過的坑）。
   *
   * 面板本身已經用 capture 階段的 stopPropagation 擋住事件，這個旗標是**第二道、而且與事件
   * 傳遞順序無關**的保險：capture 能擋住是因為 window 的 capture 監聽比 bubble 監聽早跑，
   * 但那是隱含的順序依賴——一旦有人把某個監聽改成直接掛在 window 上派送，順序就變成註冊順序，
   * 保護會安靜地失效（實測確認過這個失效情境）。旗標則不管誰先誰後都成立。
   */
  const isCapturing = ref(false)

  function persist() {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(bindings.value))
    } catch {
      // 存不進去就只影響「下次開啟要重設」，不影響這一輪能不能玩
    }
  }

  function actionFor(key: string): BindableAction | undefined {
    const normalized = key.toLowerCase()
    return (Object.keys(bindings.value) as BindableAction[]).find(
      (action) => bindings.value[action] === normalized,
    )
  }

  function keyFor(action: BindableAction): string {
    return bindings.value[action]
  }

  /**
   * 把 action 綁到 key。若這個 key 已經被別的動作佔用，兩個動作的按鍵互換。
   * @returns 被互換掉的動作（沒有衝突時是 undefined），給UI提示用。
   */
  function setBinding(action: BindableAction, key: string): BindableAction | undefined {
    if (!isBindableKey(key)) return undefined
    const normalized = key.toLowerCase()
    if (bindings.value[action] === normalized) return undefined

    const conflicting = actionFor(normalized)
    const previous = bindings.value[action]
    const next = { ...bindings.value, [action]: normalized }
    if (conflicting) next[conflicting] = previous
    bindings.value = next
    persist()
    return conflicting
  }

  function resetToDefaults() {
    bindings.value = { ...DEFAULT_BINDINGS }
    persist()
  }

  return { bindings, isCapturing, actionFor, keyFor, setBinding, resetToDefaults }
})
