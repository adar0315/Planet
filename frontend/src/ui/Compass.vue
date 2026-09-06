<script setup lang="ts">
import { computed } from 'vue'
import { useGameStateStore } from '@/stores/gameState'

const gameState = useGameStateStore()

// 指針要指向「正北在畫面上的方向」，所以順時針轉跟compassAngleDeg同方向即可。
const needleStyle = computed(() => ({ transform: `rotate(${gameState.compassAngleDeg}deg)` }))
</script>

<template>
  <div class="compass" title="點擊復位視角（畫面上方=正北）" @click="gameState.requestResetView()">
    <svg viewBox="0 0 100 100" width="64" height="64">
      <circle cx="50" cy="50" r="46" class="dial" />
      <text x="50" y="16" class="tick-label">N</text>
      <text x="50" y="90" class="tick-label minor">S</text>
      <text x="12" y="54" class="tick-label minor">W</text>
      <text x="88" y="54" class="tick-label minor">E</text>
      <g class="needle" :style="needleStyle">
        <polygon points="50,14 42,54 50,46 58,54" class="needle-north" />
        <polygon points="50,86 42,54 50,62 58,54" class="needle-south" />
      </g>
      <circle cx="50" cy="50" r="3" class="pivot" />
    </svg>
  </div>
</template>

<style scoped>
.compass {
  position: fixed;
  bottom: 12px;
  left: 12px;
  padding: 6px;
  background: rgba(0, 0, 0, 0.5);
  border-radius: 50%;
  cursor: pointer;
}

.compass:hover {
  background: rgba(30, 30, 30, 0.7);
}

.dial {
  fill: rgba(255, 255, 255, 0.04);
  stroke: rgba(255, 255, 255, 0.35);
  stroke-width: 1.5;
}

.tick-label {
  fill: #ccc;
  font-family: monospace;
  font-size: 13px;
  text-anchor: middle;
}

.tick-label.minor {
  fill: #888;
  font-size: 10px;
}

.needle {
  transform-origin: 50px 50px;
}

.needle-north {
  fill: #e04b4b;
}

.needle-south {
  fill: #cfd3d8;
}

.pivot {
  fill: #eee;
}
</style>
