import * as THREE from 'three'
import { slotForAction, useKeyBindingsStore } from '@/stores/keyBindings'
import { useGameStateStore } from '@/stores/gameState'
import type { PlayerMovement } from './playerMovement'
import type { PatchManager } from './patchManager'
import { MOVE_COOLDOWN_MS } from './planetSceneConstants'

export interface InputHandlers {
  keydownHandler(event: KeyboardEvent): void
  clickHandler(event: MouseEvent): void
}

/**
 * 鍵盤與滑鼠點擊輸入。兩者都只負責「翻譯成一次 moveToTile / 轉向呼叫」，
 * 實際的移動/轉向邏輯在 playerMovement.ts／gameState store 裡，這裡不重複。
 *
 * @param camera 點擊時做 raycast 用；不會變動，所以直接傳物件而不是getter。
 * @param getRenderer 點擊時要讀 `renderer.domElement` 換算座標；renderer 在元件卸載時會被設成
 *   null（見 PlanetScene.vue 的 disposeScene），用getter確保每次都讀到當下最新的值。
 */
export function createInputHandlers(
  gameState: ReturnType<typeof useGameStateStore>,
  keyBindings: ReturnType<typeof useKeyBindingsStore>,
  movement: PlayerMovement,
  patchManager: PatchManager,
  camera: THREE.PerspectiveCamera,
  getRenderer: () => THREE.WebGLRenderer | null,
): InputHandlers {
  // 長按方向鍵/轉向鍵時的節流。各自獨立計時（轉向跟移動不互相搶額度）。
  let lastMoveAt = 0
  let lastTurnAt = 0

  function keydownHandler(event: KeyboardEvent) {
    // 在輸入欄位裡打字時不要當成移動指令：這是掛在window上的全域監聽，
    // 而管理面板有文字輸入框——金鑰裡只要含 a/d/w/e/z/x/s 就會一邊打字一邊把玩家走掉。
    const eventTarget = event.target as HTMLElement | null
    if (
      eventTarget &&
      (eventTarget.isContentEditable ||
        ['INPUT', 'TEXTAREA', 'SELECT'].includes(eventTarget.tagName))
    ) {
      return
    }

    // 按鍵設定面板正在等待玩家按下要綁定的鍵：這一輪完全不處理任何輸入。
    // 面板自己也會在 capture 階段擋下事件，但那依賴 capture 比 bubble 早跑；這個旗標
    // 跟事件傳遞順序無關，兩道一起才擋得住「改綁按鍵時角色跟著走掉」。
    if (keyBindings.isCapturing) return

    const key = event.key.toLowerCase()

    // Home 鍵：跟資訊欄的「Home」按鈕做同一件事，走同一條路徑（不要各自呼叫，行為才不會分岔）。
    if (key === 'home') {
      event.preventDefault() // 避免瀏覽器把 Home 當成「捲到頁首」
      gameState.requestResetToDefaultView()
      return
    }

    // 其餘按鍵一律查玩家自訂的配置（stores/keyBindings），不要在這裡寫死字母——
    // 寫死的話重新綁定就會失效，而且症狀是「改了設定但沒反應」，很難查。
    const action = keyBindings.actionFor(key)
    if (!action) return

    if (action === 'stay') {
      if (gameState.mode === 'stepped') gameState.advanceTime(1)
      return
    }

    const now = performance.now()

    if (action === 'turnLeft' || action === 'turnRight') {
      if (now - lastTurnAt < MOVE_COOLDOWN_MS) return
      lastTurnAt = now
      if (action === 'turnLeft') gameState.turnLeft()
      else gameState.turnRight()
      return // 實際轉鏡頭與重算方向插槽由 facingDeg 的 watch 統一處理
    }

    // 移動冷卻：拿掉 isMoving 之後，長按方向鍵會以作業系統的連發速率（約30次/秒）觸發，
    // 在移動模式下等於每秒推進30個遊戲小時。冷卻讓長按維持在約11格/秒的合理步行速度。
    if (now - lastMoveAt < MOVE_COOLDOWN_MS) return
    lastMoveAt = now

    const target = movement.neighborsBySlot.get(slotForAction(action))
    if (!target) return // 五角格少一個方向，或鄰居資料還沒補齊
    movement.moveToTile(target)
  }

  const raycaster = new THREE.Raycaster()

  function clickHandler(event: MouseEvent) {
    const renderer = getRenderer()
    const patchMesh = patchManager.patchMesh
    if (!renderer || !patchMesh) return

    const rect = renderer.domElement.getBoundingClientRect()
    const ndc = new THREE.Vector2(
      ((event.clientX - rect.left) / rect.width) * 2 - 1,
      -((event.clientY - rect.top) / rect.height) * 2 + 1,
    )
    raycaster.setFromCamera(ndc, camera)
    // 整個patch現在只有一個Mesh，命中的是「第幾個三角形」（faceIndex），
    // 六角格佔6個三角形、五角格佔5個，用patchMesh的查表換回tileId（不能用固定stride推算）。
    const hit = raycaster.intersectObject(patchMesh.mesh, false)[0]
    if (!hit || hit.faceIndex === undefined || hit.faceIndex === null) return

    const clickedTileId = patchMesh.tileIdAtFace(hit.faceIndex)
    if (clickedTileId === undefined) return

    const target = [...movement.neighborsBySlot.values()].find((t) => t.tileId === clickedTileId)
    if (!target) return // 點到的不是目前所在格的合法鄰居（例如點太遠的格子）

    movement.moveToTile(target)
  }

  return { keydownHandler, clickHandler }
}
