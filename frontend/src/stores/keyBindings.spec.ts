import { beforeEach, describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import {
  ACTION_LABELS,
  DEFAULT_BINDINGS,
  actionForSlot,
  isBindableKey,
  slotForAction,
  useKeyBindingsStore,
  type BindableAction,
} from './keyBindings'
import { MOVEMENT_SLOT_ORDER } from '@/map/bearing'

/**
 * 自訂按鍵的測試重點在「不可能把自己鎖死」：
 * 綁定衝突若處理成「把舊的清掉」，就會出現某個動作沒有按鍵、玩家得自己發現的狀況；
 * 壞掉的 localStorage 內容若整包直接信任，症狀是「某個動作永遠按不到」，都很難查。
 */

/** node 環境沒有 localStorage，用最小的記憶體版頂替（同時也讓測試彼此隔離）。 */
function installLocalStorage(initial: Record<string, string> = {}) {
  const data = new Map(Object.entries(initial))
  const stub = {
    getItem: (k: string) => data.get(k) ?? null,
    setItem: (k: string, v: string) => void data.set(k, v),
    removeItem: (k: string) => void data.delete(k),
    clear: () => data.clear(),
    key: (i: number) => [...data.keys()][i] ?? null,
    get length() {
      return data.size
    },
  }
  ;(globalThis as { localStorage?: unknown }).localStorage = stub
  return data
}

const STORAGE_KEY = 'planet.keyBindings.v1'

beforeEach(() => {
  installLocalStorage()
  setActivePinia(createPinia())
})

describe('isBindableKey', () => {
  it.each(['w', 'e', ',', '.', '1', 'ㄅ'])('接受單一字元 %s', (key) => {
    expect(isBindableKey(key)).toBe(true)
  })

  it.each(['Shift', 'Control', 'F5', 'ArrowUp', 'Home', '', ' '])('拒絕 %s', (key) => {
    expect(isBindableKey(key)).toBe(false)
  })
})

describe('actionForSlot / slotForAction', () => {
  it('六個移動插槽都能來回轉換而不變形', () => {
    for (const slot of MOVEMENT_SLOT_ORDER) {
      const action = actionForSlot(slot)
      expect(slotForAction(action as `move${Capitalize<typeof slot>}`)).toBe(slot)
    }
  })

  it('面板上列出的動作剛好涵蓋全部可綁定動作，沒有漏也沒有多', () => {
    const listed = ACTION_LABELS.map((entry) => entry.action).sort()
    const all = (Object.keys(DEFAULT_BINDINGS) as BindableAction[]).sort()
    expect(listed).toEqual(all)
  })
})

describe('useKeyBindingsStore', () => {
  it('預設配置：六個方向鍵＋原地鍵＋兩個轉向鍵，而且沒有任何重複', () => {
    const store = useKeyBindingsStore()
    expect(store.keyFor('moveUpLeft')).toBe('w')
    expect(store.keyFor('stay')).toBe('s')
    expect(store.keyFor('turnLeft')).toBe(',')
    expect(store.keyFor('turnRight')).toBe('.')

    const keys = Object.values(store.bindings)
    expect(new Set(keys).size).toBe(keys.length)
  })

  it('actionFor 不分大小寫（按 Shift+W 也要能走）', () => {
    const store = useKeyBindingsStore()
    expect(store.actionFor('W')).toBe('moveUpLeft')
    expect(store.actionFor('w')).toBe('moveUpLeft')
  })

  it('綁到沒人用的鍵：直接生效，其他動作不受影響', () => {
    const store = useKeyBindingsStore()
    expect(store.setBinding('turnLeft', 'q')).toBeUndefined()
    expect(store.keyFor('turnLeft')).toBe('q')
    expect(store.actionFor('q')).toBe('turnLeft')
    expect(store.actionFor(',')).toBeUndefined()
    expect(store.keyFor('moveUpLeft')).toBe('w')
  })

  it('綁到別人在用的鍵：兩個動作互換，不會有動作變成沒有按鍵', () => {
    const store = useKeyBindingsStore()
    // turnLeft 原本是 ','，moveUpLeft 原本是 'w'
    expect(store.setBinding('turnLeft', 'w')).toBe('moveUpLeft')
    expect(store.keyFor('turnLeft')).toBe('w')
    expect(store.keyFor('moveUpLeft')).toBe(',') // 拿到 turnLeft 原本的鍵，而不是變成空的

    const keys = Object.values(store.bindings)
    expect(new Set(keys).size).toBe(keys.length)
  })

  it('大寫輸入會正規化成小寫，不會產生「W 和 w 各綁一個動作」', () => {
    const store = useKeyBindingsStore()
    store.setBinding('turnLeft', 'Q')
    expect(store.keyFor('turnLeft')).toBe('q')
    expect(store.actionFor('q')).toBe('turnLeft')
  })

  it('綁成自己目前已經是的鍵：不算衝突、也不會把自己換掉', () => {
    const store = useKeyBindingsStore()
    expect(store.setBinding('moveUpLeft', 'w')).toBeUndefined()
    expect(store.keyFor('moveUpLeft')).toBe('w')
  })

  it('拒絕多字元的按鍵（Shift/F5 之類），維持原本的綁定', () => {
    const store = useKeyBindingsStore()
    store.setBinding('turnLeft', 'Shift')
    expect(store.keyFor('turnLeft')).toBe(',')
  })

  it('恢復預設', () => {
    const store = useKeyBindingsStore()
    store.setBinding('turnLeft', 'q')
    store.resetToDefaults()
    expect(store.keyFor('turnLeft')).toBe(',')
  })

  it('會寫進 localStorage，重開之後沿用', () => {
    const data = installLocalStorage()
    setActivePinia(createPinia())
    useKeyBindingsStore().setBinding('turnRight', 'p')

    // 換一個全新的 pinia，模擬重新整理頁面
    setActivePinia(createPinia())
    expect(useKeyBindingsStore().keyFor('turnRight')).toBe('p')
    expect(JSON.parse(data.get(STORAGE_KEY)!).turnRight).toBe('p')
  })

  it('localStorage 內容壞掉時，逐欄位丟掉壞的、保留好的，不整包放棄', () => {
    installLocalStorage({
      [STORAGE_KEY]: JSON.stringify({ turnLeft: 'q', turnRight: 'Shift', stay: 42 }),
    })
    setActivePinia(createPinia())
    const store = useKeyBindingsStore()

    expect(store.keyFor('turnLeft')).toBe('q') // 好的留下
    expect(store.keyFor('turnRight')).toBe('.') // 多字元 → 退回預設
    expect(store.keyFor('stay')).toBe('s') // 非字串 → 退回預設
  })

  it('localStorage 是無效 JSON 時退回全部預設，不會爆', () => {
    installLocalStorage({ [STORAGE_KEY]: '{ not json' })
    setActivePinia(createPinia())
    expect(useKeyBindingsStore().bindings).toEqual(DEFAULT_BINDINGS)
  })

  it('movementKeys 六個插槽都有對應按鍵，改綁之後也跟著更新', () => {
    const store = useKeyBindingsStore()
    for (const slot of MOVEMENT_SLOT_ORDER) expect(store.movementKeys[slot]).toBeTruthy()

    store.setBinding('moveRight', 'l')
    expect(store.movementKeys.right).toBe('l')
  })
})
