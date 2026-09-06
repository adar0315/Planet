<script setup lang="ts">
import { computed, onUnmounted, reactive, ref } from 'vue'
import { fetchSeedingStatus, fetchTerrainOptions, triggerRegeneration, updateTerrainOptions } from '@/network/apiClient'
import type { TerrainGenerationOptionsDto } from '@/types/terrainOptions'

/**
 * 開發用的地形生成參數管理面板（右下角，預設收合）。對應後端 RegenerationController 的
 * GET/PUT /api/admin/terrain-options ＋ POST /api/admin/regenerate。「重新生成」會先PUT目前表單內容
 * 再觸發生成，確保套用的一定是畫面上看到的參數，不會有「改了忘記存」的落差；因為會truncate掉現有
 * 2,000萬格資料、無法復原，所以按鈕做成二段式確認，不能誤觸就直接清空地圖。
 */
const POLL_INTERVAL_MS = 1000

/**
 * 管理金鑰存在 sessionStorage 而不是 localStorage：關掉分頁就失效。
 * 這是清空整顆星球用的金鑰，沒必要長期留在瀏覽器裡。
 */
const ADMIN_KEY_STORAGE = 'planet.adminKey'

const isExpanded = ref(false)
const isLoaded = ref(false)
const loadError = ref<string | null>(null)

const adminKey = ref(sessionStorage.getItem(ADMIN_KEY_STORAGE) ?? '')

function rememberKey() {
  try {
    sessionStorage.setItem(ADMIN_KEY_STORAGE, adminKey.value)
  } catch {
    // 無痕模式等情況存不進去就算了，只是每次要重打，不影響功能
  }
}

const form = reactive<TerrainGenerationOptionsDto>({
  seed: 42,
  continentALatitudeStretch: 1,
  continentALongitudeStretch: 1,
  continentBLatitudeStretch: 1,
  continentBLongitudeStretch: 0.5,
  islandCount: 250,
  islandClusterRatio: 0.6,
  islandVolcanicProbability: 0.7,
})

const confirmingRegenerate = ref(false)
const isSubmitting = ref(false)
const submitError = ref<string | null>(null)

const isSeeding = ref(false)
const tilesWritten = ref(0)
const totalTiles = ref(0)
const percent = computed(() => (totalTiles.value > 0 ? Math.round((tilesWritten.value / totalTiles.value) * 100) : 0))

let pollHandle: ReturnType<typeof setTimeout> | null = null
let stopped = false

async function loadOptions() {
  if (!adminKey.value) {
    loadError.value = '請先輸入管理金鑰。'
    return
  }
  loadError.value = null
  try {
    Object.assign(form, await fetchTerrainOptions(adminKey.value))
    rememberKey()
    isLoaded.value = true
  } catch (err) {
    loadError.value = err instanceof Error ? err.message : String(err)
    isLoaded.value = false
  }
}

async function toggle() {
  isExpanded.value = !isExpanded.value
  if (!isExpanded.value) return
  if (!isLoaded.value && adminKey.value) await loadOptions()
  startPolling() // 展開時順便查一次目前狀態，避免「別的地方已經在重新生成」卻沒發現
}

function requestRegenerate() {
  if (!confirmingRegenerate.value) {
    confirmingRegenerate.value = true
    return
  }
  confirmingRegenerate.value = false
  void submitRegenerate()
}

function cancelRegenerate() {
  confirmingRegenerate.value = false
}

async function submitRegenerate() {
  isSubmitting.value = true
  submitError.value = null
  try {
    await updateTerrainOptions(form, adminKey.value)
    await triggerRegeneration(adminKey.value)
    isSeeding.value = true
    startPolling()
  } catch (err) {
    submitError.value = err instanceof Error ? err.message : String(err)
  } finally {
    isSubmitting.value = false
  }
}

function startPolling() {
  if (pollHandle) clearTimeout(pollHandle)
  void poll()
}

async function poll() {
  try {
    const status = await fetchSeedingStatus()
    isSeeding.value = status.isSeeding
    tilesWritten.value = status.tilesWritten
    totalTiles.value = status.totalTiles
    if (status.errorMessage) {
      submitError.value = status.errorMessage
      isSeeding.value = false
      return // 生成失敗，不繼續輪詢
    }
  } catch {
    // 短暫連不上就繼續嘗試，不中斷輪詢（服務可能正在重新生成中途重啟等）
  }
  if (!stopped && isSeeding.value) pollHandle = setTimeout(poll, POLL_INTERVAL_MS)
}

onUnmounted(() => {
  stopped = true
  if (pollHandle) clearTimeout(pollHandle)
})
</script>

<template>
  <div class="admin-panel">
    <button type="button" class="toggle" :title="isExpanded ? '收合管理面板' : '展開管理面板（地形生成參數）'" @click="toggle">
      {{ isExpanded ? '×' : '⚙' }}
    </button>
    <div v-if="isExpanded" class="body">
      <div class="title">星球生成參數</div>

      <label class="key-row">
        管理金鑰
        <input type="password" v-model="adminKey" placeholder="X-Admin-Key" @keyup.enter="loadOptions" />
      </label>
      <button v-if="!isLoaded" type="button" @click="loadOptions">載入參數</button>

      <div v-if="loadError" class="error">{{ loadError }}</div>
      <template v-if="isLoaded">
        <label>種子 Seed<input type="number" v-model.number="form.seed" step="1" /></label>

        <fieldset>
          <legend>大陸 A</legend>
          <label>緯度伸展<input type="number" v-model.number="form.continentALatitudeStretch" min="0.1" max="3" step="0.05" /></label>
          <label>經度伸展<input type="number" v-model.number="form.continentALongitudeStretch" min="0.1" max="3" step="0.05" /></label>
        </fieldset>
        <fieldset>
          <legend>大陸 B</legend>
          <label>緯度伸展<input type="number" v-model.number="form.continentBLatitudeStretch" min="0.1" max="3" step="0.05" /></label>
          <label>經度伸展<input type="number" v-model.number="form.continentBLongitudeStretch" min="0.1" max="3" step="0.05" /></label>
        </fieldset>
        <div class="hint">⚠ 緯度伸展過大可能導致兩大陸融合成一塊，建議優先調整經度伸展</div>

        <label>小島數量<input type="number" v-model.number="form.islandCount" min="0" max="5000" step="10" /></label>
        <label>小島聚集比例<input type="number" v-model.number="form.islandClusterRatio" min="0" max="1" step="0.05" /></label>
        <label>小島火山機率<input type="number" v-model.number="form.islandVolcanicProbability" min="0" max="1" step="0.05" /></label>

        <div v-if="isSeeding" class="progress-block">
          <div class="progress-track">
            <div class="progress-fill" :style="{ width: `${percent}%` }"></div>
          </div>
          <div class="detail">正在重新生成…{{ tilesWritten.toLocaleString() }} / {{ totalTiles.toLocaleString() }}（{{ percent }}%）</div>
        </div>
        <div v-else-if="submitError" class="error">{{ submitError }}</div>

        <div class="actions">
          <template v-if="confirmingRegenerate">
            <span class="confirm-text">確定要清空目前地圖並重新生成嗎？無法復原！</span>
            <button type="button" class="danger" @click="requestRegenerate">確定重新生成</button>
            <button type="button" @click="cancelRegenerate">取消</button>
          </template>
          <button v-else type="button" class="danger" :disabled="isSubmitting || isSeeding" @click="requestRegenerate">
            套用參數並重新生成星球
          </button>
        </div>
      </template>
    </div>
  </div>
</template>

<style scoped>
.admin-panel {
  /* 疊在ModeSwitcher（top:12px/right:12px）正下方——它收合/展開時都只有一行按鈕高度不變，
     不會跟著往下長，所以固定top:56px不會被蓋到。 */
  position: fixed;
  top: 56px;
  right: 12px;
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 6px;
  font-family: monospace;
  font-size: 12px;
}

.toggle {
  padding: 6px 10px;
  font-size: 14px;
  line-height: 1;
  background: rgba(0, 0, 0, 0.5);
  color: #ccc;
  border: 1px solid rgba(255, 255, 255, 0.2);
  border-radius: 4px;
  cursor: pointer;
}

.toggle:hover {
  border-color: rgba(255, 255, 255, 0.5);
}

.body {
  width: 260px;
  padding: 10px 12px;
  background: rgba(0, 0, 0, 0.75);
  color: #ccc;
  border: 1px solid rgba(255, 255, 255, 0.2);
  border-radius: 4px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.title {
  font-weight: bold;
  color: #fff;
}

fieldset {
  border: 1px solid rgba(255, 255, 255, 0.15);
  border-radius: 4px;
  padding: 6px 8px;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

legend {
  padding: 0 4px;
  font-size: 11px;
  opacity: 0.8;
}

label {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 8px;
}

.key-row input[type='password'] {
  width: 120px;
  background: rgba(255, 255, 255, 0.08);
  color: #fff;
  border: 1px solid rgba(255, 255, 255, 0.25);
  border-radius: 3px;
  padding: 2px 4px;
  font-family: monospace;
}

input[type='number'] {
  width: 80px;
  background: rgba(255, 255, 255, 0.08);
  color: #fff;
  border: 1px solid rgba(255, 255, 255, 0.25);
  border-radius: 3px;
  padding: 2px 4px;
  font-family: monospace;
}

.hint {
  font-size: 11px;
  color: #e0b64b;
  opacity: 0.9;
  line-height: 1.4;
}

.error {
  color: #e04b4b;
  font-size: 11px;
  line-height: 1.4;
}

.actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  margin-top: 4px;
}

.confirm-text {
  font-size: 11px;
  color: #e04b4b;
  flex-basis: 100%;
}

button {
  padding: 5px 8px;
  font-family: monospace;
  font-size: 11px;
  background: rgba(0, 0, 0, 0.5);
  color: #ccc;
  border: 1px solid rgba(255, 255, 255, 0.2);
  border-radius: 4px;
  cursor: pointer;
}

button:hover {
  border-color: rgba(255, 255, 255, 0.5);
}

button.danger {
  background: rgba(200, 60, 60, 0.5);
}

button.danger:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.progress-block {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.progress-track {
  height: 6px;
  border-radius: 3px;
  background: rgba(255, 255, 255, 0.12);
  overflow: hidden;
}

.progress-fill {
  height: 100%;
  background: rgba(60, 140, 220, 0.85);
  transition: width 0.3s ease;
}

.detail {
  font-size: 11px;
  opacity: 0.8;
}
</style>
