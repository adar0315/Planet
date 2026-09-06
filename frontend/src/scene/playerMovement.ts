import { fetchTileNeighbors } from '@/network/apiClient'
import { requestMoveTo } from '@/network/signalrClient'
import { edgeDepthOf, hasCompleteNeighbors, neighborsOf } from '@/map/tilePatch'
import { assignNeighborsToSlots, type MovementSlot } from '@/map/bearing'
import { useGameStateStore } from '@/stores/gameState'
import type { TileDto } from '@/types/tile'
import type { CameraRig } from './cameraRig'
import type { PatchManager } from './patchManager'
import { REFETCH_DEPTH_THRESHOLD } from './planetSceneConstants'

export interface PlayerMovement {
  /** 目前「哪個方向鍵走到哪一格」，六角格最多6個、五角格最多5個。inputHandlers 查表用。 */
  readonly neighborsBySlot: ReadonlyMap<MovementSlot, TileDto>
  /**
   * 設定玩家目前所在格：更新狀態、平移鏡頭、重算方向鍵對應與鄰居外框。
   * 全部同步完成，中途沒有任何await——這是拿掉 isMoving 輸入閘門的前提
   * （舊版在狀態轉換中間await了三次網路來回，那段期間各項狀態互相矛盾，只好用閘門擋住輸入）。
   */
  setPlayerTile(tile: TileDto, opts: { recenter: boolean; distance?: number }): void
  /**
   * 走到相鄰的一格。**同步完成、立即生效**，伺服器驗證在背景跑（見內部的 validateMoveInBackground）。
   *
   * 舊版是「先等SignalR驗證→再等重新抓2000格→再等抓鄰居」三次序列往返（實測約200ms），
   * 期間用 isMoving 把後續按鍵整個丟棄（不是排隊），所以連按方向鍵只會走一格。
   * 現在客戶端是照著「伺服器自己算出來的鄰接關係」在走（neighborIds由後端提供），
   * 所以本地判斷跟伺服器一致，可以先動再驗證。
   */
  moveToTile(target: TileDto): void
  /** 重算「哪個方向鍵走到哪一格」。優先用patch裡現成的鄰接資料，資料不完整才退回打API。 */
  rebuildNeighborKeys(tile: TileDto): void
  /** 玩家接近已載入範圍邊緣時，在背景補載下一批。舊patch在新的載好之前完全保持可用，不會卡住或破圖。 */
  ensurePatchAroundPlayer(): Promise<void>
}

/**
 * 管理玩家在patch裡的位置與移動：目前所在格、方向鍵對應、移動時的鏡頭跟隨與伺服器驗證。
 *
 * @param cameraRig 移動/轉向時要跟著動的鏡頭。
 * @param patchManager 目前的地塊資料，移動與補載都要讀寫它。
 * @param setMoveWarning 回報移動相關的提示（被伺服器拒絕、離線未驗證等），交給呼叫端顯示在畫面上。
 */
export function createPlayerMovement(
  gameState: ReturnType<typeof useGameStateStore>,
  cameraRig: CameraRig,
  patchManager: PatchManager,
  setMoveWarning: (message: string | null) => void,
): PlayerMovement {
  let neighborsBySlot = new Map<MovementSlot, TileDto>()
  let playerTileId = -1
  let isFetchingPatch = false
  // 每次rollback就+1，讓還在路上的舊驗證結果失效（避免過期的回應把玩家彈到別的地方）。
  let moveGeneration = 0

  function rebuildNeighborKeys(tile: TileDto) {
    const patch = patchManager.patch
    if (patch && hasCompleteNeighbors(patch, tile.tileId)) {
      neighborsBySlot = assignNeighborsToSlots(
        tile,
        neighborsOf(patch, tile.tileId),
        gameState.facingDeg,
      )
      patchManager.highlightNeighbors(neighborsBySlot.values())
      return
    }

    // 鄰居資料不齊（走到patch最外圈，或後端沒回neighborIds）：這時候不能用殘缺的清單建方向鍵，
    // 少掉的方向會被錯誤地指派給別的鄰居。改成打一次API補齊。
    // 正常情況下邊緣預載入會讓玩家根本走不到這裡，這只是保底。
    neighborsBySlot = new Map()
    patchManager.highlightNeighbors(neighborsBySlot.values())
    void fetchTileNeighbors(tile.tileId)
      .then((neighbors) => {
        if (playerTileId !== tile.tileId) return // 已經又走掉了，這份結果過期
        neighborsBySlot = assignNeighborsToSlots(tile, neighbors, gameState.facingDeg)
        patchManager.highlightNeighbors(neighborsBySlot.values())
      })
      .catch(() => {
        /* 補不到就維持沒有方向鍵，下一次移動或載入會再試 */
      })
  }

  function setPlayerTile(tile: TileDto, opts: { recenter: boolean; distance?: number }) {
    playerTileId = tile.tileId
    gameState.currentTile = tile
    cameraRig.followPlayerTile(tile, opts)
    rebuildNeighborKeys(tile)
  }

  /**
   * 背景向伺服器確認這一步合法。單人情境下幾乎不可能被拒——客戶端本來就是照伺服器算出來的
   * 鄰接關係在走——所以真的被拒代表鄰接資料有問題，這裡等於是最高風險改動的回歸偵測器。
   */
  function validateMoveInBackground(from: TileDto, target: TileDto) {
    const generation = moveGeneration
    requestMoveTo(from.tileId, target.tileId)
      .then((confirmed) => {
        if (generation !== moveGeneration) return // 已經被某次rollback取代，這份結果過期
        if (confirmed) return // 一致，正常情況

        // 伺服器說這步不合法：退回上一格。
        moveGeneration++
        setMoveWarning('移動被伺服器拒絕，已退回上一格')
        const patch = patchManager.patch
        const restored = patch?.indexByTileId.has(from.tileId)
          ? patch.tiles[patch.indexByTileId.get(from.tileId)!]!
          : from
        setPlayerTile(restored, { recenter: false })
      })
      .catch(() => {
        // 連不上伺服器 ≠ 這步不合法，不可以因此退回（否則離線時玩家會被彈來彈去）。
        if (generation !== moveGeneration) return
        setMoveWarning('離線中，移動尚未經伺服器驗證')
      })
  }

  function moveToTile(target: TileDto) {
    const from = gameState.currentTile
    if (!from) return

    setPlayerTile(target, { recenter: false })
    if (gameState.mode === 'stepped') gameState.advanceTime(1)

    void ensurePatchAroundPlayer()
    validateMoveInBackground(from, target)
  }

  async function ensurePatchAroundPlayer() {
    const patch = patchManager.patch
    if (!patch || isFetchingPatch) return
    if (edgeDepthOf(patch, playerTileId) > REFETCH_DEPTH_THRESHOLD) return

    const tile = gameState.currentTile
    if (!tile) return

    isFetchingPatch = true
    try {
      await patchManager.loadPatch(tile.latitude, tile.longitude)
      // 換完patch後，玩家那一格要改用新patch裡的物件（同一個tileId，但帶的是新的鄰接資料）。
      const freshPatch = patchManager.patch
      const index = freshPatch?.indexByTileId.get(playerTileId)
      if (index !== undefined) {
        gameState.currentTile = freshPatch!.tiles[index]!
        rebuildNeighborKeys(freshPatch!.tiles[index]!)
      }
    } catch {
      /* 補載失敗就維持用舊patch，玩家還能繼續走，下一步會再試 */
    } finally {
      isFetchingPatch = false
    }
  }

  return {
    get neighborsBySlot() {
      return neighborsBySlot
    },
    setPlayerTile,
    moveToTile,
    rebuildNeighborKeys,
    ensurePatchAroundPlayer,
  }
}
