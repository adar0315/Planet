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
import { createPlayerController } from './playerController'
import { createPlayerAvatar } from './playerAvatar'
import { createHudStatsUpdater } from './hudStats'
import { createInputHandlers } from './inputHandlers'
import { createGodView } from './godView'
import { createSeasonData } from './seasonData'
import { createSubTileManager } from './subTileManager'
import {
  AVATAR_SIZE_PER_CAMERA_DISTANCE,
  DEFAULT_CAMERA_DISTANCE_METERS,
  MAX_PIXEL_RATIO,
  MIN_DISTANCE_EDGE_MULTIPLE,
  REALTIME_TICK_MS,
  RENDER_RADIUS,
} from './planetSceneConstants'

/**
 * 走動視角的主場景。這個檔案只負責**組裝**：建立 THREE 的 scene/camera/renderer，
 * 依序建構各個子系統（cameraRig／patchManager／planetBase／playerController／playerAvatar／inputHandlers／
 * hudStats），把它們串起來跑 render loop，掛載/卸載時建立與釋放資源。
 *
 * 各子系統實際的邏輯都在 `src/scene/*.ts`：
 *   - cameraRig.ts        追尾鏡頭：跟在玩家後上方、滾輪縮放、滑鼠拖曳環顧／轉身
 *   - patchManager.ts     地塊資料、合併mesh、所在格外框、時段換色
 *   - planetBase.ts       星球本體（填縫用的球體＋遠景貼圖＋日夜色調）
 *   - playerController.ts 連續移動：位置、面向、速度、跨格偵測與伺服器驗證
 *   - playerAvatar.ts     角色模型（目前是指向前方的圓錐）
 *   - hudStats.ts         每幀算給HUD看的鏡頭數字（指北針角度/傾斜/距離/高度/縮放%）
 *   - inputHandlers.ts    鍵盤按住／放開，翻譯成 playerController 的呼叫
 *   - godView.ts          上帝視角：鏡頭脫離角色自由移動，時間不動
 *   - subTileManager.ts   近看細分：鏡頭拉近時把附近的地塊換成 250 公尺的小六角
 *   - seasonData.ts       模擬粗格的四季資料（積雪、結冰、河的水位）
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
let keyupHandler: ((event: KeyboardEvent) => void) | null = null
let blurHandler: (() => void) | null = null
let stopResetViewWatch: (() => void) | null = null
let stopResetDefaultViewWatch: (() => void) | null = null
let stopRecolorWatch: (() => void) | null = null
let stopGodViewWatch: (() => void) | null = null
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

  // 右鍵拖曳轉身要呼叫 controller，但 controller 晚一點才建好——用閉包延後解析
  let turnByDrag: (deltaDeg: number) => void = () => {}
  const cameraRig = createCameraRig(
    camera,
    renderer.domElement,
    DEFAULT_CAMERA_DISTANCE_METERS / sceneToMeters,
    (deltaDeg) => turnByDrag(deltaDeg),
  )
  const seasonData = createSeasonData()
  gameState.seasonsOf = seasonData.seasonsOf
  const patchManager = createPatchManager(
    scene,
    meshOptions,
    gameState,
    terrainStyleStore,
    () => renderer === null,
    seasonData,
  )
  const subTiles = createSubTileManager(scene, meshOptions, patchManager, seasonData, () => renderer === null)
  const controller = createPlayerController(
    gameState,
    patchManager,
    planet.radiusMeters,
    meshOptions,
    planet.tileEdgeLengthMeters,
    (message) => {
      moveWarning.value = message
    },
  )

  const godView = createGodView(
    controller,
    patchManager,
    cameraRig,
    () => gameState.currentTile,
    planet.radiusMeters,
    meshOptions,
  )
  turnByDrag = (deltaDeg) =>
    godView.active ? godView.turnBy(deltaDeg) : controller.turnBy(deltaDeg)

  let playerPlaced = false
  // 離開上帝視角要等地塊載回角色身邊，這段期間不接受再次切換——
  // 否則新一趟的補載可能跟離開時的補載搶著換 patch
  let godViewExiting = false
  function toggleGodView() {
    if (!playerPlaced || godViewExiting) return
    if (!godView.active) {
      godView.enter()
      gameState.isGodView = true
      return
    }
    gameState.isGodView = false
    godViewExiting = true
    void godView.exit().finally(() => {
      godViewExiting = false
    })
  }

  const avatar = createPlayerAvatar()
  avatar.object.visible = false // 玩家被放到某一格之前不顯示
  scene.add(avatar.object)

  const inputHandlers = createInputHandlers(
    gameState,
    keyBindings,
    controller,
    godView,
    toggleGodView,
  )
  keydownHandler = inputHandlers.keydownHandler
  keyupHandler = inputHandlers.keyupHandler
  blurHandler = inputHandlers.blurHandler
  window.addEventListener('keydown', keydownHandler)
  window.addEventListener('keyup', keyupHandler)
  window.addEventListener('blur', blurHandler)

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
      cameraRig.setDistance(DEFAULT_CAMERA_DISTANCE_METERS / sceneToMeters)
      if (startTile) {
        controller.placeAtTile(startTile)
        avatar.object.visible = true
        playerPlaced = true
      }
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
  let lastFrameAt = performance.now()
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

    const now = performance.now()
    const dt = (now - lastFrameAt) / 1000
    lastFrameAt = now

    // 玩家先動，鏡頭與角色再跟上——順序反過來的話鏡頭永遠落後一幀，移動時畫面會抖。
    // 上帝視角時角色不 tick（留在原地、不送驗證、不推進時間），鏡頭改跟上帝視角的注視點。
    if (playerPlaced) {
      if (godView.active) {
        godView.tick(dt)
        cameraRig.update(godView.focusPoint, godView.forward, godView.isMoving, dt)
      } else {
        controller.tick(dt)
        cameraRig.update(controller.focusPoint, controller.forward, controller.isMoving, dt)
      }
      // 角色大小依「鏡頭到角色」的距離：平常就是鏡頭距離；上帝視角時鏡頭不在角色身上，
      // 用鏡頭距離的話拉到 5,000 公里高空，角色會變成幾百公里長
      avatar.update(
        controller.focusPoint,
        controller.forward,
        camera.position.distanceTo(controller.focusPoint) * AVATAR_SIZE_PER_CAMERA_DISTANCE,
        controller.isFlying,
        controller.groundPoint,
      )
    }

    // 時間變動只立旗標，真正重新上色在這裡做，確保一幀最多做一次。遠景貼圖的色調要跟著一起變，
    // 否則載入範圍邊界會有亮度落差——兩件事一定同時發生，所以用回傳值串起來，不各自判斷一次旗標。
    if (patchManager.flushColorsIfDirty()) {
      planetBase.applyTint()
      subTiles.recolor()
    }
    // 近看細分：跟著鏡頭的注視點（上帝視角時也一樣）
    if (playerPlaced) subTiles.update(cameraRig.target, cameraRig.distance * sceneToMeters)

    // 太陽方向：依遊戲時間算太陽直射點，換算成場景座標放置光源方向（距離夠遠只是為了讓光線近似平行）。
    // 只在遊戲時間真的變了才重算——時間靜止模式下太陽根本不動，原本每幀都重算一次（含配置新物件）純屬浪費。
    const currentTimeMs = gameState.gameTime.getTime()
    if (currentTimeMs !== lastSunTimeMs) {
      lastSunTimeMs = currentTimeMs
      const subsolar = computeSubsolarPoint(gameState.gameTime)
      latLonToVector3Into(subsolar.latitude, subsolar.longitude, sunLightDistance, sunPosition)
      sunLight.position.copy(sunPosition)
    }

    hudStats.update(camera, cameraRig, sceneToMeters)

    renderer!.render(scene, camera)
  }
  animate()

  // 指北針點擊：轉成面向正北、環顧回正（畫面上方＝正北）
  stopResetViewWatch = watch(
    () => gameState.resetViewSignal,
    () => {
      if (godView.active) godView.faceNorth()
      else controller.faceNorth()
      cameraRig.resetView()
    },
  )

  stopResetDefaultViewWatch = watch(
    () => gameState.resetToDefaultViewSignal,
    () => cameraRig.resetToDefaultView(),
  )

  stopGodViewWatch = watch(() => gameState.godViewToggleSignal, toggleGodView)

  stopRecolorWatch = watch(
    () => gameState.gameTime,
    () => patchManager.markColorsDirty(),
  )

  // 場景資源的釋放邏輯必須在這裡建立：scene/cameraRig/patchManager/planetBase 等都是 onMounted
  // 閉包裡的區域變數，onUnmounted 根本拿不到（原本就是因為這樣才漏掉沒釋放）。
  disposeScene = () => {
    // 上帝視角是這個場景的狀態，切到別的視角就結束；store 會留著，不清掉的話回來時 HUD 還顯示「時間暫停」
    gameState.isGodView = false
    subTiles.dispose()
    patchManager.dispose()
    planetBase.dispose()
    cameraRig.dispose()
    avatar.dispose()

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
      // 上帝視角期間時間不動
      realtimeTimer = setInterval(() => {
        if (!gameState.isGodView) gameState.advanceTime(1)
      }, REALTIME_TICK_MS)
    }
  },
  { immediate: true },
)

onUnmounted(() => {
  cancelAnimationFrame(animationHandle)
  resizeObserver?.disconnect()
  if (keydownHandler) window.removeEventListener('keydown', keydownHandler)
  if (keyupHandler) window.removeEventListener('keyup', keyupHandler)
  if (blurHandler) window.removeEventListener('blur', blurHandler)
  if (realtimeTimer) clearInterval(realtimeTimer)
  stopResetViewWatch?.()
  stopResetDefaultViewWatch?.()
  stopRecolorWatch?.()
  stopGodViewWatch?.()
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
