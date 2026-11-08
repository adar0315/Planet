<script setup lang="ts">
import { computed, ref } from 'vue'
import { useGameStateStore, type GameMode } from '@/stores/gameState'
import { REALTIME_HOURS_PER_SECOND_OPTIONS, STEPPED_HOURS_PER_TILE_OPTIONS } from '@/map/timeRates'

const gameState = useGameStateStore()

/** 時間流逝的速度：移動模式選每格多久、即時模式選每秒多久；時間靜止時不顯示。 */
const rateOptions = computed(() =>
  gameState.mode === 'stepped'
    ? STEPPED_HOURS_PER_TILE_OPTIONS
    : gameState.mode === 'realtime'
      ? REALTIME_HOURS_PER_SECOND_OPTIONS
      : null,
)
const currentRate = computed(() =>
  gameState.mode === 'stepped' ? gameState.steppedHoursPerTile : gameState.realtimeHoursPerSecond,
)

function onRateChange(event: Event) {
  const hours = Number((event.target as HTMLSelectElement).value)
  if (gameState.mode === 'stepped') gameState.setSteppedHoursPerTile(hours)
  else if (gameState.mode === 'realtime') gameState.setRealtimeHoursPerSecond(hours)
}

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
      <select v-if="rateOptions" :value="currentRate" title="時間流逝的速度" @change="onRateChange">
        <option v-for="option in rateOptions" :key="option.hours" :value="option.hours">{{ option.label }}</option>
      </select>
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

select {
  padding: 5px 6px;
  font-family: monospace;
  font-size: 12px;
  background: rgba(0, 0, 0, 0.6);
  color: #fff;
  border: 1px solid rgba(255, 255, 255, 0.3);
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
