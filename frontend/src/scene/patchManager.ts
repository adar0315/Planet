import * as THREE from 'three'
import { fetchTilesNear } from '@/network/apiClient'
import { buildTileOutline, type TileMeshOptions } from '@/map/tileMeshBuilder'
import { buildTilePatchMeshSliced, type TilePatchMesh } from '@/map/tilePatchMesh'
import { createTilePatch, neighborsOf, type TilePatch } from '@/map/tilePatch'
import { determineDayPeriod } from '@/map/solarTime'
import { monthPosition, seasonalTerrain } from '@/map/seasons'
import type { SeasonData } from './seasonData'
import { useGameStateStore } from '@/stores/gameState'
import { useTerrainStyleStore } from '@/stores/terrainStyles'
import type { TileDto } from '@/types/tile'
import { NEARBY_TILE_COUNT } from './planetSceneConstants'

export interface PatchLoadResult {
  patch: TilePatch
  mesh: TilePatchMesh
}

export interface PatchManager {
  readonly patch: TilePatch | null
  readonly patchMesh: TilePatchMesh | null
  /**
   * 載入某個經緯度附近的一批Tile並換掉場景中的patch。
   * **只負責patch本身**——不決定玩家在哪一格、也不動鏡頭的縮放範圍，那些是呼叫端的事
   * （呼叫端在初次載入時要記得呼叫 cameraRig.setZoomLimits，順序見 PlanetScene.vue 的說明）。
   * 回傳 null 代表這批座標查無Tile，或建構途中元件被卸載（cancel）——兩種情況都不會動到目前的patch。
   */
  loadPatch(latitude: number, longitude: number): Promise<PatchLoadResult | null>
  /** 遊戲時間變動時呼叫，只立旗標；實際重新上色留給 flushColorsIfDirty，確保一幀最多做一次。 */
  markColorsDirty(): void
  /** 每幀呼叫。若旗標已立就重新上色一次並清旗標，回傳是否真的做了——呼叫端可能要順便處理其他跟著時段變的東西（例如遠景貼圖的色調）。 */
  flushColorsIfDirty(): boolean
  /** 標示這些Tile的外框（亮黃色，可點擊移動的鄰居）。傳入的是完整Tile清單，不是插槽——patchManager不需要知道「哪個方向鍵對到哪一格」。 */
  highlightNeighbors(neighbors: Iterable<TileDto>): void
  /**
   * 某一格（地塊或近看細分的小格）在目前遊戲時間的顏色：時段（清晨／白天／黃昏／夜晚）＋季節（積雪、結冰、河的水位）。
   * 小格（isSubTile）的河依中心線距離決定；地塊的河是「範圍裡有河就整格是河」，拉遠時用（水文地貌規劃 H6/H8）。
   */
  colorFor(tile: TileDto, isSubTile: boolean): number
  dispose(): void
}

/**
 * 管理目前畫面上的地塊資料：patch本身、合併mesh、鄰居外框、時段換色。
 *
 * @param scene tileGroup／outlineGroup 會直接加進這個場景。
 * @param meshOptions 尺寸換算設定，跟 cameraRig 用同一份。
 * @param isCancelled 建mesh是分幀進行的（見 buildTilePatchMeshSliced），這段期間元件可能被卸載，
 *   每次交還控制權後會檢查一次，是的話中止並讓 loadPatch 回傳 null。
 */
export function createPatchManager(
  scene: THREE.Scene,
  meshOptions: TileMeshOptions,
  gameState: ReturnType<typeof useGameStateStore>,
  terrainStyleStore: ReturnType<typeof useTerrainStyleStore>,
  isCancelled: () => boolean,
  seasonData: SeasonData,
): PatchManager {
  const tileGroup = new THREE.Group()
  scene.add(tileGroup)
  const outlineGroup = new THREE.Group()
  scene.add(outlineGroup)

  // 目前畫面上的合併patch mesh（整個patch只有一個Mesh＝一次draw call，見 tilePatchMesh.ts）。
  let patch: TilePatch | null = null
  let patchMesh: TilePatchMesh | null = null
  // 遊戲時間變動時只立旗標，實際重新上色留到下一幀做——移動模式下長按方向鍵時
  // advanceTime會以按鍵連發速率觸發，直接每次都重算等於一秒做十幾次全量上色。
  let colorsDirty = false

  function clearTileGroup() {
    tileGroup.clear()
    patchMesh?.dispose()
    patchMesh = null
  }

  /** 依Tile所在經緯度＋目前遊戲時間，算出清晨/白天/黃昏/夜晚哪個時段、哪個季節對應的顏色。 */
  function colorFor(tile: TileDto, isSubTile: boolean): number {
    const period = determineDayPeriod(tile.latitude, tile.longitude, gameState.gameTime)
    const terrain = seasonalTerrain(tile, monthPosition(gameState.gameTime), seasonData.seasonsOf, isSubTile)
    return terrainStyleStore.getColorHex(terrain, period)
  }

  function colorForTile(tile: TileDto): number {
    return colorFor(tile, false)
  }

  async function loadPatch(latitude: number, longitude: number): Promise<PatchLoadResult | null> {
    const tiles = await fetchTilesNear(latitude, longitude, NEARBY_TILE_COUNT)
    if (tiles.length === 0) return null

    const next = createTilePatch(tiles)

    // 先把新的mesh整個建好，最後才一次換掉——中途不留下「patch跟mesh對不起來」的中間狀態。
    // 傳鄰居進去，格子才能用「共用角落」畫成真正密合的蜂窩。
    // neighborsOf 只回傳「在這個patch裡」的鄰居，所以最外圈的格子拿到的數量會少於邊數，
    // computeTilePolygon 會自動退回舊的近似畫法——那些格子在畫面外，不影響觀感。
    //
    // 用切片版本：5萬格一次建完約217ms，走動中會明顯頓一下。分幾幀做完雖然總時間略長，
    // 但畫面與輸入全程流暢；這段期間舊的patch仍掛在場景上，看不到任何空缺。
    const nextMesh = await buildTilePatchMeshSliced(
      tiles,
      meshOptions,
      colorForTile,
      (tile) => neighborsOf(next, tile.tileId),
      { isCancelled },
    )
    if (!nextMesh) return null

    clearTileGroup()
    patch = next
    patchMesh = nextMesh
    gameState.visibleTiles = tiles
    tileGroup.add(nextMesh.mesh)
    // 切片建構期間遊戲時間可能剛好跨過時段，先建好的格子會拿到舊顏色、後建的拿到新顏色。
    // 換上去之後統一重新上色一次，確保整片顏色一致。
    colorsDirty = true
    // 四季資料（積雪、結冰、河的水位）抓到之後再重新上色一次；沒有也照常畫
    void seasonData.ensure(tiles).then((loaded) => {
      if (loaded) colorsDirty = true
    })

    return { patch: next, mesh: nextMesh }
  }

  function markColorsDirty() {
    colorsDirty = true
  }

  function flushColorsIfDirty(): boolean {
    if (!colorsDirty) return false
    colorsDirty = false
    patchMesh?.recolorAll(colorForTile)
    return true
  }

  /** 標示玩家目前站的格子周圍，哪些鄰居是可以點擊移動過去的（亮黃色外框）。 */
  function highlightNeighbors(neighbors: Iterable<TileDto>) {
    for (const child of outlineGroup.children) {
      if (child instanceof THREE.LineLoop) child.geometry.dispose()
    }
    outlineGroup.clear()
    for (const tile of neighbors) {
      // 一定要跟地形mesh傳同一份鄰居，否則外框會用近似畫法、跟腳下的格子邊界對不齊。
      outlineGroup.add(
        buildTileOutline(tile, meshOptions, patch ? neighborsOf(patch, tile.tileId) : undefined),
      )
    }
  }

  function dispose() {
    clearTileGroup()
    for (const child of outlineGroup.children) {
      if (child instanceof THREE.LineLoop) child.geometry.dispose()
    }
    outlineGroup.clear()
  }

  return {
    get patch() {
      return patch
    },
    get patchMesh() {
      return patchMesh
    },
    loadPatch,
    colorFor,
    markColorsDirty,
    flushColorsIfDirty,
    highlightNeighbors,
    dispose,
  }
}
