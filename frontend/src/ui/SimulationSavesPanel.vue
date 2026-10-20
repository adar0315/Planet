<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useSimulationStore } from '@/stores/simulation'
import type { SnapshotInfo, SnapshotKind } from '@/network/apiClient'

/**
 * 存檔管理（v2 規劃 P9）：列表、手動另存、載入（從檢查點分岔）、刪除、清碎島並放大成走動地圖。
 *
 * 分岔＝載入一個檢查點 → 用新名字另存 → 繼續跑；之後自動存的檢查點帶著新的一輪標記，不會蓋掉原本那條時間線的。
 */
const simulation = useSimulationStore()

const newName = ref('')
const nameError = computed(() =>
  newName.value === '' || /^[A-Za-z0-9_-]{1,64}$/.test(newName.value)
    ? null
    : '只能用英數、底線、連字號，最多 64 個字',
)

/** 等使用者確認放大的那個存檔；確認區出現時才要金鑰。 */
const pendingMaterialize = ref<string | null>(null)
const adminKey = ref('')

const KIND_LABEL: Record<SnapshotKind, string> = {
  Manual: '手動',
  Checkpoint: '檢查點',
  StopCondition: '停止條件',
}

function kindLabel(save: SnapshotInfo) {
  return save.kind ? KIND_LABEL[save.kind] : '舊存檔'
}

function ageLabel(save: SnapshotInfo) {
  if (save.elapsedYears === null) return '—'
  return `${(save.elapsedYears / 1e6).toFixed(1)} Myr`
}

async function saveCurrent() {
  if (newName.value === '' || nameError.value) return
  if (simulation.saves.some((s) => s.name === newName.value) && !window.confirm(`「${newName.value}」已經存在，要覆蓋嗎？`)) {
    return
  }
  await simulation.saveAs(newName.value)
  newName.value = ''
}

async function load(save: SnapshotInfo) {
  if (!window.confirm(`載入「${save.name}」（${ageLabel(save)}）？目前的模擬沒存檔的部分會不見。`)) return
  await simulation.load(save.name)
}

async function remove(save: SnapshotInfo) {
  if (!window.confirm(`刪除存檔「${save.name}」？刪掉就救不回來。`)) return
  await simulation.remove(save.name)
}

async function confirmMaterialize() {
  if (pendingMaterialize.value === null || adminKey.value === '') return
  const name = pendingMaterialize.value
  const key = adminKey.value
  pendingMaterialize.value = null
  adminKey.value = ''
  await simulation.materialize(name, key)
}

const progress = computed(() => {
  const m = simulation.materialization
  if (!m) return null
  if (m.isRunning) {
    const pct = m.totalTiles > 0 ? ((m.tilesWritten / m.totalTiles) * 100).toFixed(1) : '0'
    return `放大「${m.snapshotName}」：${m.phase ?? ''}　${pct}%`
  }
  if (m.error) return null
  if (m.phase === '完成') {
    return `「${m.snapshotName}」已放大完成（${Math.round(m.elapsedSeconds)} 秒、清掉 ${m.sunkIslandCells} 格碎島），切到走動模式就能走`
  }
  return null
})

onMounted(() => simulation.refreshSaves())
</script>

<template>
  <div class="saves">
    <div class="header">
      <span>存檔</span>
      <button type="button" :disabled="simulation.isBusy" @click="simulation.refreshSaves()">重新整理</button>
    </div>

    <form class="save-row" @submit.prevent="saveCurrent">
      <input v-model="newName" placeholder="新存檔名稱（英數、_、-）" maxlength="64" />
      <button type="submit" :disabled="simulation.isBusy || newName === '' || nameError !== null">另存</button>
    </form>
    <p v-if="nameError" class="warn">{{ nameError }}</p>

    <ul class="list">
      <li v-for="save in simulation.saves" :key="save.name" :class="{ incompatible: !save.compatible }">
        <div class="name" :title="save.name">{{ save.name }}</div>
        <div class="meta">
          <span class="kind" :class="save.kind ?? 'old'">{{ kindLabel(save) }}</span>
          <span>{{ ageLabel(save) }}</span>
          <span>{{ (save.sizeBytes / 1e6).toFixed(0) }} MB</span>
        </div>
        <div v-if="save.compatible" class="actions">
          <button type="button" :disabled="simulation.isBusy" @click="load(save)">載入</button>
          <button type="button" :disabled="simulation.materialization?.isRunning" @click="pendingMaterialize = save.name">
            放大成走動地圖
          </button>
          <button type="button" class="danger" :disabled="simulation.isBusy" @click="remove(save)">刪除</button>
        </div>
        <div v-else class="actions">
          <span class="warn">舊版格式，讀不回來</span>
          <button type="button" class="danger" :disabled="simulation.isBusy" @click="remove(save)">刪除</button>
        </div>
      </li>
      <li v-if="simulation.saves.length === 0" class="empty">還沒有存檔</li>
    </ul>

    <form v-if="pendingMaterialize" class="confirm" @submit.prevent="confirmMaterialize">
      <p>
        把「{{ pendingMaterialize }}」清碎島、放大成 2,000 萬個地塊，<strong>取代目前走動模式的整顆星球</strong>（約 10 分鐘）。
        寫完之前舊地圖照常可以走。
      </p>
      <input v-model="adminKey" type="password" placeholder="管理金鑰" autocomplete="off" />
      <div class="actions">
        <button type="submit" class="danger" :disabled="adminKey === ''">確定放大</button>
        <button type="button" @click="pendingMaterialize = null; adminKey = ''">取消</button>
      </div>
    </form>

    <p v-if="progress" class="progress">{{ progress }}</p>
    <p v-if="simulation.materializeError" class="error">{{ simulation.materializeError }}</p>
  </div>
</template>

<style scoped>
.saves {
  margin-top: 10px;
  padding-top: 8px;
  border-top: 1px solid rgba(255, 255, 255, 0.15);
}

.header,
.save-row,
.actions,
.meta {
  display: flex;
  align-items: center;
  gap: 6px;
}

.header {
  justify-content: space-between;
  margin-bottom: 6px;
}

input {
  flex: 1;
  min-width: 0;
  padding: 6px 8px;
  font-family: monospace;
  font-size: 12px;
  background: rgba(0, 0, 0, 0.55);
  color: #eee;
  border: 1px solid rgba(255, 255, 255, 0.25);
  border-radius: 4px;
}

button {
  padding: 4px 8px;
  font-family: monospace;
  font-size: 12px;
  background: rgba(0, 0, 0, 0.55);
  color: #ccc;
  border: 1px solid rgba(255, 255, 255, 0.2);
  border-radius: 4px;
  cursor: pointer;
}

button:hover:not(:disabled) {
  border-color: rgba(255, 255, 255, 0.5);
  color: #fff;
}

button:disabled {
  opacity: 0.4;
  cursor: default;
}

button.danger:hover:not(:disabled) {
  border-color: rgba(220, 80, 80, 0.8);
  color: #ffb0b0;
}

.list {
  list-style: none;
  margin: 8px 0 0;
  padding: 0;
  max-height: 260px;
  overflow-y: auto;
}

.list li {
  padding: 6px 0;
  border-bottom: 1px solid rgba(255, 255, 255, 0.08);
}

.list li.incompatible .name {
  color: #777;
}

.name {
  color: #fff;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.meta {
  color: #8a8a8a;
  margin: 2px 0 4px;
}

.kind {
  padding: 0 4px;
  border-radius: 3px;
  background: rgba(255, 255, 255, 0.1);
}

.kind.Checkpoint {
  color: #8ab4ff;
}

.kind.StopCondition {
  color: #7fe0a0;
}

.empty {
  color: #8a8a8a;
}

.confirm {
  margin-top: 8px;
  padding: 8px;
  background: rgba(120, 40, 20, 0.6);
  border-radius: 4px;
}

.confirm p {
  margin: 0 0 6px;
  color: #fff;
}

.confirm input {
  width: 100%;
  box-sizing: border-box;
  margin-bottom: 6px;
}

.warn {
  margin: 4px 0 0;
  color: #f0c060;
}

.progress,
.error {
  margin: 8px 0 0;
  padding: 6px 8px;
  border-radius: 4px;
  color: #fff;
}

.progress {
  background: rgba(20, 80, 140, 0.85);
}

.error {
  background: rgba(140, 20, 20, 0.85);
}
</style>
