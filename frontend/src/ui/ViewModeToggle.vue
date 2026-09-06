<script setup lang="ts">
import { useGameStateStore, VIEW_MODE_LABELS, type ViewMode } from '@/stores/gameState'

const gameState = useGameStateStore()

/**
 * 三個視角全部列出來，而不是用一顆「切換到下一個」的按鈕循環。
 *
 * 循環式的按鈕在只有兩個模式時還好，第三個模式加進來之後就變成「看不到」——
 * 從走動視角只會顯示「切換到 星球總覽」，使用者根本不知道還有第三個模式存在（2026-09-06 回報）。
 */
const modes: ViewMode[] = ['walk', 'overview', 'evolution']
</script>

<template>
  <div class="view-mode-toggle">
    <button
      v-for="mode in modes"
      :key="mode"
      type="button"
      :class="{ active: gameState.viewMode === mode }"
      @click="gameState.setViewMode(mode)"
    >
      {{ VIEW_MODE_LABELS[mode] }}
    </button>
  </div>
</template>

<style scoped>
.view-mode-toggle {
  position: fixed;
  top: 12px;
  left: 50%;
  transform: translateX(-50%);
  display: flex;
  gap: 4px;
}

button {
  padding: 6px 12px;
  font-family: monospace;
  font-size: 12px;
  background: rgba(0, 0, 0, 0.5);
  color: #ccc;
  border: 1px solid rgba(255, 255, 255, 0.2);
  border-radius: 4px;
  cursor: pointer;
}

button:hover {
  border-color: rgba(255, 255, 255, 0.5);
  color: #fff;
}

button.active {
  background: rgba(60, 140, 220, 0.85);
  color: #fff;
  border-color: rgba(255, 255, 255, 0.6);
}
</style>
