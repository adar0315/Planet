<script setup lang="ts">
import { onMounted, onUnmounted, ref, watch } from 'vue'
import * as THREE from 'three'
import { ensureMovementConnectionStarted, stopMovementConnection } from '@/network/signalrClient'
import { findNearestTile } from '@/map/tilePatch'
import { latLonToVector3Into } from '@/map/sphereCoordinates'
import { computeSubsolarPoint } from '@/map/sunPosition'
import { useGameStateStore } from '@/stores/gameState'
import { useKeyBindingsStore } from '@/stores/keyBindings'
import { useTerrainStyleStore } from '@/stores/terrainStyles'
import { usePlanetInfoStore } from '@/stores/planetInfo'
import { createCameraRig } from './cameraRig'
import { createPatchManager } from './patchManager'
import { createPlanetBase } from './planetBase'
import { createPlayerMovement } from './playerMovement'
import { createHudStatsUpdater } from './hudStats'
import { createInputHandlers } from './inputHandlers'
import {
  DEFAULT_CAMERA_DISTANCE_METERS,
  MAX_PIXEL_RATIO,
  MIN_DISTANCE_EDGE_MULTIPLE,
  REALTIME_TICK_MS,
  RENDER_RADIUS,
} from './planetSceneConstants'

/**
 * 走動視角的主場景。這個檔案只負責**組裝**：建立 THREE 的 scene/camera/renderer，
 * 依序建構各個子系統（cameraRig／patchManager／planetBase／playerMovement／inputHandlers／
 * hudStats），把它們串起來跑 render loop，掛載/卸載時建立與釋放資源。
 *
 * 各子系統實際的邏輯都在 `src/scene/*.ts`：
 *   - cameraRig.ts     鏡頭定位、面向朝上的俯視角、轉向動畫
 *   - patchManager.ts  地塊資料、合併mesh、鄰居外框、時段換色
 *   - planetBase.ts    星球本體（填縫用的球體＋遠景貼圖＋日夜色調）
 *   - playerMovement.ts 玩家位置、方向鍵對應、移動與伺服器驗證
 *   - hudStats.ts      每幀算給HUD看的鏡頭數字（指北針角度/傾斜/距離/高度/縮放%）
 *   - inputHandlers.ts 鍵盤與滑鼠點擊，翻譯成上面幾個子系統的呼叫
 *
 * 這裡刻意保留這個順序性的組裝流程、不再拆更細——子系統之間有些初始化順序是真的有依賴
 * （例如 cameraRig 的縮放範圍必須在 patchManager 載入初始patch「之後」、玩家第一次
 * 被定位「之前」設好，見下面 onMounted 裡的註解），拆到render loop本身反而會讓這條
 * 依賴鏈更難看清楚。
 */

const canvasContainer = ref<HTMLDivElement | null>(null)
const loadError = ref<string | null>(null)
// 移動相關的提示（被伺服器拒絕、離線未驗證等）。跟loadError分開：這類訊息不代表載入失敗，
// 而且移動現在是先動再驗證，訊息屬於「事後告知」性質。
const moveWarning = ref<string | null>(null)

let renderer: THREE.WebGLRenderer | null = null
let animationHandle = 0
let resizeObserver: ResizeObserver | null = null
let realtimeTimer: ReturnType<typeof setInterval> | null = null
let keydownHandler: ((event: KeyboardEvent) => void) | null = null
let clickHandler: ((event: MouseEvent) => void) | null = null
let stopResetViewWatch: (() => void) | null = null
let stopResetDefaultViewWatch: (() => void) | null = null
let stopRecolorWatch: (() => void) | null = null
let stopFacingWatch: (() => void) | null = null
// 由 onMounted 建立（需要用到那裡的區域變數），onUnmounted 呼叫。
let disposeScene: (() => void) | null = null

const gameState = useGameStateStore()
const keyBindings = useKeyBindingsStore()
const terrainStyleStore = useTerrainStyleStore()
const planetInfoStore = usePlanetInfoStore()

onMounted(async () => {
  if (!canvasContainer.value) return
  const container = canvasContainer.value

  ensureMovementConnectionStarted().catch((err) => {
    loadError.value = `SignalR連線失敗: ${err instanceof Error ? err.message : String(err)}`
  })
  void terrainStyleStore.load()

  const scene = new THREE.Scene()
  scene.background = new THREE.Color(0x05070d)

  // 容器的 clientWidth/clientHeight 在 mount 當下有時還沒完成排版（會量到0），
  // 所以先用 window 尺寸當初始值，實際大小交給下面的 ResizeObserver 立刻校正。
  const camera = new THREE.PerspectiveCamera(60, window.innerWidth / window.innerHeight, 0.1, 10000)
  camera.position.set(0, RENDER_RADIUS * 0.6, RENDER_RADIUS * 2.2)

  renderer = new THREE.WebGLRenderer({ antialias: true })
  renderer.setSize(window.innerWidth, window.innerHeight)
  renderer.setPixelRatio(Math.min(window.devicePixelRatio, MAX_PIXEL_RATIO))
  container.appendChild(renderer.domElement)

  scene.add(new THREE.AmbientLight(0xffffff, 0.3))
  const sunLight = new THREE.DirectionalLight(0xffffff, 1.4)
  scene.add(sunLight)
  scene.add(sunLight.target) // target固定在星球中心(0,0,0)，加進scene確保matrixWorld每幀正確更新

  // 星球尺寸一律向後端要（PlanetLoadingGate 已保證載入完成），不再在前端寫死一份。
  const planet = planetInfoStore.require()
  const meshOptions = {
    renderRadius: RENDER_RADIUS,
    planetRadiusMeters: planet.radiusMeters,
    edgeLengthMeters: planet.tileEdgeLengthMeters,
  }
  const edgeLengthScene = (planet.tileEdgeLengthMeters / planet.radiusMeters) * RENDER_RADIUS
  // 場景單位換算成星球實際比例的公尺數，給視角資訊的「鏡頭距離」用。
  const sceneToMeters = planet.radiusMeters / RENDER_RADIUS

  const planetBase = createPlanetBase(
    planetInfoStore,
    terrainStyleStore,
    gameState,
    () => renderer === null,
  )
  scene.add(planetBase.mesh)

  const cameraRig = createCameraRig(
    camera,
    renderer.domElement,
    meshOptions,
    edgeLengthScene,
    sceneToMeters,
    gameState,
  )
  const patchManager = createPatchManager(
    scene,
    meshOptions,
    gameState,
    terrainStyleStore,
    () => renderer === null,
  )
  const movement = createPlayerMovement(gameState, cameraRig, patchManager, (message) => {
    moveWarning.value = message
  })
  const inputHandlers = createInputHandlers(
    gameState,
    keyBindings,
    movement,
    patchManager,
    camera,
    () => renderer,
  )

  keydownHandler = inputHandlers.keydownHandler
  window.addEventListener('keydown', keydownHandler)
  clickHandler = inputHandlers.clickHandler
  renderer.domElement.addEventListener('click', clickHandler)

  try {
    // 有存檔（localStorage）就從上次的位置繼續，沒有才從(0,0)開始。
    const savedTile = gameState.currentTile
    const startLat = savedTile?.latitude ?? 0
    const startLon = savedTile?.longitude ?? 0
    const initial = await patchManager.loadPatch(startLat, startLon)

    if (initial) {
      // patch相對整顆星球非常小，鏡頭要貼近這個patch，而不是看整顆星球（那樣patch只會是個小點）。
      // minDistance/maxDistance要在移動鏡頭（內部會呼叫controls.update()做clamp）之前先設好，
      // 不然controls.update()會用建構子當初設的舊範圍（給整顆星球視角用的）把算好的近距離鏡頭夾回去。
      cameraRig.setZoomLimits(edgeLengthScene * MIN_DISTANCE_EDGE_MULTIPLE, RENDER_RADIUS * 4)

      // 決定玩家站在哪一格。優先沿用存檔的tileId（但採用patch裡的新鮮資料，
      // 這樣地圖若被重新生成過，地形/海拔會是最新的）；
      // 找不到（地圖重生過、Id已經不一樣）才退回找地理上最近的一格。
      // 刻意不用「回傳的第一筆」——後端是用原始度數的平方差排序，離赤道越遠越不等於真實距離。
      const savedIndex = savedTile ? initial.patch.indexByTileId.get(savedTile.tileId) : undefined
      const startTile =
        savedIndex !== undefined
          ? initial.patch.tiles[savedIndex]!
          : findNearestTile(initial.patch.tiles, startLat, startLon)

      // 初次載入的鏡頭距離直接用實際公尺數換算，不再依patch大小推算——
      // 這樣「預設看起來多遠」是一個看得懂的數字，調整載入量也不會連帶改變預設視角。
      const initialDistance = DEFAULT_CAMERA_DISTANCE_METERS / sceneToMeters
      if (startTile)
        movement.setPlayerTile(startTile, { recenter: true, distance: initialDistance })
    }
  } catch (err) {
    loadError.value = err instanceof Error ? err.message : '載入地圖資料失敗'
  }

  const hudStats = createHudStatsUpdater(gameState)
  const sunPosition = new THREE.Vector3()

  /** 把畫布尺寸、長寬比、pixelRatio 一次同步好。ResizeObserver 與 render loop 都走這一支。 */
  function syncRendererSize(width: number, height: number) {
    if (!renderer || width === 0 || height === 0) return
    camera.aspect = width / height
    camera.updateProjectionMatrix()
    // 一併更新pixelRatio：視窗被拖到不同DPI的螢幕時devicePixelRatio會變，
    // 只在mount當下設一次的話換螢幕後解析度就不對了。
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, MAX_PIXEL_RATIO))
    renderer.setSize(width, height)
  }
  let lastSunTimeMs = Number.NaN // NaN 保證第一幀一定會算一次
  const sunLightDistance = RENDER_RADIUS * 20
  const animate = () => {
    animationHandle = requestAnimationFrame(animate)

    // 自我修正畫布尺寸：onMounted 中間有數次 await（首批地塊要等網路），
    // 期間容器若從 0 變成實際大小，ResizeObserver 還沒建立就會錯過那次變化，
    // 畫布會一直停在 0×0（畫面整片空白）直到使用者剛好改變視窗大小。
    // 這裡每幀比對一次（兩個屬性讀取，成本可忽略），任何原因造成的不同步都會被補回來。
    if (
      renderer &&
      (container.clientWidth !== renderer.domElement.clientWidth ||
        container.clientHeight !== renderer.domElement.clientHeight)
    ) {
      syncRendererSize(container.clientWidth, container.clientHeight)
    }

    cameraRig.controls.update()

    // 轉向動畫：逐幀把顯示角度從起點內插到終點，中間角度由 cameraRig 內部重新定位鏡頭。
    cameraRig.tickFacingAnimation()

    // 時間變動只立旗標，真正重新上色在這裡做，確保一幀最多做一次。遠景貼圖的色調要跟著一起變，
    // 否則載入範圍邊界會有亮度落差——兩件事一定同時發生，所以用回傳值串起來，不各自判斷一次旗標。
    if (patchManager.flushColorsIfDirty()) {
      planetBase.applyTint()
    }

    // 太陽方向：依遊戲時間算太陽直射點，換算成場景座標放置光源方向（距離夠遠只是為了讓光線近似平行）。
    // 只在遊戲時間真的變了才重算——時間靜止模式下太陽根本不動，原本每幀都重算一次（含配置新物件）純屬浪費。
    const currentTimeMs = gameState.gameTime.getTime()
    if (currentTimeMs !== lastSunTimeMs) {
      lastSunTimeMs = currentTimeMs
      const subsolar = computeSubsolarPoint(gameState.gameTime)
      latLonToVector3Into(subsolar.latitude, subsolar.longitude, sunLightDistance, sunPosition)
      sunLight.position.copy(sunPosition)
    }

    hudStats.update(camera, cameraRig.controls, sceneToMeters)

    renderer!.render(scene, camera)
  }
  animate()

  stopResetViewWatch = watch(
    () => gameState.resetViewSignal,
    () => cameraRig.resetView(),
  )

  stopResetDefaultViewWatch = watch(
    () => gameState.resetToDefaultViewSignal,
    () => cameraRig.resetToDefaultView(),
  )

  stopRecolorWatch = watch(
    () => gameState.gameTime,
    () => patchManager.markColorsDirty(),
  )

  // 轉向：面向一變，鏡頭重新定向成「面向朝上」，六個移動插槽也要跟著重算——
  // 兩件事一定要一起做，只做一半就會變成「畫面轉了但按右還是往原本的右走」。
  // 統一由這個 watch 處理（而不是在 keydown 裡直接做），這樣不論面向是被鍵盤、UI按鈕
  // 還是之後任何來源改動，行為都一致。
  //
  // 鏡頭本身不在這裡瞬間轉過去——cameraRig.beginFacingTurn 只是設定動畫的起點/終點角度，
  // 實際內插由 animate() 逐幀呼叫 tickFacingAnimation 處理，這樣轉向才會平滑過渡而不是一下子
  // 跳到新角度。方向插槽（哪一格是「右」）則立刻重算，不等動畫播完：操作邏輯要立即反應，
  // 鏡頭轉不轉完是純視覺的事，兩者刻意分開處理。
  stopFacingWatch = watch(
    () => gameState.facingDeg,
    () => {
      const tile = gameState.currentTile
      if (!tile) return
      cameraRig.beginFacingTurn()
      movement.rebuildNeighborKeys(tile)
    },
  )

  // 場景資源的釋放邏輯必須在這裡建立：scene/cameraRig/patchManager/planetBase 等都是 onMounted
  // 閉包裡的區域變數，onUnmounted 根本拿不到（原本就是因為這樣才漏掉沒釋放）。
  disposeScene = () => {
    patchManager.dispose()
    planetBase.dispose()
    cameraRig.dispose()

    // renderer.dispose() 只釋放 Three.js 自己的資源，不會歸還 WebGL context——
    // 瀏覽器每個分頁的 context 數量有上限（約16個），而「走動視角↔星球總覽」是用 v-if 整個換掉元件，
    // 每切一次就會新建一個 renderer，不主動歸還的話切十幾次之後畫面就會整個壞掉。
    renderer?.forceContextLoss()
    renderer?.dispose()
    renderer?.domElement.remove()
    renderer = null
  }

  resizeObserver = new ResizeObserver((entries) => {
    const entry = entries[0]
    if (!entry) return
    const { width, height } = entry.contentRect
    if (width === 0 || height === 0) return
    syncRendererSize(width, height)
  })
  resizeObserver.observe(container)
})

// 即時模式：每3秒自動+1小時，跟移動無關；切換模式時要開/關這個計時器。
watch(
  () => gameState.mode,
  (mode) => {
    if (realtimeTimer) {
      clearInterval(realtimeTimer)
      realtimeTimer = null
    }
    if (mode === 'realtime') {
      realtimeTimer = setInterval(() => gameState.advanceTime(1), REALTIME_TICK_MS)
    }
  },
  { immediate: true },
)

onUnmounted(() => {
  cancelAnimationFrame(animationHandle)
  resizeObserver?.disconnect()
  if (keydownHandler) window.removeEventListener('keydown', keydownHandler)
  if (clickHandler) renderer?.domElement.removeEventListener('click', clickHandler)
  if (realtimeTimer) clearInterval(realtimeTimer)
  stopResetViewWatch?.()
  stopResetDefaultViewWatch?.()
  stopRecolorWatch?.()
  stopFacingWatch?.()
  disposeScene?.()
  disposeScene = null
  void stopMovementConnection()
})
</script>

<template>
  <div ref="canvasContainer" class="planet-scene"></div>
  <div v-if="loadError" class="load-error">{{ loadError }}</div>
  <div v-else-if="terrainStyleStore.loadError" class="load-error">
    地形樣式載入失敗，畫面上的地形顏色與名稱都不正確：{{ terrainStyleStore.loadError }}
  </div>
  <div v-else-if="moveWarning" class="move-warning">{{ moveWarning }}</div>
</template>

<style scoped>
/*
 * 用 position:fixed + inset:0 貼齊視窗，不要用 100vw/100vh。
 * 100vw 不等於實際可視寬度——捲軸、瀏覽器縮放（devicePixelRatio 非整數）、
 * 或擴充套件注入的側欄都會讓兩者對不起來，畫布就會比視窗窄，右側露出一條頁面底色的空白。
 * inset:0 直接對齊視窗本身，沒有這個落差。
 */
.planet-scene {
  position: fixed;
  inset: 0;
}

.load-error,
.move-warning {
  position: fixed;
  bottom: 12px;
  left: 50%;
  transform: translateX(-50%);
  padding: 8px 12px;
  color: #fff;
  font-family: monospace;
  font-size: 12px;
  border-radius: 4px;
  pointer-events: none;
}

.load-error {
  background: rgba(140, 20, 20, 0.85);
}

.move-warning {
  background: rgba(140, 90, 20, 0.85);
}
</style>
