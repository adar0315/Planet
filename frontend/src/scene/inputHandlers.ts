import { HELD_ACTIONS, useKeyBindingsStore } from '@/stores/keyBindings'
import { useGameStateStore } from '@/stores/gameState'
import type { PlayerController } from './playerController'

export interface InputHandlers {
  keydownHandler(event: KeyboardEvent): void
  keyupHandler(event: KeyboardEvent): void
  /** 視窗失去焦點：放開所有按住的鍵。 */
  blurHandler(): void
}

/**
 * 鍵盤輸入，翻譯成 playerController 的「按住／放開」。
 *
 * 2026-10-16 從「每次 keydown 觸發一次跳格」改成追蹤按住的狀態——移動與轉向是持續的，
 * 必須同時知道何時開始（keydown）與何時停止（keyup）。滑鼠拖曳轉身與縮放在 cameraRig.ts。
 *
 * 點擊移動暫時拿掉了：原本是「點相鄰格就跳過去」，連續移動之後要改成「點遠處自動走過去」，
 * 那是規劃中的 M5（見 docs/movement-redesign-plan.md）。
 */
export function createInputHandlers(
  gameState: ReturnType<typeof useGameStateStore>,
  keyBindings: ReturnType<typeof useKeyBindingsStore>,
  controller: PlayerController,
): InputHandlers {
  /** 在輸入欄位裡打字時不要當成移動指令（這是掛在 window 上的全域監聽，CLAUDE.md 記載過的坑）。 */
  function isTypingTarget(event: KeyboardEvent): boolean {
    const target = event.target as HTMLElement | null
    return (
      !!target &&
      (target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName))
    )
  }

  function keydownHandler(event: KeyboardEvent) {
    if (isTypingTarget(event)) return

    // 按鍵設定面板正在等待玩家按下要綁定的鍵：這一輪完全不處理任何輸入。
    // 面板自己也會在 capture 階段擋下事件，但那依賴 capture 比 bubble 早跑；這個旗標
    // 跟事件傳遞順序無關，兩道一起才擋得住「改綁按鍵時角色跟著走掉」。
    if (keyBindings.isCapturing) return

    if (event.key === 'Shift') {
      controller.setRunning(true)
      return
    }

    const key = event.key.toLowerCase()

    // Home 鍵：跟資訊欄的「Home」按鈕走同一條路徑。
    if (key === 'home') {
      event.preventDefault() // 避免瀏覽器把 Home 當成「捲到頁首」
      gameState.requestResetToDefaultView()
      return
    }

    // 一律查玩家自訂的配置，不要在這裡寫死字母——寫死的話重新綁定就會失效，
    // 而且症狀是「改了設定但沒反應」，很難查。
    const action = keyBindings.actionFor(key)
    if (!action) return

    if (HELD_ACTIONS.has(action)) {
      // 長按時作業系統會以約 30 次/秒重複送 keydown，重複設成「按住」沒有副作用
      controller.setHeld(action, true)
      return
    }

    if (action === 'wait' && !event.repeat) {
      if (gameState.mode === 'stepped') gameState.advanceTime(1)
    }
  }

  function keyupHandler(event: KeyboardEvent) {
    if (event.key === 'Shift') {
      controller.setRunning(false)
      return
    }

    // 放開時**不檢查** isCapturing 與輸入欄位：按下時若已經在走，放開的那一刻焦點可能剛好
    // 移到輸入框或設定面板上，擋掉 keyup 會讓角色一直走下去。放開一個沒按住的鍵沒有副作用。
    const action = keyBindings.actionFor(event.key.toLowerCase())
    if (action && HELD_ACTIONS.has(action)) controller.setHeld(action, false)
  }

  function blurHandler() {
    // 按著 W 切到別的視窗再放開，keyup 送到別的視窗去了，這裡永遠收不到。
    controller.releaseAll()
  }

  return { keydownHandler, keyupHandler, blurHandler }
}
