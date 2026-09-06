<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import * as THREE from 'three'
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js'
import { fetchOverviewTexture } from '@/network/apiClient'
import { usePlanetInfoStore } from '@/stores/planetInfo'

/**
 * 全星球總覽：跟走動視角（PlanetScene.vue）是完全獨立的Three.js場景/render loop，不共用任何狀態
 * （只有「這是同一顆星球」這件事是共用的）。用低解析度的等距柱狀投影貼圖（後端
 * OverviewTextureGenerator算出來的，不是查2000萬格地塊資料）貼在一顆簡單球體上，
 * 可以自由旋轉/縮放看整顆星球——這是效能評估後選定的方案：查全部地塊資料在查詢時間、
 * 傳輸量、繪製呼叫數三個維度都不可行，見 backend/src/Planet.Data/TileRepository.cs 的說明。
 */
// 貼圖尺寸由後端提供（見 usePlanetInfoStore），不在這裡寫死——寫死的話後端改了解析度，
// 這裡會用錯誤的寬高去解讀那塊RGBA緩衝區，畫面會整個扭曲錯位。
// SPHERE_RADIUS 則純粹是這個場景自己的顯示比例，跟後端無關，留在前端是對的。
const SPHERE_RADIUS = 50

const planetInfoStore = usePlanetInfoStore()

const canvasContainer = ref<HTMLDivElement | null>(null)
const loadError = ref<string | null>(null)
const isLoading = ref(true)

let renderer: THREE.WebGLRenderer | null = null
let animationHandle = 0
let resizeObserver: ResizeObserver | null = null
// 由 onMounted 建立（需要用到那裡的區域變數），onUnmounted 呼叫。
let disposeScene: (() => void) | null = null

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

  scene.add(new THREE.AmbientLight(0xffffff, 0.55))
  const sunLight = new THREE.DirectionalLight(0xffffff, 1.0)
  sunLight.position.set(SPHERE_RADIUS * 3, SPHERE_RADIUS * 2, SPHERE_RADIUS * 2)
  scene.add(sunLight)

  try {
    const buffer = await fetchOverviewTexture()
    const rgba = new Uint8Array(buffer)

    const planet = planetInfoStore.require()
    const texture = new THREE.DataTexture(
      rgba,
      planet.overviewTextureWidth,
      planet.overviewTextureHeight,
      THREE.RGBAFormat,
    )
    // DataTexture預設flipY=false（跟一般圖片載入的Texture不同），但貼圖buffer第0列是北極，
    // SphereGeometry的uv.y=1對應到北極——不設flipY=true的話畫面會南北顛倒。
    texture.flipY = true
    texture.colorSpace = THREE.SRGBColorSpace
    texture.needsUpdate = true

    const sphere = new THREE.Mesh(
      new THREE.SphereGeometry(SPHERE_RADIUS, 64, 64),
      new THREE.MeshStandardMaterial({ map: texture, roughness: 1 }),
    )
    scene.add(sphere)
    isLoading.value = false
  } catch (err) {
    loadError.value = err instanceof Error ? err.message : '載入星球總覽貼圖失敗'
    isLoading.value = false
  }

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

  // 釋放邏輯建在這裡，因為 scene/controls 都是這個閉包的區域變數，onUnmounted 取不到。
  disposeScene = () => {
    scene.traverse((obj) => {
      if (!(obj instanceof THREE.Mesh)) return
      obj.geometry.dispose()
      const material = obj.material as THREE.MeshStandardMaterial
      material.map?.dispose() // 這張是1024×512的DataTexture（2MB），不釋放會一直留在GPU記憶體
      material.dispose()
    })
    scene.clear()
    controls.dispose()

    // 跟走動視角同樣的理由：dispose()不會歸還WebGL context，而這兩個場景是用v-if互相切換的，
    // 每切一次就各自新建一個renderer，不主動歸還的話十幾次之後就會超過瀏覽器的context上限。
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
  <div ref="canvasContainer" class="planet-overview"></div>
  <div v-if="isLoading" class="loading-hint">載入星球總覽貼圖中…</div>
  <div v-if="loadError" class="load-error">{{ loadError }}</div>
</template>

<style scoped>
/* 與走動視角同樣的理由：100vw 不等於實際可視寬度，改用 inset:0 貼齊視窗，右側才不會露出空白。 */
.planet-overview {
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

.load-error {
  position: fixed;
  bottom: 12px;
  right: 12px;
  padding: 8px 12px;
  background: rgba(140, 20, 20, 0.85);
  color: #fff;
  font-family: monospace;
  border-radius: 4px;
}
</style>
