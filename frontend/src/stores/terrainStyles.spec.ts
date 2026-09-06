import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { UNKNOWN_TERRAIN_COLOR } from '@/map/terrainColors'
import type { TerrainStyleDto } from '@/types/terrainStyle'

// 這支測試的重點是「拿不到後端資料時會怎樣」，所以把網路層整個換掉。
const fetchTerrainStyles = vi.fn<() => Promise<TerrainStyleDto[]>>()
vi.mock('@/network/apiClient', () => ({
  fetchTerrainStyles: () => fetchTerrainStyles(),
}))

const { useTerrainStyleStore } = await import('./terrainStyles')

const GRASSLAND: TerrainStyleDto = {
  terrain: 'Grassland',
  nameEn: 'Grassland',
  nameZh: '草地',
  colorDawn: '#88943a',
  colorDay: '#8fae4e',
  colorDusk: '#81822f',
  colorNight: '#24341b',
}

describe('useTerrainStyleStore', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    fetchTerrainStyles.mockReset()
  })

  it('載入成功後依時段回傳資料庫裡的顏色', async () => {
    fetchTerrainStyles.mockResolvedValue([GRASSLAND])
    const store = useTerrainStyleStore()
    await store.load()

    expect(store.isLoaded).toBe(true)
    expect(store.loadError).toBeNull()
    expect(store.getColorHex('Grassland', 'day')).toBe(0x8fae4e)
    expect(store.getColorHex('Grassland', 'night')).toBe(0x24341b)
    expect(store.getDisplayName('Grassland')).toBe('Grassland 草地')
  })

  /**
   * 這是第5項的核心：前端不再保留第二份調色盤，所以拿不到資料時**不可以**
   * 「看起來正常」——必須退到單一中性色，而且錯誤要浮出來讓 UI 顯示。
   * 以前是安靜地退回一份硬編碼的18色調色盤，畫面看起來沒事，
   * 但那份複本會跟資料庫慢慢漂移，而且只有在後端掛掉那一刻才會顯現。
   */
  it('載入失敗時所有地形都退回單一中性色，而不是另一份調色盤', async () => {
    fetchTerrainStyles.mockRejectedValue(new Error('連線失敗'))
    const store = useTerrainStyleStore()
    await store.load()

    expect(store.getColorHex('Grassland', 'day')).toBe(UNKNOWN_TERRAIN_COLOR)
    expect(store.getColorHex('Seawater', 'night')).toBe(UNKNOWN_TERRAIN_COLOR)
    expect(store.getColorHex('VolcanicRock', 'dusk')).toBe(UNKNOWN_TERRAIN_COLOR)
  })

  it('載入失敗時把原因記下來，不再靜默吞掉', async () => {
    fetchTerrainStyles.mockRejectedValue(new Error('連線失敗'))
    const store = useTerrainStyleStore()
    await store.load()

    expect(store.loadError).toBe('連線失敗')
    expect(store.isLoaded).toBe(false) // 沒載成功就不能標記為已載入，否則不會再重試
  })

  it('資料裡沒有的地形也回傳中性色，不會是 undefined 或 NaN', async () => {
    fetchTerrainStyles.mockResolvedValue([GRASSLAND])
    const store = useTerrainStyleStore()
    await store.load()

    expect(store.getColorHex('Swamp', 'day')).toBe(UNKNOWN_TERRAIN_COLOR)
    // 查不到名稱時退回原始列舉值，至少還看得出是什麼地形
    expect(store.getDisplayName('Swamp')).toBe('Swamp')
  })

  it('只會實際載入一次（這是全域共用的靜態參考資料）', async () => {
    fetchTerrainStyles.mockResolvedValue([GRASSLAND])
    const store = useTerrainStyleStore()
    await store.load()
    await store.load()

    expect(fetchTerrainStyles).toHaveBeenCalledTimes(1)
  })

  it('多個元件同時呼叫時共用同一個請求（只靠 isLoaded 擋不住併發）', async () => {
    fetchTerrainStyles.mockResolvedValue([GRASSLAND])
    const store = useTerrainStyleStore()

    // 不 await 第一個就發第二、三個——這正是 PlanetScene 與 CoordinateHud 的實際情形
    await Promise.all([store.load(), store.load(), store.load()])

    expect(fetchTerrainStyles).toHaveBeenCalledTimes(1)
    expect(store.getColorHex('Grassland', 'day')).toBe(0x8fae4e)
  })

  it('先失敗後再呼叫會重試（不能因為失敗過就永遠不再嘗試）', async () => {
    fetchTerrainStyles.mockRejectedValueOnce(new Error('暫時性錯誤'))
    fetchTerrainStyles.mockResolvedValue([GRASSLAND])
    const store = useTerrainStyleStore()

    await store.load()
    expect(store.loadError).not.toBeNull()

    await store.load()
    expect(store.loadError).toBeNull()
    expect(store.getColorHex('Grassland', 'day')).toBe(0x8fae4e)
  })
})
