<script setup lang="ts">
import { onUnmounted, ref } from 'vue'
import {
  ACTION_LABELS,
  displayKey,
  isBindableKey,
  useKeyBindingsStore,
  type BindableAction,
} from '@/stores/keyBindings'

/**
 * 自訂按鍵面板。點某個動作的按鍵 → 進入等待狀態 → 按下想要的鍵就綁上去。
 *
 * ⚠ 這裡是 CLAUDE.md 記載的那個坑的變形：PlanetScene 的移動監聽掛在 window 上，
 * 而它只跳過 INPUT/TEXTAREA/SELECT——本面板用的是 <button>，不在那個白名單裡，
 * 所以「等待按鍵」期間如果不做處理，玩家為了綁定按下的每一個鍵都會同時把角色走掉。
 * 解法是在 **capture 階段**掛 window 監聽並 stopPropagation：事件走到
 * window(capture) → target → window(bubble)，在第一段就攔下來，PlanetScene 那個
 * bubble 階段的監聽根本不會收到。
 */
const keyBindings = useKeyBindingsStore()

const isOpen = ref(false)
const capturingAction = ref<BindableAction | null>(null)
const message = ref<string | null>(null)

let captureHandler: ((event: KeyboardEvent) => void) | null = null

const labelOf = (action: BindableAction) =>
  ACTION_LABELS.find((entry) => entry.action === action)?.label ?? action

function stopCapturing() {
  if (captureHandler) {
    window.removeEventListener('keydown', captureHandler, true)
    captureHandler = null
  }
  capturingAction.value = null
  keyBindings.isCapturing = false
}

function startCapturing(action: BindableAction) {
  if (capturingAction.value === action) {
    stopCapturing()
    return
  }
  stopCapturing()
  capturingAction.value = action
  keyBindings.isCapturing = true
  message.value = null

  captureHandler = (event: KeyboardEvent) => {
    // 不論這個鍵能不能綁，都要擋下來——否則「按了 Shift 想取消」也會傳到底下把玩家走掉。
    event.preventDefault()
    event.stopPropagation()

    if (event.key === 'Escape') {
      stopCapturing()
      return
    }
    if (!isBindableKey(event.key)) {
      message.value = '只能綁單一字元的按鍵（字母、數字、標點）。'
      return
    }

    const swapped = keyBindings.setBinding(action, event.key)
    message.value = swapped
      ? `${displayKey(event.key)} 原本是「${labelOf(swapped)}」，兩個動作的按鍵已互換。`
      : null
    stopCapturing()
  }
  window.addEventListener('keydown', captureHandler, true)
}

function toggle() {
  isOpen.value = !isOpen.value
  if (!isOpen.value) {
    stopCapturing()
    message.value = null
  }
}

function resetAll() {
  stopCapturing()
  keyBindings.resetToDefaults()
  message.value = '已恢復預設按鍵。'
}

// 面板被 v-if 拿掉（例如切到星球總覽視角）時，等待中的 capture 監聽必須跟著移除，
// 否則它會一直吃掉全域按鍵，症狀是「整個鍵盤突然沒反應」。
onUnmounted(stopCapturing)

defineExpose({ toggle })
</script>

<template>
  <div class="key-binding-panel">
    <button type="button" class="opener" title="自訂按鍵" @click="toggle">按鍵設定</button>

    <div v-if="isOpen" class="body">
      <div class="title">自訂按鍵</div>
      <ul class="rows">
        <li v-for="entry in ACTION_LABELS" :key="entry.action">
          <span class="label">{{ entry.label }}</span>
          <button
            type="button"
            class="key"
            :class="{ capturing: capturingAction === entry.action }"
            :title="capturingAction === entry.action ? '按下想要的鍵（Esc 取消）' : '點一下改綁'"
            @click="startCapturing(entry.action)"
          >
            {{
              capturingAction === entry.action
                ? '按鍵…'
                : displayKey(keyBindings.keyFor(entry.action))
            }}
          </button>
        </li>
      </ul>

      <div v-if="message" class="message">{{ message }}</div>
      <div class="hint">按鍵重複時會自動與原本的動作互換，不會有動作變成沒有按鍵。</div>

      <div class="actions">
        <button type="button" @click="resetAll">恢復預設</button>
        <button type="button" @click="toggle">關閉</button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.key-binding-panel {
  pointer-events: auto;
}

.opener {
  padding: 4px 10px;
  font-family: monospace;
  font-size: 12px;
  background: rgba(0, 0, 0, 0.5);
  color: #ccc;
  border: 1px solid rgba(255, 255, 255, 0.25);
  border-radius: 4px;
  cursor: pointer;
}

.opener:hover {
  border-color: rgba(255, 255, 255, 0.6);
  color: #fff;
}

.body {
  margin-top: 6px;
  padding: 8px 10px;
  background: rgba(0, 0, 0, 0.72);
  border: 1px solid rgba(255, 255, 255, 0.2);
  border-radius: 4px;
  font-size: 12px;
  color: #ddd;
}

.title {
  font-weight: bold;
  margin-bottom: 6px;
  color: #fff;
}

.rows {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  gap: 3px;
}

.rows li {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
}

.label {
  opacity: 0.85;
}

.key {
  min-width: 44px;
  padding: 2px 6px;
  font-family: monospace;
  font-size: 12px;
  background: rgba(255, 255, 255, 0.08);
  color: #fff;
  border: 1px solid rgba(255, 255, 255, 0.3);
  border-radius: 3px;
  cursor: pointer;
}

.key:hover {
  border-color: rgba(255, 255, 255, 0.7);
}

.key.capturing {
  border-color: #ffd24a;
  color: #ffd24a;
}

.message {
  margin-top: 6px;
  color: #ffd24a;
  line-height: 1.4;
}

.hint {
  margin-top: 6px;
  opacity: 0.6;
  font-size: 11px;
  line-height: 1.4;
}

.actions {
  margin-top: 8px;
  display: flex;
  gap: 6px;
}

.actions button {
  flex: 1;
  padding: 3px 8px;
  font-family: monospace;
  font-size: 12px;
  background: rgba(255, 255, 255, 0.08);
  color: #ddd;
  border: 1px solid rgba(255, 255, 255, 0.25);
  border-radius: 3px;
  cursor: pointer;
}

.actions button:hover {
  border-color: rgba(255, 255, 255, 0.6);
  color: #fff;
}
</style>
