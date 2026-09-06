<script setup lang="ts">
import { onMounted, onUnmounted, ref, watch } from 'vue'
import * as THREE from 'three'
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js'
import { useSimulationStore } from '@/stores/simulation'

/**
 * 地質演化模式：把後端模擬的當下狀態貼在一顆球上，可以一邊跑一邊看星球長出大陸。
 *
 * 跟走動視角（PlanetScene.vue）、總覽視角（PlanetOverview.vue）一樣是**完全獨立**的
 * Three.js 場景與 render loop，不共用任何狀態。
 *
 * 貼圖的取樣慣例跟 `OverviewTextureGenerator` 完全一致（第0列＝北極、左到右＝經度-180→+180），
 * 所以這裡可以直接沿用總覽模式那顆球體與材質設定，包含 `flipY = true` 那個坑
 * （DataTexture 預設 flipY=false，不設的話畫面會南北顛倒）。
 */
const SPHERE_RADIUS = 50

const simulation = useSimulationStore()

const canvasContainer = ref<HTMLDivElement | null>(null)

let renderer: THREE.WebGLRenderer | null = null
let animationHandle = 0
let resizeObserver: ResizeObserver | null = null
let disposeScene: (() => void) | null = null

/** 目前掛在球上的貼圖，換欄位／推進之後要把新的位元組上傳上去。 */
let dataTexture: THREE.DataTexture | null = null

/**
 * 自己留一份 DataTexture 底下那塊緩衝區的參考。
 * `dataTexture.image.data` 在型別上是可為 null 的，而且每步都要寫進去，
 * 與其每次都判斷，不如在建立時就把它記下來。
 */
let textureBytes: Uint8Array | null = null
let sphere: THREE.Mesh | null = null

/**
 * 畫出星球的自轉軸。
 *
 * 貼圖是等距柱狀投影、第0列＝北極，貼到 `SphereGeometry` 之後極點會落在 **Y 軸**上，
 * 所以自轉軸就是 Y 軸。軸線兩端刻意伸出球體之外一截，從任何角度都看得到它穿出來。
 *
 * 這件事在平面貼圖上看不出來（極點被攤成整條上下邊），只有在球體上標出來才有意義——
 * 板塊的歐拉極偏向自轉軸、中洋脊因此偏南北向（見 `PlateSet.EulerPoleSpinAxisBias`），
 * 要判斷這件事對不對就得先看得到軸在哪。
 */
function addSpinAxis(scene: THREE.Scene) {
  const overhang = SPHERE_RADIUS * 1.35

  // 北端偏暖、南端偏冷，一眼分得出哪邊是北
  for (const [from, to, color] of [
    [0, overhang, 0xff9a4d],
    [-overhang, 0, 0x4da6ff],
  ] as const) {
    const geometry = new THREE.BufferGeometry().setFromPoints([
      new THREE.Vector3(0, from, 0),
      new THREE.Vector3(0, to, 0),
    ])
    scene.add(new THREE.Line(geometry, new THREE.LineBasicMaterial({ color })))
  }

  // 兩端各放一個小圓錐指出方向，光線不足時線條本身不夠明顯
  for (const [y, color, flip] of [
    [overhang, 0xff9a4d, false],
    [-overhang, 0x4da6ff, true],
  ] as const) {
    const cone = new THREE.Mesh(
      new THREE.ConeGeometry(SPHERE_RADIUS * 0.035, SPHERE_RADIUS * 0.12, 12),
      new THREE.MeshBasicMaterial({ color }),
    )
    cone.position.set(0, y, 0)
    if (flip) cone.rotation.x = Math.PI
    scene.add(cone)
  }
}

/** 建立或更新球體的貼圖。尺寸從後端的 status 拿，不寫死。 */
function applyTexture(scene: THREE.Scene) {
  const rgba = simulation.texture
  const status = simulation.status
  if (!rgba || !status) return

  // 尺寸沒變就只換內容，不要重建 DataTexture——每步都重建會不斷產生要回收的GPU資源
  if (dataTexture && textureBytes && textureBytes.length === rgba.length) {
    textureBytes.set(rgba)
    dataTexture.needsUpdate = true
    return
  }

  dataTexture?.dispose()
  textureBytes = new Uint8Array(rgba)
  dataTexture = new THREE.DataTexture(
    textureBytes,
    status.textureWidth,
    status.textureHeight,
    THREE.RGBAFormat,
  )
  dataTexture.flipY = true
  dataTexture.colorSpace = THREE.SRGBColorSpace
  dataTexture.needsUpdate = true

  if (!sphere) {
    sphere = new THREE.Mesh(
      new THREE.SphereGeometry(SPHERE_RADIUS, 64, 64),
      new THREE.MeshStandardMaterial({ map: dataTexture, roughness: 1 }),
    )
    scene.add(sphere)
    return
  }

  const material = sphere.material as THREE.MeshStandardMaterial
  material.map = dataTexture
  material.needsUpdate = true
}

onMounted(async () => {
  if (!canvasContainer.value) return
  const container = canvasContainer.value

  const scene = new THREE.Scene()
  scene.background = new THREE.Color(0x05070d)

  const camera = new THREE.PerspectiveCamera(60, window.innerWidth / window.innerHeight, 0.1, 10000)
  camera.position.set(0, SPHERE_RADIUS * 0.4, SPHERE_RADIUS * 2.6)

  renderer = new THREE.WebGLRenderer({ antialias: true })
  renderer.setSize(window.innerWidth, window.innerHeight)
  renderer.setPixelRatio(window.devicePixelRatio)
  container.appendChild(renderer.domElement)

  const controls = new OrbitControls(camera, renderer.domElement)
  controls.enableDamping = true
  controls.minDistance = SPHERE_RADIUS * 1.2
  controls.maxDistance = SPHERE_RADIUS * 6
  controls.target.set(0, 0, 0)

  addSpinAxis(scene)

  scene.add(new THREE.AmbientLight(0xffffff, 0.75))
  const sunLight = new THREE.DirectionalLight(0xffffff, 0.9)
  sunLight.position.set(SPHERE_RADIUS * 3, SPHERE_RADIUS * 2, SPHERE_RADIUS * 2)
  scene.add(sunLight)

  await simulation.initialize()
  applyTexture(scene)

  // 貼圖換新內容（推進了一步、或換了欄位）就重新上傳。看的是版本號而不是陣列本身——
  // 位元組陣列是就地覆寫的，參考不變，watch 陣列不會觸發。
  const stopWatching = watch(() => simulation.textureVersion, () => applyTexture(scene))

  const animate = () => {
    animationHandle = requestAnimationFrame(animate)
    controls.update()
    renderer!.render(scene, camera)
  }
  animate()

  resizeObserver = new ResizeObserver((entries) => {
    const entry = entries[0]
    if (!entry || !renderer) return
    const { width, height } = entry.contentRect
    if (width === 0 || height === 0) return

    camera.aspect = width / height
    camera.updateProjectionMatrix()
    renderer.setSize(width, height)
  })
  resizeObserver.observe(container)

  disposeScene = () => {
    stopWatching()
    scene.traverse((obj) => {
      // 軸線是 THREE.Line、不是 Mesh，只判斷 Mesh 會把它的 geometry/material 漏掉
      if (!(obj instanceof THREE.Mesh) && !(obj instanceof THREE.Line)) return
      obj.geometry.dispose()
      const material = obj.material as THREE.MeshStandardMaterial
      material.map?.dispose() // 1024×512 的 DataTexture（2MB），不釋放會一直留在GPU記憶體
      material.dispose()
    })
    scene.clear()
    controls.dispose()
    dataTexture = null
    textureBytes = null
    sphere = null

    // 跟另外兩個視角同樣的理由：dispose() 不會歸還 WebGL context，
    // 三個場景是用 v-if 互相切換的，不主動歸還的話切幾十次就會超過瀏覽器的 context 上限。
    renderer?.forceContextLoss()
    renderer?.dispose()
    renderer?.domElement.remove()
    renderer = null
  }
})

onUnmounted(() => {
  cancelAnimationFrame(animationHandle)
  resizeObserver?.disconnect()
  disposeScene?.()
  disposeScene = null
})
</script>

<template>
  <div ref="canvasContainer" class="planet-evolution"></div>
  <div v-if="!simulation.status" class="loading-hint">載入模擬狀態中…</div>
</template>

<style scoped>
/* 與另外兩個視角同樣的理由：100vw 不等於實際可視寬度，改用 inset:0 貼齊視窗。 */
.planet-evolution {
  position: fixed;
  inset: 0;
}

.loading-hint {
  position: fixed;
  top: 50%;
  left: 50%;
  transform: translate(-50%, -50%);
  padding: 10px 16px;
  background: rgba(0, 0, 0, 0.6);
  color: #ccc;
  font-family: monospace;
  border-radius: 4px;
}
</style>
