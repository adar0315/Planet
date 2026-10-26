import * as THREE from 'three'
import { fetchSubTiles } from '@/network/apiClient'
import { latLonToVector3 } from '@/map/sphereCoordinates'
import { createTilePatch, neighborsOf } from '@/map/tilePatch'
import { buildTilePatchMeshSliced, type TilePatchMesh } from '@/map/tilePatchMesh'
import type { TileMeshOptions } from '@/map/tileMeshBuilder'
import { scaleColorHex, subTileBrightness } from '@/map/subTileShading'
import type { SubTileDto, TileDto } from '@/types/tile'
import type { PatchManager } from './patchManager'
import type { SeasonData } from './seasonData'
import {
  SUBTILE_CAMERA_DISTANCE_METERS,
  SUBTILE_EDGE_METERS,
  SUBTILE_RADIUS_METERS,
  SUBTILE_REFRESH_METERS,
} from './planetSceneConstants'

/** 一次最多細分幾個地塊（後端的上限）。 */
const MAX_PARENTS = 1_500

/**
 * 近看細分（水文地貌規劃 H7，2026-10-24）：鏡頭拉近時，把注視點附近的地塊換成約 250 公尺的小六角——
 * 河道、沙灘、河谷的形狀才看得出來。小格是後端即時產生的（不存資料庫），鄰居也是後端算好的。
 *
 * 換的做法：小格合成一個 mesh 加進場景，被取代的地塊在地塊 mesh 裡藏起來（{@link TilePatchMesh.setTileHidden}）。
 * 小格歸屬離它最近的地塊中心，拼起來的範圍邊界是鋸齒狀的，細分區最外圈會有約半個小格寬的接縫——
 * 那裡離鏡頭最遠，2026-10-24 使用者接受。
 *
 * 角色移動、跨格偵測、伺服器驗證仍然只用地塊，不受影響。
 */
export interface SubTileManager {
  /**
   * 每幀呼叫（很便宜：大部分時候只比一下距離）。
   * @param focus 鏡頭注視點（場景座標）。
   * @param cameraDistanceMeters 鏡頭距離（公尺）。
   */
  update(focus: THREE.Vector3, cameraDistanceMeters: number): void
  /** 遊戲時間變了（時段、季節）：小格重新上色。 */
  recolor(): void
  dispose(): void
}

export function createSubTileManager(
  scene: THREE.Scene,
  meshOptions: TileMeshOptions,
  patchManager: PatchManager,
  seasonData: SeasonData,
  isCancelled: () => boolean,
): SubTileManager {
  const subOptions: TileMeshOptions = { ...meshOptions, edgeLengthMeters: SUBTILE_EDGE_METERS }
  const group = new THREE.Group()
  scene.add(group)

  let subMesh: TilePatchMesh | null = null
  /** 目前這批小格的亮度倍率（依高低與編號，見 subTileShading），重新上色時要再乘一次。 */
  let brightness = new Map<number, number>()
  /** 目前被小格取代、藏起來的地塊。 */
  let hiddenParents = new Set<number>()
  /** 藏地塊時用的是哪一個地塊 mesh：patch 換掉之後新的 mesh 沒有藏，要重新藏一次。 */
  let hiddenIn: TilePatchMesh | null = null
  let lastCenter: THREE.Vector3 | null = null
  let loading = false
  let generation = 0

  const focusUnit = new THREE.Vector3()

  function shadedColor(tile: TileDto, factors: ReadonlyMap<number, number>): number {
    return scaleColorHex(patchManager.colorFor(tile, true), factors.get(tile.tileId) ?? 1)
  }

  function setParentsHidden(ids: Iterable<number>, hidden: boolean) {
    const mesh = patchManager.patchMesh
    if (!mesh) return
    for (const id of ids) {
      const index = mesh.indexOfTile(id)
      if (index !== undefined) mesh.setTileHidden(index, hidden)
    }
  }

  function clear() {
    generation++ // 還在路上的那一批作廢
    if (subMesh) {
      group.remove(subMesh.mesh)
      subMesh.dispose()
      subMesh = null
    }
    if (hiddenIn === patchManager.patchMesh) setParentsHidden(hiddenParents, false)
    hiddenParents = new Set()
    hiddenIn = null
    lastCenter = null
  }

  async function load(center: THREE.Vector3) {
    const patch = patchManager.patch
    if (!patch) return
    const myGeneration = ++generation
    loading = true
    try {
      // 注視點附近的地塊，由近到遠，最多 MAX_PARENTS 個
      const radiusRad = SUBTILE_RADIUS_METERS / meshOptions.planetRadiusMeters
      const minDot = Math.cos(radiusRad)
      const candidates: { tile: TileDto; dot: number }[] = []
      for (const tile of patch.tiles) {
        const dot = latLonToVector3(tile.latitude, tile.longitude, 1).dot(center)
        if (dot >= minDot) candidates.push({ tile, dot })
      }
      candidates.sort((a, b) => b.dot - a.dot)
      const parents = candidates.slice(0, MAX_PARENTS).map((c) => c.tile.tileId)
      if (parents.length === 0) return

      const subs = await fetchSubTiles(parents)
      if (myGeneration !== generation) return
      await seasonData.ensure(subs)
      if (myGeneration !== generation) return

      const subPatch = createTilePatch(subs)
      const nextBrightness = new Map<number, number>()
      for (const sub of subs)
        nextBrightness.set(sub.tileId, subTileBrightness(sub, neighborsOf(subPatch, sub.tileId)))
      const built = await buildTilePatchMeshSliced(
        subs,
        subOptions,
        (tile) => shadedColor(tile, nextBrightness),
        (tile) => neighborsOf(subPatch, tile.tileId),
        {
          isCancelled: () => isCancelled() || myGeneration !== generation,
          slopeShading: true,
          edgeLines: true,
        },
      )
      if (!built || myGeneration !== generation) return
      brightness = nextBrightness

      // 換上去：舊的小格拿掉、新的放上；藏的地塊換成新的這一批
      if (subMesh) {
        group.remove(subMesh.mesh)
        subMesh.dispose()
      }
      subMesh = built
      group.add(built.mesh)
      const nextHidden = new Set(subs.map((s: SubTileDto) => s.parentTileId))
      if (hiddenIn === patchManager.patchMesh) {
        setParentsHidden([...hiddenParents].filter((id) => !nextHidden.has(id)), false)
      }
      setParentsHidden(nextHidden, true)
      hiddenParents = nextHidden
      hiddenIn = patchManager.patchMesh
      lastCenter = center.clone()
    } catch {
      // 抓不到就維持現狀，下次移動再試
    } finally {
      if (myGeneration === generation) loading = false
    }
  }

  return {
    update(focus, cameraDistanceMeters) {
      if (cameraDistanceMeters >= SUBTILE_CAMERA_DISTANCE_METERS || !patchManager.patch) {
        if (subMesh || loading) {
          clear()
          loading = false
        }
        return
      }
      // 地塊 patch 換了（走遠補載）：新的 mesh 沒有藏，重新藏一次
      if (subMesh && hiddenIn !== patchManager.patchMesh) {
        setParentsHidden(hiddenParents, true)
        hiddenIn = patchManager.patchMesh
      }
      if (loading) return
      focusUnit.copy(focus).normalize()
      if (lastCenter && focusUnit.angleTo(lastCenter) * meshOptions.planetRadiusMeters < SUBTILE_REFRESH_METERS) return
      void load(focusUnit.clone())
    },

    recolor() {
      subMesh?.recolorAll((tile) => shadedColor(tile, brightness))
    },

    dispose() {
      clear()
      scene.remove(group)
    },
  }
}
