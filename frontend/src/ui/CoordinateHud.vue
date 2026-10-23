<script setup lang="ts">
import { computed } from 'vue'
import { compassPointLabel, useGameStateStore } from '@/stores/gameState'
import { useKeyBindingsStore, displayKey } from '@/stores/keyBindings'
import KeyBindingPanel from './KeyBindingPanel.vue'
import { useTerrainStyleStore } from '@/stores/terrainStyles'
import { ELEVATION_EXAGGERATION, METERS_PER_ELEVATION_LEVEL } from '@/map/tileMeshBuilder'
import { FLIGHT_TIERS, SPEED_OF_SOUND_MPS } from '@/map/flightMotion'

const gameState = useGameStateStore()
const keyBindings = useKeyBindingsStore()
const terrainStyleStore = useTerrainStyleStore()
void terrainStyleStore.load()

const modeLabel: Record<string, string> = {
  observe: '時間靜止（自由觀看）',
  stepped: '移動模式（走路每跨一格+1小時，飛行依速度換算）',
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

/** 面向：連續角度，顯示最接近的八方位與度數。 */
const facingLabel = computed(() => compassPointLabel(gameState.facingDeg))

/** 按鍵說明顯示的是玩家目前綁定的按鍵，不是寫死的 WASD。 */
const keys = computed(() => ({
  forward: displayKey(keyBindings.keyFor('moveForward')),
  back: displayKey(keyBindings.keyFor('moveBack')),
  left: displayKey(keyBindings.keyFor('strafeLeft')),
  right: displayKey(keyBindings.keyFor('strafeRight')),
  turnLeft: displayKey(keyBindings.keyFor('turnLeft')),
  turnRight: displayKey(keyBindings.keyFor('turnRight')),
  wait: displayKey(keyBindings.keyFor('wait')),
  toggleFlight: displayKey(keyBindings.keyFor('toggleFlight')),
  godView: displayKey(keyBindings.keyFor('toggleGodView')),
  ascend: displayKey(keyBindings.keyFor('ascend')),
  descend: displayKey(keyBindings.keyFor('descend')),
  tiers: [
    displayKey(keyBindings.keyFor('flightTier1')),
    displayKey(keyBindings.keyFor('flightTier2')),
    displayKey(keyBindings.keyFor('flightTier3')),
  ].join('/'),
}))

/** 飛行區塊：檔位、速度（含馬赫數）、離地高度。 */
const flightLabel = computed(() => {
  const f = gameState.flight
  const tier = FLIGHT_TIERS[f.tierIndex]?.label ?? '—'
  const mach = (f.speedMps / SPEED_OF_SOUND_MPS).toFixed(1)
  return {
    status: f.isLanding ? '降落中' : `${tier}檔`,
    speed: `${f.speedMps.toLocaleString('zh-TW')} m/s（馬赫 ${mach}）`,
    altitude: formatLength(f.altitudeMeters),
  }
})
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
    <div v-if="gameState.isGodView" class="god-view">
      上帝視角（時間暫停、角色留在原地）：{{ keys.forward }}{{ keys.left }}{{ keys.back }}{{ keys.right }}
      移動、{{ keys.ascend }}{{ keys.descend }}／滾輪升降、{{ keys.godView }} 回到角色
    </div>
    <div v-if="gameState.flight.isFlying" class="flight">
      飛行：{{ flightLabel.status }}、{{ flightLabel.speed }}、離地 {{ flightLabel.altitude }}
    </div>
    <div>面向：{{ facingLabel }}（{{ gameState.facingDeg.toFixed(1) }}°）</div>
    <div>視角：{{ gameState.cameraTiltDeg.toFixed(1) }}°（0°=正上方俯視）</div>
    <div>鏡頭：距離 {{ formattedCameraDistance }}、高度 {{ formattedCameraHeight }}</div>
    <div>縮放：{{ gameState.cameraZoomPercent }}%（0=最近、100=最遠）</div>
    <div class="hint keypad">
      <span>{{ keys.turnLeft }}</span><span>{{ keys.forward }}</span><span>{{ keys.turnRight }}</span>
      <span>{{ keys.left }}</span><span>{{ keys.back }}</span><span>{{ keys.right }}</span>
    </div>
    <div class="hint">
      {{ keys.forward }}{{ keys.back }} 前進後退、{{ keys.left }}{{ keys.right }} 橫移、{{ keys.turnLeft
      }}{{ keys.turnRight }} 轉向、Shift 跑步、{{ keys.wait }} 原地等待
    </div>
    <div class="hint">
      {{ keys.toggleFlight }} 起飛／降落、{{ keys.ascend }}{{ keys.descend }} 上升下降、{{ keys.tiers }}
      飛行速度檔、{{ keys.godView }} 上帝視角
    </div>
    <div class="hint">滑鼠：滾輪縮放、左鍵拖曳環顧、右鍵拖曳轉身</div>
    <div class="buttons">
      <button
        type="button"
        class="home-button"
        title="恢復預設視角與鏡頭距離（也可以按鍵盤 Home）"
        @click="gameState.requestResetToDefaultView()"
      >
        Home
      </button>
      <button
        type="button"
        class="home-button"
        :class="{ active: gameState.isGodView }"
        :title="`鏡頭脫離角色自由移動，時間暫停（也可以按 ${keys.godView}）`"
        @click="gameState.requestToggleGodView()"
      >
        {{ gameState.isGodView ? '回到角色' : '上帝視角' }}
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

.god-view {
  color: #ffd27f;
}

.home-button.active {
  border-color: #ffd27f;
  color: #ffd27f;
}

.flight {
  color: #9fd8ff;
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
