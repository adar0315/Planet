<script setup lang="ts">
import { computed } from 'vue'
import { FACING_LABELS, FACING_STEP_DEG, useGameStateStore } from '@/stores/gameState'
import { useKeyBindingsStore, displayKey } from '@/stores/keyBindings'
import KeyBindingPanel from './KeyBindingPanel.vue'
import { useTerrainStyleStore } from '@/stores/terrainStyles'
import { ELEVATION_EXAGGERATION, METERS_PER_ELEVATION_LEVEL } from '@/map/tileMeshBuilder'

const gameState = useGameStateStore()
const keyBindings = useKeyBindingsStore()
const terrainStyleStore = useTerrainStyleStore()
void terrainStyleStore.load()

const modeLabel: Record<string, string> = {
  observe: '時間靜止（自由觀看）',
  stepped: '移動模式（每步+1小時）',
  realtime: '即時模式（每3秒+1小時）',
}

const formattedTime = computed(() => {
  const t = gameState.gameTime
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${t.getUTCFullYear()}/${pad(t.getUTCMonth() + 1)}/${pad(t.getUTCDate())} ${pad(t.getUTCHours())}:${pad(t.getUTCMinutes())}:${pad(t.getUTCSeconds())} UTC`
})

const terrainLabel = computed(() => {
  const terrain = gameState.currentTile?.terrain
  return terrain ? terrainStyleStore.getDisplayName(terrain) : '—'
})

/** 換算成好讀的公尺/公里，避免拉遠時顯示七位數的公尺數字。 */
function formatLength(meters: number): string {
  if (Math.abs(meters) >= 10_000) {
    return `${(meters / 1000).toLocaleString('zh-TW', { maximumFractionDigits: 1 })}公里`
  }
  return `${meters.toLocaleString('zh-TW')}公尺`
}

/** 海拔的實際公尺數。換算倍率沿用 tileMeshBuilder 的常數，不要在這裡再寫死一份。 */
const elevationMeters = computed(() =>
  gameState.currentTile ? gameState.currentTile.elevationLevel * METERS_PER_ELEVATION_LEVEL : 0,
)

const formattedCameraDistance = computed(() => formatLength(gameState.cameraDistanceMeters))
const formattedCameraHeight = computed(() => formatLength(gameState.cameraHeightMeters))

/** 面向：六角格所以只有六個值，畫面上方永遠是這個方向。 */
const facingLabel = computed(() => FACING_LABELS[gameState.facingDeg / FACING_STEP_DEG] ?? '—')

/** 九宮格顯示的是玩家目前綁定的按鍵，不是寫死的 WEASDZX。 */
const keypad = computed(() => {
  const m = keyBindings.movementKeys
  return {
    upLeft: displayKey(m.upLeft),
    upRight: displayKey(m.upRight),
    left: displayKey(m.left),
    stay: displayKey(keyBindings.keyFor('stay')),
    right: displayKey(m.right),
    downLeft: displayKey(m.downLeft),
    downRight: displayKey(m.downRight),
  }
})

const turnKeys = computed(() => ({
  left: displayKey(keyBindings.keyFor('turnLeft')),
  right: displayKey(keyBindings.keyFor('turnRight')),
}))
</script>

<template>
  <div class="hud">
    <div>Tile: {{ gameState.currentTile?.tileId ?? '—' }}（{{ terrainLabel }}）</div>
    <div v-if="gameState.currentTile">
      海拔：{{ elevationMeters }}公尺（{{ gameState.currentTile.elevationLevel }}級，畫面起伏×{{
        ELEVATION_EXAGGERATION
      }}）
    </div>
    <div>時間：{{ formattedTime }}</div>
    <div>模式：{{ modeLabel[gameState.mode] }}</div>
    <div>面向：{{ facingLabel }}（{{ gameState.facingDeg }}°，畫面上方就是這個方向）</div>
    <div>視角：{{ gameState.cameraTiltDeg.toFixed(1) }}°（0°=正上方俯視）</div>
    <div>鏡頭：距離 {{ formattedCameraDistance }}、高度 {{ formattedCameraHeight }}</div>
    <div>縮放：{{ gameState.cameraZoomPercent }}%（0=最近、100=最遠）</div>
    <div class="hint keypad">
      <span></span><span>{{ keypad.upLeft }}</span
      ><span>{{ keypad.upRight }}</span> <span>{{ keypad.left }}</span
      ><span>{{ keypad.stay }}</span
      ><span>{{ keypad.right }}</span> <span></span><span>{{ keypad.downLeft }}</span
      ><span>{{ keypad.downRight }}</span>
    </div>
    <div class="hint">
      轉向：{{ turnKeys.left }} 左轉 {{ FACING_STEP_DEG }}°、{{ turnKeys.right }} 右轉
      {{ FACING_STEP_DEG }}°
    </div>
    <div class="buttons">
      <button
        type="button"
        class="home-button"
        title="恢復預設視角與鏡頭距離（也可以按鍵盤 Home）"
        @click="gameState.requestResetToDefaultView()"
      >
        Home
      </button>
      <KeyBindingPanel />
    </div>
  </div>
</template>

<style scoped>
.hud {
  position: fixed;
  top: 12px;
  left: 12px;
  padding: 8px 12px;
  background: rgba(0, 0, 0, 0.5);
  color: #fff;
  font-family: monospace;
  font-size: 13px;
  border-radius: 4px;
  pointer-events: none;
  line-height: 1.6;
}

.hint {
  opacity: 0.7;
  font-size: 11px;
}

.keypad {
  display: grid;
  grid-template-columns: repeat(3, 14px);
  gap: 2px;
  margin-top: 4px;
  text-align: center;
}

/* .hud 是 pointer-events:none（不擋住底下的地圖拖曳），按鈕要自己開回來才點得到。 */
.buttons {
  margin-top: 8px;
  display: flex;
  align-items: flex-start;
  gap: 6px;
}

.home-button {
  pointer-events: auto;
  padding: 4px 10px;
  font-family: monospace;
  font-size: 12px;
  background: rgba(0, 0, 0, 0.5);
  color: #ccc;
  border: 1px solid rgba(255, 255, 255, 0.25);
  border-radius: 4px;
  cursor: pointer;
}

.home-button:hover {
  border-color: rgba(255, 255, 255, 0.6);
  color: #fff;
}
</style>
