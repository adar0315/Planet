import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { MOVEMENT_SLOT_ORDER, type MovementSlot } from '@/map/bearing'

/**
 * 可自訂按鍵的動作。六個移動插槽用 `move` 前綴，跟 bearing.ts 的插槽名一一對應
 * （`moveUpLeft` ↔ `upLeft`），這樣新增/改名插槽時型別會直接抓到不一致。
 */
export type BindableAction = `move${Capitalize<MovementSlot>}` | 'stay' | 'turnLeft' | 'turnRight'

/** 由插槽名推出對應的動作名，避免兩邊各寫一份字串對照表而漂移。 */
export function actionForSlot(slot: MovementSlot): BindableAction {
  return `move${slot.charAt(0).toUpperCase()}${slot.slice(1)}` as BindableAction
}

/** actionForSlot 的反向轉換。型別上只接受六個移動動作，不會誤傳 stay/turnLeft 進來。 */
export function slotForAction(action: `move${Capitalize<MovementSlot>}`): MovementSlot {
  const rest = action.slice('move'.length)
  return `${rest.charAt(0).toLowerCase()}${rest.slice(1)}` as MovementSlot
}

/** 面板上的顯示順序與名稱。順序刻意照鍵盤排列由上而下，不是照字母。 */
export const ACTION_LABELS: { action: BindableAction; label: string }[] = [
  { action: 'moveUpLeft', label: '移動：左上' },
  { action: 'moveUpRight', label: '移動：右上' },
  { action: 'moveLeft', label: '移動：左' },
  { action: 'moveRight', label: '移動：右' },
  { action: 'moveDownLeft', label: '移動：左下' },
  { action: 'moveDownRight', label: '移動：右下' },
  { action: 'stay', label: '原地不動' },
  { action: 'turnLeft', label: '左轉 60°' },
  { action: 'turnRight', label: '右轉 60°' },
]

/**
 * 預設按鍵。移動的六鍵沿用原本的配置（形狀對應鍵盤上的相對位置）：
 *   W(左上) E(右上)
 * A(左) S(原地) D(右)
 *   Z(左下) X(右下)
 * 轉向用 `,` `.`——它們在鍵盤上剛好是左右相鄰的一對，而且不佔用字母區。
 */
export const DEFAULT_BINDINGS: Readonly<Record<BindableAction, string>> = Object.freeze({
  moveUpLeft: 'w',
  moveUpRight: 'e',
  moveRight: 'd',
  moveDownRight: 'x',
  moveDownLeft: 'z',
  moveLeft: 'a',
  stay: 's',
  turnLeft: ',',
  turnRight: '.',
})

const STORAGE_KEY = 'planet.keyBindings.v1'

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

function loadPersisted(): Partial<Record<BindableAction, string>> {
  try {
    const raw = localStorage.getItem(STORAGE_KEY)
    if (!raw) return {}
    const parsed = JSON.parse(raw) as Record<string, unknown>
    const result: Partial<Record<BindableAction, string>> = {}
    // 逐欄位驗證而不是整包信任：localStorage 是使用者可以直接編輯的，
    // 壞掉的值（多字元、非字串）混進來會變成「某個動作永遠按不到」這種難查的狀況。
    for (const action of Object.keys(DEFAULT_BINDINGS) as BindableAction[]) {
      const value = parsed[action]
      if (typeof value === 'string' && isBindableKey(value)) result[action] = value.toLowerCase()
    }
    return result
  } catch {
    return {} // localStorage不可用（無痕模式等）或資料壞掉，就用預設值
  }
}

/**
 * 玩家自訂的按鍵配置。存在 localStorage，重新整理後保留。
 *
 * 綁定衝突一律用**互換**處理而不是「把舊的清掉」：清掉會留下一個沒有按鍵的動作，
 * 玩家得自己發現並補綁；互換則永遠維持「每個動作都有鍵、每個鍵只對一個動作」。
 */
export const useKeyBindingsStore = defineStore('keyBindings', () => {
  const bindings = ref<Record<BindableAction, string>>({ ...DEFAULT_BINDINGS, ...loadPersisted() })

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

  /** key（小寫）→ 動作 的反查表。每幀不會用到，但 keydown 每次都要查，所以做成 computed 快取。 */
  const actionByKey = computed(() => {
    const map = new Map<string, BindableAction>()
    for (const [action, key] of Object.entries(bindings.value) as [BindableAction, string][]) {
      map.set(key, action)
    }
    return map
  })

  function actionFor(key: string): BindableAction | undefined {
    return actionByKey.value.get(key.toLowerCase())
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

  /** 六個移動插槽對應的按鍵，給資訊欄的九宮格顯示用。 */
  const movementKeys = computed(() => {
    const result = {} as Record<MovementSlot, string>
    for (const slot of MOVEMENT_SLOT_ORDER) result[slot] = bindings.value[actionForSlot(slot)]
    return result
  })

  return { bindings, isCapturing, actionFor, keyFor, setBinding, resetToDefaults, movementKeys }
})
