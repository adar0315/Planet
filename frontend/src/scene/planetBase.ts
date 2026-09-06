import * as THREE from 'three'
import { fetchOverviewTexture } from '@/network/apiClient'
import { determineDayPeriod } from '@/map/solarTime'
import { useGameStateStore } from '@/stores/gameState'
import { useTerrainStyleStore } from '@/stores/terrainStyles'
import { usePlanetInfoStore } from '@/stores/planetInfo'
import { RENDER_RADIUS } from './planetSceneConstants'

export interface PlanetBase {
  readonly mesh: THREE.Mesh
  /** 讓遠景貼圖跟著時段一起明暗變化，貼圖到位前／沒有目前所在格時是no-op。 */
  applyTint(): void
  dispose(): void
}

/**
 * 星球本體。除了填滿Tile之間的縫隙，也負責兩件事：
 *
 * 1. **遠景地形**：載入範圍（半徑約117公里）之外原本是一片純色，
 *    在60度傾斜視角下很明顯——地平線在約193公里外，遠比載入範圍遠。
 *    這裡貼上「星球總覽」用的那張1024×512等距柱狀投影地形貼圖當遠景，
 *    近處仍然是精細的六角格，等於一套簡單的LOD。要真的用格子填到地平線需要約5萬格，
 *    查詢時間與傳輸量都不划算。
 * 2. **海面**：海水地塊的海拔一律是-500級，誇張8倍後在半徑49.46，
 *    比這顆球(49.95)還低，所以海一直都是被這顆球蓋住、由它代表的。
 *    海本來就是平的，用球面表示反而合理，貼上貼圖之後顏色也跟著資料庫的地形樣式走。
 *
 * 遠景貼圖是2MB，不放進 PlanetLoadingGate 一起等——那會拖慢開場。這裡非同步載入，
 * 還沒到位之前遠景就維持原本的純色，到位後才換上。
 *
 * @param scene 貼圖load完成後要用到；mesh 本身仍由呼叫端 `scene.add()`（跟 tileGroup/outlineGroup同一層次）。
 * @param isDisposed 貼圖非同步載入完成時檢查元件是否已卸載，是的話放棄套用（避免對已釋放的場景操作）。
 */
export function createPlanetBase(
  planetInfoStore: ReturnType<typeof usePlanetInfoStore>,
  terrainStyleStore: ReturnType<typeof useTerrainStyleStore>,
  gameState: ReturnType<typeof useGameStateStore>,
  isDisposed: () => boolean,
): PlanetBase {
  const mesh = new THREE.Mesh(
    new THREE.SphereGeometry(RENDER_RADIUS * 0.999, 96, 96),
    new THREE.MeshLambertMaterial({ color: 0x0b2942 }),
  )

  let baseTexture: THREE.DataTexture | null = null

  void fetchOverviewTexture()
    .then((buffer) => {
      if (isDisposed()) return
      const info = planetInfoStore.require()
      baseTexture = new THREE.DataTexture(
        new Uint8Array(buffer),
        info.overviewTextureWidth,
        info.overviewTextureHeight,
        THREE.RGBAFormat,
      )
      // DataTexture 預設 flipY=false（跟一般圖片載入的Texture不同）。貼圖第0列是北極，
      // 而 SphereGeometry 的 uv.y=1 對應北極，不設 true 會南北顛倒。
      baseTexture.flipY = true
      baseTexture.colorSpace = THREE.SRGBColorSpace
      // 經度方向：SphereGeometry 的 u=(經度+180)/360，跟後端產生貼圖的取樣方式完全一致，
      // 不需要額外旋轉（已用「貼圖顏色 vs 該經緯度實際地形色」實測驗證過）。
      baseTexture.needsUpdate = true

      const material = mesh.material as THREE.MeshLambertMaterial
      material.map = baseTexture
      material.color.setHex(0xffffff) // 有貼圖之後底色要設成白色，否則會把貼圖染色
      material.needsUpdate = true
      applyTint()
    })
    .catch(() => {
      /* 拿不到就維持純色遠景，不影響遊戲 */
    })

  /**
   * 讓遠景貼圖跟著時段一起明暗變化。
   *
   * 貼圖用的是各地形的「白天色」，而近處的格子會依時段換色，兩者在載入範圍邊界會有亮度落差。
   * 這裡用資料庫裡某個參考地形的「該時段色 ÷ 白天色」當作倍率去染色，
   * 倍率因此還是來自資料庫的地形樣式，不用在前端再寫死一組明暗參數。
   */
  function applyTint() {
    const material = mesh.material as THREE.MeshLambertMaterial
    if (!material.map || !gameState.currentTile) return

    const period = determineDayPeriod(
      gameState.currentTile.latitude,
      gameState.currentTile.longitude,
      gameState.gameTime,
    )
    const day = terrainStyleStore.getColorHex('Grassland', 'day')
    const now = terrainStyleStore.getColorHex('Grassland', period)
    if (day === 0) return

    const ratio = (shift: number) => {
      const d = (day >> shift) & 0xff
      return d === 0 ? 1 : Math.min(1, ((now >> shift) & 0xff) / d)
    }
    material.color.setRGB(ratio(16), ratio(8), ratio(0))
  }

  function dispose() {
    mesh.geometry.dispose()
    baseTexture?.dispose() // 1024×512的RGBA貼圖，不釋放會一直留在GPU記憶體
    ;(mesh.material as THREE.Material).dispose()
  }

  return { mesh, applyTint, dispose }
}
