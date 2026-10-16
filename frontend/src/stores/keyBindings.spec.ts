import { beforeEach, describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import {
  ACTION_LABELS,
  DEFAULT_BINDINGS,
  HELD_ACTIONS,
  isBindableKey,
  useKeyBindingsStore,
  type BindableAction,
} from './keyBindings'

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

const STORAGE_KEY = 'planet.keyBindings.v2'
const LEGACY_STORAGE_KEY_V1 = 'planet.keyBindings.v1'

function expectNoDuplicateKeys(bindings: Record<BindableAction, string>) {
  const keys = Object.values(bindings)
  expect(new Set(keys).size).toBe(keys.length)
}

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

describe('動作清單', () => {
  it('面板上列出的動作剛好涵蓋全部可綁定動作，沒有漏也沒有多', () => {
    const listed = ACTION_LABELS.map((entry) => entry.action).sort()
    const all = (Object.keys(DEFAULT_BINDINGS) as BindableAction[]).sort()
    expect(listed).toEqual(all)
  })

  it('移動與轉向是按住生效的，原地等待不是', () => {
    for (const action of ['moveForward', 'moveBack', 'strafeLeft', 'strafeRight', 'turnLeft', 'turnRight'] as const) {
      expect(HELD_ACTIONS.has(action)).toBe(true)
    }
    expect(HELD_ACTIONS.has('wait')).toBe(false)
  })
})

describe('useKeyBindingsStore', () => {
  it('預設配置：WASD 移動、QE 轉向、Z 原地等待，沒有任何重複', () => {
    const store = useKeyBindingsStore()
    expect(store.keyFor('moveForward')).toBe('w')
    expect(store.keyFor('moveBack')).toBe('s')
    expect(store.keyFor('strafeLeft')).toBe('a')
    expect(store.keyFor('strafeRight')).toBe('d')
    expect(store.keyFor('turnLeft')).toBe('q')
    expect(store.keyFor('turnRight')).toBe('e')
    expect(store.keyFor('wait')).toBe('z')
    expectNoDuplicateKeys(store.bindings)
  })

  it('actionFor 不分大小寫——按住 Shift（跑步）時 event.key 是大寫，也要能走', () => {
    const store = useKeyBindingsStore()
    expect(store.actionFor('W')).toBe('moveForward')
    expect(store.actionFor('w')).toBe('moveForward')
  })

  it('綁到沒人用的鍵：直接生效，其他動作不受影響', () => {
    const store = useKeyBindingsStore()
    expect(store.setBinding('turnLeft', 'j')).toBeUndefined()
    expect(store.keyFor('turnLeft')).toBe('j')
    expect(store.actionFor('j')).toBe('turnLeft')
    expect(store.actionFor('q')).toBeUndefined()
    expect(store.keyFor('moveForward')).toBe('w')
  })

  it('綁到別人在用的鍵：兩個動作互換，不會有動作變成沒有按鍵', () => {
    const store = useKeyBindingsStore()
    expect(store.setBinding('turnLeft', 'w')).toBe('moveForward')
    expect(store.keyFor('turnLeft')).toBe('w')
    expect(store.keyFor('moveForward')).toBe('q') // 拿到 turnLeft 原本的鍵，而不是變成空的
    expectNoDuplicateKeys(store.bindings)
  })

  it('大寫輸入會正規化成小寫，不會產生「W 和 w 各綁一個動作」', () => {
    const store = useKeyBindingsStore()
    store.setBinding('turnLeft', 'J')
    expect(store.keyFor('turnLeft')).toBe('j')
  })

  it('綁成自己目前已經是的鍵：不算衝突、也不會把自己換掉', () => {
    const store = useKeyBindingsStore()
    expect(store.setBinding('moveForward', 'w')).toBeUndefined()
    expect(store.keyFor('moveForward')).toBe('w')
  })

  it('拒絕多字元的按鍵（Shift/F5 之類），維持原本的綁定', () => {
    const store = useKeyBindingsStore()
    store.setBinding('turnLeft', 'Shift')
    expect(store.keyFor('turnLeft')).toBe('q')
  })

  it('恢復預設', () => {
    const store = useKeyBindingsStore()
    store.setBinding('turnLeft', 'j')
    store.resetToDefaults()
    expect(store.keyFor('turnLeft')).toBe('q')
  })

  it('會寫進 localStorage，重開之後沿用', () => {
    const data = installLocalStorage()
    setActivePinia(createPinia())
    useKeyBindingsStore().setBinding('turnRight', 'p')

    setActivePinia(createPinia()) // 模擬重新整理頁面
    expect(useKeyBindingsStore().keyFor('turnRight')).toBe('p')
    expect(JSON.parse(data.get(STORAGE_KEY)!).turnRight).toBe('p')
  })

  it('localStorage 內容壞掉時，逐欄位丟掉壞的、保留好的，不整包放棄', () => {
    installLocalStorage({
      [STORAGE_KEY]: JSON.stringify({ turnLeft: 'j', turnRight: 'Shift', wait: 42 }),
    })
    setActivePinia(createPinia())
    const store = useKeyBindingsStore()

    expect(store.keyFor('turnLeft')).toBe('j') // 好的留下
    expect(store.keyFor('turnRight')).toBe('e') // 多字元 → 退回預設
    expect(store.keyFor('wait')).toBe('z') // 非字串 → 退回預設
  })

  it('存檔裡兩個動作綁到同一個鍵（手動改壞）：讀進來時互換解開，不會出現重複', () => {
    installLocalStorage({ [STORAGE_KEY]: JSON.stringify({ turnLeft: 'w', moveForward: 'w' }) })
    setActivePinia(createPinia())
    expectNoDuplicateKeys(useKeyBindingsStore().bindings)
  })

  it('localStorage 是無效 JSON 時退回全部預設，不會爆', () => {
    installLocalStorage({ [STORAGE_KEY]: '{ not json' })
    setActivePinia(createPinia())
    expect(useKeyBindingsStore().bindings).toEqual(DEFAULT_BINDINGS)
  })
})

describe('從 v1（六方向跳格）遷移', () => {
  it('只帶走語意沒變的「原地」→「原地等待」，六個方向插槽與舊轉向鍵不帶', () => {
    installLocalStorage({
      [LEGACY_STORAGE_KEY_V1]: JSON.stringify({
        moveUpLeft: 'w',
        moveRight: 'l',
        stay: 'x',
        turnLeft: ',',
        turnRight: '.',
      }),
    })
    setActivePinia(createPinia())
    const store = useKeyBindingsStore()

    expect(store.keyFor('wait')).toBe('x')
    expect(store.keyFor('turnLeft')).toBe('q') // 舊的 , 是給「按一下轉60°」挑的位置，不帶
    expect(store.keyFor('strafeRight')).toBe('d') // 舊的 moveRight 沒有對應
    expectNoDuplicateKeys(store.bindings)
  })

  it('v1 帶過來的鍵跟新預設撞鍵時，互換解開', () => {
    installLocalStorage({ [LEGACY_STORAGE_KEY_V1]: JSON.stringify({ stay: 'w' }) })
    setActivePinia(createPinia())
    const store = useKeyBindingsStore()

    expect(store.keyFor('wait')).toBe('w')
    expect(store.keyFor('moveForward')).toBe('z') // 拿到 wait 原本的預設鍵
    expectNoDuplicateKeys(store.bindings)
  })

  it('已經有 v2 存檔時不再讀 v1', () => {
    installLocalStorage({
      [STORAGE_KEY]: JSON.stringify({ wait: 'c' }),
      [LEGACY_STORAGE_KEY_V1]: JSON.stringify({ stay: 'x' }),
    })
    setActivePinia(createPinia())
    expect(useKeyBindingsStore().keyFor('wait')).toBe('c')
  })
})
