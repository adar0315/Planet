import type { CellSeasonsDto, SubTileDto, TileDto } from '@/types/tile'
import type { TerrainStyleDto } from '@/types/terrainStyle'
import type { TerrainGenerationOptionsDto } from '@/types/terrainOptions'

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5202'

export async function fetchTilesNear(
  latitude: number,
  longitude: number,
  radiusCount = 250,
): Promise<TileDto[]> {
  const url = new URL('/api/tiles/nearby', API_BASE_URL)
  url.searchParams.set('lat', String(latitude))
  url.searchParams.set('lon', String(longitude))
  url.searchParams.set('count', String(radiusCount))

  const response = await fetch(url)
  if (!response.ok) {
    throw new Error(`取得地圖資料失敗: ${response.status}`)
  }
  return response.json()
}

/**
 * 近看細分（水文地貌規劃 H7）：這些地塊底下約 250 公尺一格的小六角，含鄰居與河道的資訊。
 * 用 POST：一次上千個地塊編號放在網址上會超過長度限制。
 */
export async function fetchSubTiles(tileIds: readonly number[]): Promise<SubTileDto[]> {
  const response = await fetch(new URL('/api/tiles/subtiles', API_BASE_URL), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ tileIds }),
  })
  if (!response.ok) throw new Error(`取得細分地塊失敗: ${response.status}`)
  // 後端的編號欄位叫 subTileId，前端沿用 TileDto 的 tileId。漏了這一步的話 tileId 全是 undefined：
  // 鄰居一個都查不到，每格都退回固定半徑的近似畫法，六角形互相重疊（2026-10-26 使用者回報「顯示有問題」）
  const raw: (Omit<SubTileDto, 'tileId'> & { subTileId: number })[] = await response.json()
  return raw.map(({ subTileId, ...rest }) => ({ ...rest, tileId: subTileId }))
}

/** 模擬粗格的四季（水文地貌規劃 H8）。程序化的星球回傳空陣列。 */
export async function fetchCellSeasons(cellIds: readonly number[]): Promise<CellSeasonsDto[]> {
  const response = await fetch(new URL('/api/tiles/cell-seasons', API_BASE_URL), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ cellIds }),
  })
  if (!response.ok) throw new Error(`取得四季資料失敗: ${response.status}`)
  return response.json()
}

export async function fetchTileNeighbors(tileId: number): Promise<TileDto[]> {
  const url = new URL(`/api/tiles/${tileId}/neighbors`, API_BASE_URL)
  const response = await fetch(url)
  if (!response.ok) {
    throw new Error(`取得鄰居資料失敗: ${response.status}`)
  }
  return response.json()
}

/** 全星球總覽貼圖的原始RGBA位元組（1024×512×4），不是JSON，見後端OverviewTextureGenerator的說明。 */
export async function fetchOverviewTexture(): Promise<ArrayBuffer> {
  const url = new URL('/api/tiles/overview-texture', API_BASE_URL)
  const response = await fetch(url)
  if (!response.ok) {
    throw new Error(`取得星球總覽貼圖失敗: ${response.status}`)
  }
  return response.arrayBuffer()
}

export interface SeedingStatus {
  isReady: boolean
  isSeeding: boolean
  tilesWritten: number
  totalTiles: number
  errorMessage: string | null
}

/** 星球資料的生成狀態，服務首次啟動（資料庫是空的）時會在背景生成整個星球，這支API查目前進度。 */
export async function fetchSeedingStatus(): Promise<SeedingStatus> {
  const url = new URL('/api/status', API_BASE_URL)
  const response = await fetch(url)
  if (!response.ok) {
    throw new Error(`取得星球生成狀態失敗: ${response.status}`)
  }
  return response.json()
}

export interface PlanetInfoDto {
  radiusMeters: number
  tileEdgeLengthMeters: number
  subdivisionFrequency: number
  totalTileCount: number
  overviewTextureWidth: number
  overviewTextureHeight: number
  /** 陸海遮罩的尺寸，對應 fetchWaterMask()。 */
  waterMaskWidth: number
  waterMaskHeight: number
  generatedAtUtc: string | null
  /** true = 這份記錄是依預設參數推測的（地塊在此功能加入前就已存在）。 */
  isInferred: boolean
}

/**
 * 星球的基本尺寸與貼圖規格。這些值後端是從細分頻率推導出來的，
 * 前端不要自己寫死一份（寫死的話改了細分頻率就會安靜地用錯誤比例畫格子）。
 */
export async function fetchPlanetInfo(): Promise<PlanetInfoDto> {
  const url = new URL('/api/planet', API_BASE_URL)
  const response = await fetch(url)
  if (!response.ok) {
    throw new Error(`取得星球資訊失敗: ${response.status}`)
  }
  return response.json()
}

/** 全部18種地形的顯示樣式（中英文名稱＋依時段變化的顏色）。 */
export async function fetchTerrainStyles(): Promise<TerrainStyleDto[]> {
  const url = new URL('/api/terrain-styles', API_BASE_URL)
  const response = await fetch(url)
  if (!response.ok) {
    throw new Error(`取得地形樣式失敗: ${response.status}`)
  }
  return response.json()
}

/**
 * 全星球陸海遮罩（每像素1位元組：1＝水、0＝陸地），給右下角位置指示器畫大陸輪廓用。
 * 尺寸見 PlanetInfoDto.waterMaskWidth/Height，不要在前端寫死。
 * 刻意跟 2MB 的總覽貼圖分開——為了120px的小圖示載2MB不合理，這份只有8KB。
 */
export async function fetchWaterMask(): Promise<Uint8Array> {
  const url = new URL('/api/tiles/overview-water-mask', API_BASE_URL)
  const response = await fetch(url)
  if (!response.ok) {
    throw new Error(`取得陸海遮罩失敗: ${response.status}`)
  }
  return new Uint8Array(await response.arrayBuffer())
}

/** 管理端點要求的金鑰標頭名稱（對應後端 AdminApiKeyFilter.HeaderName）。 */
const ADMIN_KEY_HEADER = 'X-Admin-Key'

/**
 * 管理端點的共用錯誤處理。這幾個端點會清空整顆星球，後端要求 X-Admin-Key，
 * 而且伺服器沒設定金鑰時是「停用」（503）而不是「不驗證」——兩種情況要給不同的說明。
 */
async function adminFetch(path: string, adminKey: string, init?: RequestInit): Promise<Response> {
  const url = new URL(path, API_BASE_URL)
  const response = await fetch(url, {
    ...init,
    headers: { ...(init?.headers ?? {}), [ADMIN_KEY_HEADER]: adminKey },
  })

  if (response.status === 401) {
    throw new Error('管理金鑰錯誤或未提供。')
  }
  if (response.status === 503) {
    throw new Error('伺服器尚未設定管理金鑰（Admin:ApiKey），管理功能已停用。')
  }
  return response
}

/** 目前的星球地形生成參數（下次「重新生成」會使用的參數）。 */
export async function fetchTerrainOptions(adminKey: string): Promise<TerrainGenerationOptionsDto> {
  const response = await adminFetch('/api/admin/terrain-options', adminKey)
  if (!response.ok) {
    throw new Error(`取得地形生成參數失敗: ${response.status}`)
  }
  return response.json()
}

/** 更新地形生成參數。只是存起來，不會立刻改變目前的星球，要另外呼叫 triggerRegeneration()。 */
export async function updateTerrainOptions(
  options: TerrainGenerationOptionsDto,
  adminKey: string,
): Promise<TerrainGenerationOptionsDto> {
  const response = await adminFetch('/api/admin/terrain-options', adminKey, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(options),
  })
  if (!response.ok) {
    throw new Error(`更新地形生成參數失敗: ${response.status}`)
  }
  return response.json()
}

/** 觸發背景「清空並重新生成整顆星球」（用目前存好的地形生成參數）。無法復原，呼叫前UI要自行做好確認流程。 */
export async function triggerRegeneration(adminKey: string): Promise<void> {
  const response = await adminFetch('/api/admin/regenerate', adminKey, { method: 'POST' })
  if (response.status === 409) {
    throw new Error('已經有星球生成作業正在進行中，請等它完成後再試一次。')
  }
  if (!response.ok) {
    throw new Error(`觸發重新生成失敗: ${response.status}`)
  }
}

// ---------------------------------------------------------------------------
// 地質演化模擬（規劃見 docs/geological-simulation-plan.md）
// ---------------------------------------------------------------------------

/** 模擬可以顯示的欄位。字串值直接當成 query 參數送給後端，要跟後端的 SimulationField 列舉一致。 */
export type SimulationFieldName =
  | 'Elevation'
  | 'ElevationWithPlateBoundaries'
  | 'WaterDepth'
  | 'Temperature'
  | 'Precipitation'
  | 'Runoff'
  | 'CoralReef'
  | 'CrustType'
  | 'CrustThickness'
  | 'CrustAge'
  | 'SedimentThickness'
  | 'PlateId'
  | 'BoundaryType'
  | 'VolcanicActivity'
  | 'IceThickness'
  | 'RiversAndLakes'
  | 'Lithology'
  | 'Landforms'

export interface SimulationStatus {
  frequency: number
  cellCount: number
  planetRadiusMeters: number
  averageCellEdgeMeters: number
  elapsedYears: number
  stepCount: number
  yearsPerStep: number
  plateCount: number
  plumeCount: number
  landFraction: number
  /** 貼圖尺寸由後端提供，前端不要寫死（跟 overviewTextureWidth 同樣的理由）。 */
  textureWidth: number
  textureHeight: number
  /** 最近一次檢查的陸地配置（停止條件的判斷依據），還沒檢查過是 null。 */
  continents: ContinentConfiguration | null
  /** 這次推進是不是因為停止條件成立而提早停下。看到 true 就要停止播放。 */
  stoppedByCondition: boolean
  /** 停止條件成立時自動存檔的名稱。 */
  autoSaveName: string | null
}

/**
 * 停止條件「陸地集中成兩大塊＋至少兩個大島」的判斷依據。比例都是佔陸地面積的比例（排除碎島）。
 * 門檻在後端（ContinentCriteria），前端只顯示。
 */
export interface ContinentConfiguration {
  largestShare: number
  secondShare: number
  twoLargestShare: number
  bigIslandCount: number
  /** 海岸破碎度：海岸格數 ÷ 同面積圓盤的海岸格數。1 ＝ 圓盤，愈大愈破碎。 */
  largestRaggedness: number
  secondRaggedness: number
  /** 陸地（排除碎島）佔星球表面的比例。 */
  landShareOfSurface: number
  landMassCount: number
  oceanFraction: number
  isMet: boolean
}

export async function fetchSimulationStatus(): Promise<SimulationStatus> {
  const url = new URL('/api/simulation/status', API_BASE_URL)
  const response = await fetch(url)
  if (!response.ok) {
    throw new Error(`取得模擬狀態失敗: ${response.status}`)
  }
  return response.json()
}

/** 推進模擬。後端對單次請求的步數有上限（目前2000），超過會回400。 */
export async function stepSimulation(steps: number): Promise<SimulationStatus> {
  const url = new URL('/api/simulation/step', API_BASE_URL)
  url.searchParams.set('steps', String(steps))
  const response = await fetch(url, { method: 'POST' })
  if (!response.ok) {
    throw new Error(`推進模擬失敗: ${response.status}`)
  }
  return response.json()
}

/** 把模擬重設回起始的星球。 */
export async function resetSimulation(): Promise<SimulationStatus> {
  const url = new URL('/api/simulation/reset', API_BASE_URL)
  const response = await fetch(url, { method: 'POST' })
  if (!response.ok) {
    throw new Error(`重設模擬失敗: ${response.status}`)
  }
  return response.json()
}

/** 指定欄位的等距柱狀投影原始RGBA位元組。取樣慣例跟 fetchOverviewTexture 完全一致。 */
export async function fetchSimulationTexture(field: SimulationFieldName): Promise<ArrayBuffer> {
  const url = new URL('/api/simulation/texture', API_BASE_URL)
  url.searchParams.set('field', field)
  const response = await fetch(url)
  if (!response.ok) {
    throw new Error(`取得模擬貼圖失敗: ${response.status}`)
  }
  return response.arrayBuffer()
}

/** 存檔怎麼來的（後端 SnapshotKind）。舊版沒有說明檔的存檔是 null。 */
export type SnapshotKind = 'Manual' | 'Checkpoint' | 'StopCondition'

export interface SnapshotInfo {
  name: string
  sizeBytes: number
  savedAtUtc: string
  kind: SnapshotKind | null
  stepCount: number | null
  elapsedYears: number | null
  /** 這個版本讀不讀得回來。false 的（舊格式、v1 的存檔）列出來但不能載入或放大。 */
  compatible: boolean
}

/** 後端回 ProblemDetails 時把 detail 拿出來當錯誤訊息，沒有就用預設說明。 */
async function problem(response: Response, fallback: string): Promise<Error> {
  try {
    const body = await response.json()
    if (body && typeof body.detail === 'string') return new Error(body.detail)
    if (body && typeof body.message === 'string') return new Error(body.message)
  } catch {
    // 不是 JSON，用預設說明
  }
  return new Error(`${fallback}: ${response.status}`)
}

/** 列出所有存檔（最新的在前）。 */
export async function fetchSimulationSaves(): Promise<SnapshotInfo[]> {
  const response = await fetch(new URL('/api/simulation/saves', API_BASE_URL))
  if (!response.ok) throw await problem(response, '取得存檔清單失敗')
  return response.json()
}

/** 把目前的模擬存成指定名稱（英數、底線、連字號；同名會覆蓋）。 */
export async function saveSimulation(name: string): Promise<void> {
  const url = new URL('/api/simulation/save', API_BASE_URL)
  url.searchParams.set('name', name)
  const response = await fetch(url, { method: 'POST' })
  if (!response.ok) throw await problem(response, '存檔失敗')
}

/** 載入存檔，覆蓋目前的模擬。 */
export async function loadSimulation(name: string): Promise<SimulationStatus> {
  const url = new URL('/api/simulation/load', API_BASE_URL)
  url.searchParams.set('name', name)
  const response = await fetch(url, { method: 'POST' })
  if (!response.ok) throw await problem(response, '載入存檔失敗')
  return response.json()
}

/** 刪除存檔。不可復原，呼叫前 UI 要自己確認。 */
export async function deleteSimulationSave(name: string): Promise<void> {
  const response = await fetch(new URL(`/api/simulation/saves/${encodeURIComponent(name)}`, API_BASE_URL), {
    method: 'DELETE',
  })
  if (!response.ok) throw await problem(response, '刪除存檔失敗')
}

/** 放大作業的狀態（後端 MaterializationStatus）。 */
export interface MaterializationStatus {
  isRunning: boolean
  snapshotName: string | null
  phase: string | null
  tilesWritten: number
  totalTiles: number
  sunkIslandCells: number
  elapsedSeconds: number
  error: string | null
}

/**
 * 把存檔清碎島、放大成走動模式的 2,000 萬地塊（**覆蓋目前的星球**，約 10 分鐘）。管理端點，要管理金鑰。
 * 立刻回傳，用 fetchMaterializationStatus 查進度。
 */
export async function startMaterialization(snapshot: string, adminKey: string): Promise<void> {
  const url = new URL('/api/admin/materialize', API_BASE_URL)
  url.searchParams.set('snapshot', snapshot)
  const response = await adminFetch(url.pathname + url.search, adminKey, { method: 'POST' })
  if (response.status === 409) throw new Error('已經有星球生成或放大作業正在進行中，請等它完成後再試一次。')
  if (!response.ok) throw await problem(response, '開始放大失敗')
}

/** 目前（或最近一次）放大作業的狀態。 */
export async function fetchMaterializationStatus(adminKey: string): Promise<MaterializationStatus> {
  const response = await adminFetch('/api/admin/materialize', adminKey)
  if (!response.ok) throw await problem(response, '取得放大進度失敗')
  return response.json()
}
