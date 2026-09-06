<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { fetchSeedingStatus } from '@/network/apiClient'
import { usePlanetInfoStore } from '@/stores/planetInfo'

/**
 * 服務首次啟動、資料庫是空的時候，後端會在背景生成整個星球（2000萬格，實測約15分鐘）並即時更新進度，
 * 這段期間輪詢 /api/status 顯示進度，避免玩家看到一堆連線失敗的錯誤訊息、搞不清楚是不是壞掉了。
 * 一般情況（資料庫已經有資料）這個畫面幾乎不會被看到——第一次輪詢就會拿到 isReady=true，直接顯示內容。
 */
const POLL_INTERVAL_MS = 1000

const planetInfo = usePlanetInfoStore()

const isReady = ref(false)
const isSeeding = ref(false)
const tilesWritten = ref(0)
const totalTiles = ref(0)
const errorMessage = ref<string | null>(null)
const isConnecting = ref(true)

const percent = computed(() => (totalTiles.value > 0 ? Math.round((tilesWritten.value / totalTiles.value) * 100) : 0))

let pollHandle: ReturnType<typeof setTimeout> | null = null
let stopped = false

async function poll() {
  try {
    const status = await fetchSeedingStatus()
    isConnecting.value = false

    if (status.errorMessage) {
      errorMessage.value = status.errorMessage
      return // 生成失敗，不繼續輪詢，畫面停在錯誤訊息上
    }

    isSeeding.value = status.isSeeding
    tilesWritten.value = status.tilesWritten
    totalTiles.value = status.totalTiles

    if (status.isReady) {
      // 場景元件掛載時會同步讀取星球尺寸，所以要在放行之前先載好，
      // 否則它們只能退回寫死的常數——那正是這次要消除的問題。
      await planetInfo.load()
      isReady.value = true
      return
    }
  } catch {
    // 連不上：伺服器可能還在極短暫的啟動窗口內，或還沒啟動，先當作「還在連線中」繼續重試。
    isConnecting.value = true
  }

  if (!stopped) pollHandle = setTimeout(poll, POLL_INTERVAL_MS)
}

onMounted(poll)
onUnmounted(() => {
  stopped = true
  if (pollHandle) clearTimeout(pollHandle)
})
</script>

<template>
  <slot v-if="isReady" />
  <div v-else class="loading-gate">
    <div v-if="errorMessage" class="panel error">
      <div>星球生成失敗</div>
      <div class="detail">{{ errorMessage }}</div>
    </div>
    <div v-else-if="isSeeding" class="panel">
      <div>正在生成星球地形，第一次啟動需要重新生成整顆星球（約15分鐘），請稍候…</div>
      <div class="progress-track">
        <div class="progress-fill" :style="{ width: `${percent}%` }"></div>
      </div>
      <div class="detail">{{ tilesWritten.toLocaleString() }} / {{ totalTiles.toLocaleString() }} 格（{{ percent }}%）</div>
    </div>
    <div v-else class="panel">
      <div>{{ isConnecting ? '連線中…' : '載入中…' }}</div>
    </div>
  </div>
</template>

<style scoped>
.loading-gate {
  position: fixed;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  background: #05070d;
  color: #ccc;
  font-family: monospace;
}

.panel {
  width: 360px;
  text-align: center;
  line-height: 1.6;
}

.panel.error {
  color: #e04b4b;
}

.detail {
  margin-top: 8px;
  font-size: 12px;
  opacity: 0.8;
}

.progress-track {
  margin-top: 14px;
  height: 8px;
  border-radius: 4px;
  background: rgba(255, 255, 255, 0.12);
  overflow: hidden;
}

.progress-fill {
  height: 100%;
  background: rgba(60, 140, 220, 0.85);
  transition: width 0.3s ease;
}
</style>
