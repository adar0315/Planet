<script setup lang="ts">
import { ref } from 'vue'
import { useGameStateStore, type GameMode } from '@/stores/gameState'

const gameState = useGameStateStore()

const options: { mode: GameMode; label: string }[] = [
  { mode: 'observe', label: '① 時間靜止' },
  { mode: 'stepped', label: '② 移動模式' },
  { mode: 'realtime', label: '③ 即時模式' },
]

/** 右上角功能列預設收合，避免長期佔畫面空間；點選單鈕（≡/×）展開/收合。 */
const isExpanded = ref(false)
</script>

<template>
  <div class="mode-switcher">
    <button type="button" class="toggle" :title="isExpanded ? '收合功能列' : '展開功能列'" @click="isExpanded = !isExpanded">
      {{ isExpanded ? '×' : '☰' }}
    </button>
    <template v-if="isExpanded">
      <button
        v-for="option in options"
        :key="option.mode"
        type="button"
        :class="{ active: gameState.mode === option.mode }"
        @click="gameState.setMode(option.mode)"
      >
        {{ option.label }}
      </button>
    </template>
  </div>
</template>

<style scoped>
.mode-switcher {
  position: fixed;
  top: 12px;
  right: 12px;
  display: flex;
  gap: 6px;
}

button {
  padding: 6px 10px;
  font-family: monospace;
  font-size: 12px;
  background: rgba(0, 0, 0, 0.5);
  color: #ccc;
  border: 1px solid rgba(255, 255, 255, 0.2);
  border-radius: 4px;
  cursor: pointer;
}

button.toggle {
  font-size: 14px;
  line-height: 1;
}

button.active {
  background: rgba(60, 140, 220, 0.85);
  color: #fff;
  border-color: rgba(255, 255, 255, 0.6);
}

button:hover {
  border-color: rgba(255, 255, 255, 0.5);
}
</style>
