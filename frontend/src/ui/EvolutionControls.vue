<script setup lang="ts">
import { onUnmounted, ref, watch } from 'vue'
import { SIMULATION_FIELDS, useSimulationStore } from '@/stores/simulation'

const simulation = useSimulationStore()

/** 播放迴圈的計時器。用 setTimeout 串接而不是 setInterval——見下面的說明。 */
let timer: ReturnType<typeof setTimeout> | null = null

/**
 * 播放：跑完一次才排下一次，**不能用 setInterval**。
 *
 * 後端的模擬是 Singleton 而且整包在同一把鎖裡，一步在正式解析度上要幾十毫秒到幾百毫秒。
 * 用固定間隔的 setInterval 的話，請求會比處理速度快、在鎖上愈排愈長，
 * 畫面看起來就像整個當掉（而且按暫停也停不下來，因為佇列裡還有一堆）。
 */
function scheduleTick() {
  timer = setTimeout(async () => {
    if (!simulation.isPlaying) return
    await simulation.advance()
    if (simulation.isPlaying) scheduleTick()
  }, 0)
}

watch(
  () => simulation.isPlaying,
  (playing) => {
    if (playing) {
      scheduleTick()
      return
    }
    if (timer !== null) clearTimeout(timer)
    timer = null
  },
)

onUnmounted(() => {
  if (timer !== null) clearTimeout(timer)
  simulation.isPlaying = false
})

const isPanelOpen = ref(true)
</script>

<template>
  <div class="evolution-controls">
    <div class="bar">
      <button type="button" class="collapse" @click="isPanelOpen = !isPanelOpen">
        {{ isPanelOpen ? '×' : '☰' }}
      </button>

      <span class="elapsed">{{ simulation.elapsedLabel }}</span>

      <button type="button" :class="{ active: simulation.isPlaying }" @click="simulation.togglePlay()">
        {{ simulation.isPlaying ? '⏸ 暫停' : '▶ 播放' }}
      </button>
      <button type="button" :disabled="simulation.isPlaying" @click="simulation.advance()">
        單步 +{{ simulation.stepsPerTick }}
      </button>
      <button type="button" @click="simulation.cycleSpeed()">
        速度 ×{{ simulation.stepsPerTick }}
      </button>
      <button type="button" class="danger" @click="simulation.reset()">重設</button>

      <span v-if="simulation.isBusy" class="busy">計算中…</span>
    </div>

    <div v-if="isPanelOpen" class="panel">
      <label class="field-row">
        <span>顯示欄位</span>
        <select :value="simulation.field" @change="simulation.setField(($event.target as HTMLSelectElement).value as never)">
          <option v-for="option in SIMULATION_FIELDS" :key="option.field" :value="option.field">
            {{ option.label }}
          </option>
        </select>
      </label>
      <p class="hint">
        {{ SIMULATION_FIELDS.find((o) => o.field === simulation.field)?.hint }}
      </p>

      <dl v-if="simulation.status" class="stats">
        <dt>陸地比例</dt>
        <dd>{{ (simulation.status.landFraction * 100).toFixed(1) }}%</dd>
        <dt>海面比例</dt>
        <dd>{{ ((1 - simulation.status.landFraction) * 100).toFixed(1) }}%</dd>
        <dt>板塊／熱柱</dt>
        <dd>{{ simulation.status.plateCount }} ／ {{ simulation.status.plumeCount }}</dd>
        <dt>格數</dt>
        <dd>{{ simulation.status.cellCount.toLocaleString() }}</dd>
        <dt>每步年數</dt>
        <dd>{{ simulation.status.yearsPerStep.toLocaleString() }}</dd>
      </dl>

      <dl v-if="simulation.status?.continents" class="stats">
        <dt>最大兩塊陸地</dt>
        <dd>
          {{ (simulation.status.continents.largestShare * 100).toFixed(1) }}% ＋
          {{ (simulation.status.continents.secondShare * 100).toFixed(1) }}%
        </dd>
        <dt>大島</dt>
        <dd>{{ simulation.status.continents.bigIslandCount }} 個</dd>
        <dt>海岸破碎度</dt>
        <dd>
          {{ simulation.status.continents.largestRaggedness.toFixed(1) }} ／
          {{ simulation.status.continents.secondRaggedness.toFixed(1) }}
        </dd>
        <dt>停止條件</dt>
        <dd>{{ simulation.status.continents.isMet ? '✓ 成立' : '尚未成立' }}</dd>
      </dl>
      <p class="hint">
        停止條件：兩塊各 ≥20%、合計 ≥75%，大島（1~10%）≥ 2 個，兩塊的海岸破碎度各 ≤4（1 ＝ 圓盤）
      </p>
    </div>

    <p v-if="simulation.stopNotice" class="notice">{{ simulation.stopNotice }}</p>
    <p v-if="simulation.error" class="error">{{ simulation.error }}</p>
  </div>
</template>

<style scoped>
.evolution-controls {
  position: fixed;
  bottom: 12px;
  left: 12px;
  display: flex;
  flex-direction: column;
  gap: 6px;
  font-family: monospace;
  font-size: 12px;
  color: #ccc;
}

.bar {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

button,
select {
  padding: 6px 10px;
  font-family: monospace;
  font-size: 12px;
  background: rgba(0, 0, 0, 0.55);
  color: #ccc;
  border: 1px solid rgba(255, 255, 255, 0.2);
  border-radius: 4px;
  cursor: pointer;
}

button:hover:not(:disabled),
select:hover {
  border-color: rgba(255, 255, 255, 0.5);
  color: #fff;
}

button:disabled {
  opacity: 0.4;
  cursor: default;
}

button.active {
  background: rgba(60, 140, 220, 0.85);
  color: #fff;
  border-color: rgba(255, 255, 255, 0.6);
}

button.danger:hover {
  border-color: rgba(220, 80, 80, 0.8);
  color: #ffb0b0;
}

button.collapse {
  font-size: 14px;
  line-height: 1;
  padding: 6px 9px;
}

.elapsed {
  padding: 6px 10px;
  background: rgba(0, 0, 0, 0.55);
  border: 1px solid rgba(255, 255, 255, 0.2);
  border-radius: 4px;
  color: #fff;
  min-width: 96px;
  text-align: center;
}

.busy {
  color: #8ab4ff;
}

.panel {
  padding: 8px 10px;
  background: rgba(0, 0, 0, 0.55);
  border: 1px solid rgba(255, 255, 255, 0.2);
  border-radius: 4px;
  max-width: 320px;
}

.field-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.hint {
  margin: 6px 0 8px;
  color: #8a8a8a;
}

.stats {
  display: grid;
  grid-template-columns: auto auto;
  gap: 2px 12px;
  margin: 0;
}

.stats dt {
  color: #8a8a8a;
}

.stats dd {
  margin: 0;
  text-align: right;
}

.error,
.notice {
  margin: 0;
  padding: 8px 12px;
  color: #fff;
  border-radius: 4px;
  max-width: 320px;
}

.error {
  background: rgba(140, 20, 20, 0.85);
}

.notice {
  background: rgba(20, 110, 60, 0.9);
}
</style>
