<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useGameStateStore } from '@/stores/gameState'
import { usePlanetInfoStore } from '@/stores/planetInfo'
import { computeSubsolarPoint } from '@/map/sunPosition'
import {
  projectOrthographic,
  sampleWaterMask,
  smoothstep,
  sunCosZenith,
  unprojectOrthographic,
} from '@/map/globeIndicator'

const gameState = useGameStateStore()
const planetInfoStore = usePlanetInfoStore()

const CX = 60
const CY = 60
const R = 50
/** viewBox 留出邊界空間給 N/S/東/西 四個標籤，否則它們會被裁掉（舊版就是這樣被切掉的）。 */
const VIEW_BOX = '-10 -12 140 148'
/** 畫布解析度上限，與 PlanetScene 的 pixelRatio 上限一致。 */
const MAX_PIXEL_RATIO = 2

/** 晨昏過渡帶的寬度（以太陽天頂角餘弦計）。太小會是硬邊，太大整顆球會糊掉。 */
const TWILIGHT_EDGE = 0.12
/** 夜側的亮度倍率（0=全黑）。留一點亮度才看得出夜半球的陸海輪廓。 */
const NIGHT_BRIGHTNESS = 0.34

/** 陸與海的底色（會再乘上日照亮度）。刻意用高對比的兩色，在120px的小圓裡才讀得出形狀。 */
const LAND_COLOR = { r: 0x6f, g: 0x9c, b: 0x4f }
const WATER_COLOR = { r: 0x14, g: 0x3d, b: 0x66 }

const canvasRef = ref<HTMLCanvasElement | null>(null)

/** 視角中心經度＝玩家經度，所以玩家永遠在正中央，也就分得出正反面了。 */
const refLon = computed(() => gameState.currentTile?.longitude ?? 0)

const subsolar = computed(() => computeSubsolarPoint(gameState.gameTime))

/** 玩家標記：依定義 x 永遠是圓心，y 表示緯度。 */
const playerPoint = computed(() => {
  if (!gameState.currentTile) return null
  return projectOrthographic(gameState.currentTile.latitude, gameState.currentTile.longitude, refLon.value, CX, CY, R)
})

/** 太陽標記：算一次給 :cx/:cy 共用（舊版在模板裡呼叫了兩次）。 */
const sunPoint = computed(() =>
  projectOrthographic(subsolar.value.latitude, subsolar.value.longitude, refLon.value, CX, CY, R),
)

/**
 * 逐像素畫出球面：反向正射投影得到經緯度 → 取樣陸海遮罩 → 依太陽天頂角決定明暗。
 *
 * 為什麼不用 SVG：舊版的夜側是「通過球心的半平面」，而玩家改成固定在球心之後，
 * 玩家會永遠正好落在那條分界線上，等於看不出自己是白天還是晚上。
 * 改成逐像素之後，光影是精確的（不是半平面近似），而且陸海輪廓也順便畫出來了。
 */
function renderGlobe() {
  const canvas = canvasRef.value
  if (!canvas) return
  const ctx = canvas.getContext('2d')
  if (!ctx) return

  const scale = Math.min(window.devicePixelRatio || 1, MAX_PIXEL_RATIO)
  const size = Math.round(R * 2 * scale)
  if (canvas.width !== size || canvas.height !== size) {
    canvas.width = size
    canvas.height = size
  }

  const info = planetInfoStore.info
  const mask = planetInfoStore.waterMask
  const maskWidth = info?.waterMaskWidth ?? 0
  const maskHeight = info?.waterMaskHeight ?? 0
  const hasMask = mask !== null && maskWidth > 0 && maskHeight > 0

  const sub = subsolar.value
  const ref = refLon.value
  const image = ctx.createImageData(size, size)
  const data = image.data

  for (let py = 0; py < size; py++) {
    // 畫布 y 向下，反投影的 v 向上，所以這裡翻號
    const v = 1 - ((py + 0.5) / size) * 2
    for (let px = 0; px < size; px++) {
      const u = ((px + 0.5) / size) * 2 - 1
      const offset = (py * size + px) * 4

      const point = unprojectOrthographic(u, v, ref)
      if (!point) {
        data[offset + 3] = 0 // 圓外：透明
        continue
      }

      const isWater = hasMask
        ? sampleWaterMask(mask, maskWidth, maskHeight, point.latitudeDeg, point.longitudeDeg)
        : true
      const base = isWater ? WATER_COLOR : LAND_COLOR

      const lit = smoothstep(
        -TWILIGHT_EDGE,
        TWILIGHT_EDGE,
        sunCosZenith(point.latitudeDeg, point.longitudeDeg, sub.latitude, sub.longitude),
      )
      const brightness = NIGHT_BRIGHTNESS + (1 - NIGHT_BRIGHTNESS) * lit

      data[offset] = base.r * brightness
      data[offset + 1] = base.g * brightness
      data[offset + 2] = base.b * brightness
      data[offset + 3] = 255
    }
  }

  ctx.putImageData(image, 0, 0)
}

// 只在「玩家位置」或「遊戲時間」變動時重畫——不是每幀。
// 即時模式每3秒一次、移動模式每步一次，約8千個像素的成本可以忽略。
const renderKey = computed(() => {
  const tile = gameState.currentTile
  return `${tile?.latitude ?? 0}|${tile?.longitude ?? 0}|${gameState.gameTime.getTime()}|${planetInfoStore.waterMask ? 1 : 0}`
})

let stopRenderWatch: (() => void) | null = null

onMounted(() => {
  renderGlobe()
  stopRenderWatch = watch(renderKey, renderGlobe)
})

onUnmounted(() => {
  stopRenderWatch?.()
})
</script>

<template>
  <div class="globe-indicator">
    <!-- 外層 pointer-events:none 才不會擋住底下的畫面互動；title 掛在內層並開啟 pointer-events，
         否則說明永遠不會出現（舊版就是這個問題）。 -->
    <div class="globe-inner" title="目前位置在星球上的相對位置。圖會跟著你轉，你永遠在正中央；亮面為白天、暗面為夜晚。">
      <canvas ref="canvasRef" class="globe-canvas" />

      <svg :viewBox="VIEW_BOX" class="globe-overlay">
        <circle :cx="CX" :cy="CY" :r="R" class="globe-outline" />

        <!-- 赤道與極軸 -->
        <line :x1="CX - R" :y1="CY" :x2="CX + R" :y2="CY" class="ref-line" />
        <line :x1="CX" :y1="CY - R" :x2="CX" :y2="CY + R" class="ref-line" />

        <text :x="CX" :y="CY - R - 4" class="label">N</text>
        <text :x="CX" :y="CY + R + 11" class="label minor">S</text>
        <!-- 視角會跟著玩家轉，所以不能再標固定的「換日線」；東西方位在任何旋轉角度下都成立。 -->
        <text :x="CX - R - 4" :y="CY + 3.5" class="label minor" text-anchor="end">西</text>
        <text :x="CX + R + 4" :y="CY + 3.5" class="label minor" text-anchor="start">東</text>

        <!-- 太陽：在背面（地平線下）時畫成空心，代表玩家這裡是夜晚 -->
        <circle
          :cx="sunPoint.x"
          :cy="sunPoint.y"
          r="3"
          class="sun-marker"
          :class="{ 'is-behind': !sunPoint.isFrontFacing }"
        />

        <circle v-if="playerPoint" :cx="playerPoint.x" :cy="playerPoint.y" r="3.5" class="player-marker" />
      </svg>
    </div>
  </div>
</template>

<style scoped>
.globe-indicator {
  position: fixed;
  bottom: 12px;
  right: 12px;
  pointer-events: none;
}

.globe-inner {
  position: relative;
  width: 140px;
  height: 148px;
  pointer-events: auto;
}

/* 畫布只佔圓的範圍，SVG 疊在上層（SVG 的 viewBox 比圓大，留給標籤） */
.globe-canvas {
  position: absolute;
  left: calc((10 / 140) * 100%);
  top: calc((12 / 148) * 100%);
  width: calc((100 / 140) * 100%);
  height: calc((100 / 148) * 100%);
  border-radius: 50%;
}

.globe-overlay {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
}

.globe-outline {
  fill: none;
  stroke: rgba(255, 255, 255, 0.35);
  stroke-width: 1.5;
}

.ref-line {
  stroke: rgba(255, 255, 255, 0.18);
  stroke-width: 1;
  stroke-dasharray: 2 2;
}

.label {
  fill: #ccc;
  font-family: monospace;
  font-size: 11px;
  text-anchor: middle;
}

.label.minor {
  fill: #888;
  font-size: 9px;
}

.sun-marker {
  fill: #ffd35c;
  stroke: #7a5b00;
  stroke-width: 0.5;
}

/* 太陽在地平線下：空心，表示現在是夜晚 */
.sun-marker.is-behind {
  fill: none;
  stroke: rgba(255, 211, 92, 0.55);
  stroke-width: 1;
}

.player-marker {
  fill: #ffe066;
  stroke: #7a5b00;
  stroke-width: 1;
}
</style>
