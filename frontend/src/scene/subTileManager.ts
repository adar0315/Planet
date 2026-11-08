import * as THREE from 'three'
import { fetchSubTiles } from '@/network/apiClient'
import { latLonToVector3 } from '@/map/sphereCoordinates'
import { createTilePatch, neighborsOf, type TilePatch } from '@/map/tilePatch'
import {
  buildTilePatchMeshSliced,
  EDGE_LINES_NAME,
  hasWaterSurface,
  LAKE_MESH_NAME,
  type TilePatchMesh,
} from '@/map/tilePatchMesh'
import { tileSurfaceRadius, type TileMeshOptions } from '@/map/tileMeshBuilder'
import { scaleColorHex, subTileBrightness } from '@/map/subTileShading'
import { withSphereClip, type SphereClip } from '@/map/sphereClipMaterial'
import type { StructureKind, SubTileDto, TerrainEditKind, TileDto } from '@/types/tile'
import { buildConstructionLayer, type ConstructionLayer } from '@/map/constructionModels'
import type { PatchManager } from './patchManager'
import type { SeasonData } from './seasonData'
import {
  SUBTILE_CAMERA_DISTANCE_METERS,
  SUBTILE_EDGE_METERS,
  SUBTILE_RADIUS_METERS,
  SUBTILE_REFRESH_METERS,
} from './planetSceneConstants'

/**
 * 一次最多細分幾個地塊（後端 SubTileService.MaxParentsPerRequest）。半徑 36 公里約 1,900 個。
 * 一定要一次要完：分批的話，批次交界的小格查不到鄰居，會退回近似畫法而互相重疊。
 */
const MAX_PARENTS = 2_500

/**
 * 地塊比小格的圓往內多畫這麼多公尺（約兩個小格）。兩邊在這一圈重疊：高的蓋住低的，
 * 邊界上高低差的地方看進去是另一邊的頂面，不會看穿到底下。
 */
const CLIP_OVERLAP_METERS = 600

/**
 * 近看細分（水文地貌規劃 H7，2026-10-24）：鏡頭拉近時，把注視點附近的地塊換成約 250 公尺的小六角——
 * 河道、沙灘、河谷的形狀才看得出來。小格是後端即時產生的（不存資料庫），鄰居也是後端算好的。
 *
 * 換的做法：小格合成一個 mesh 加進場景。邊界是注視點周圍的一個圓（2026-11-07，原本是被取代地塊的鋸齒狀外緣，
 * 小格跟地塊的六角交錯，留下一圈半個小格寬的縫）：小格只畫圓內，地塊只畫「圓往內縮 {@link CLIP_OVERLAP_METERS}」以外，
 * 兩邊重疊一圈，誰高看到誰，縫裡一定有東西（見 sphereClipMaterial）。整格都在圓內的地塊直接藏起來（{@link TilePatchMesh.setTileHidden}），省得畫。
 *
 * 角色移動、跨格偵測、伺服器驗證仍然只用地塊，不受影響；只有站立高度改看腳下的小格（{@link SubTileManager.surfaceRadiusAt}）。
 */
export interface SubTileManager {
  /**
   * 每幀呼叫（很便宜：大部分時候只比一下距離）。
   * @param focus 鏡頭注視點（場景座標）。
   * @param cameraDistanceMeters 鏡頭距離（公尺）。
   */
  update(focus: THREE.Vector3, cameraDistanceMeters: number): void
  /**
   * 腳下小格頂面的場景半徑：畫面上看到的是小格，角色要站在它上面，不是站在藏起來的地塊頂面上——
   * 河谷旁地塊是一整格的高度，小格是谷底或谷壁，差上百公尺，角色看起來浮在半空（2026-11-07 使用者回報）。
   * 這一格地塊沒有被小格取代時回傳 null（用地塊的高度）。
   * @param unit 位置（單位向量）。
   * @param tile 所在的地塊。
   */
  surfaceRadiusAt(unit: THREE.Vector3, tile: TileDto): number | null
  /** 遊戲時間變了（時段、季節）：小格重新上色。 */
  recolor(): void
  /** 小格載好了（鏡頭夠近）：編輯模式只能在這時候用。 */
  readonly ready: boolean
  /** 小格 mesh 用的尺寸換算（編輯模式畫外框用）。 */
  readonly subOptions: TileMeshOptions
  /**
   * 射線打到的小格；只算裁切圓裡面（畫面上看得到小格的地方），圓外是地塊、回傳 null。
   * @param raycaster 已經設好方向的射線。
   */
  pick(raycaster: THREE.Raycaster): SubTileDto | null
  /** 同一批裡的鄰居。 */
  neighboursOf(sub: SubTileDto): SubTileDto[]
  /**
   * 編輯模式改了一個小格：建築物變了就重畫建築物，地形變了就重新向後端要一次小格（高度、顏色、水面都要重算）。
   */
  applyEdit(subTileId: number, structure: StructureKind | undefined, terrainEdit: TerrainEditKind | undefined, terrainChanged: boolean): void
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
  /** 地塊的邊長＝外接圓半徑（公尺）。 */
  const parentEdgeMeters = meshOptions.edgeLengthMeters ?? 1000
  const group = new THREE.Group()
  scene.add(group)

  let subMesh: TilePatchMesh | null = null
  /** 目前這批小格（編輯模式查鄰居、改建築物用）。 */
  let subPatch: TilePatch | null = null
  /** 這批小格上的建築物。 */
  let constructions: ConstructionLayer | null = null
  /** 地形改過了，下一次 update 要重新要一批（就算正在載入也要：那一批可能是改之前要的）。 */
  let refreshRequested = false
  /** 目前這批小格的亮度倍率（依高低與編號，見 subTileShading），重新上色時要再乘一次。 */
  let brightness = new Map<number, number>()
  /** 目前這批小格依地塊分組，格心是單位向量（站立高度用）。 */
  let subsByParent = new Map<number, { sub: SubTileDto; center: THREE.Vector3 }[]>()
  /** 目前被小格取代、藏起來的地塊。 */
  let hiddenParents = new Set<number>()
  /** 藏地塊時用的是哪一個地塊 mesh：patch 換掉之後新的 mesh 沒有藏，要重新藏一次。 */
  let hiddenIn: TilePatchMesh | null = null
  let lastCenter: THREE.Vector3 | null = null
  /** 加上圓形裁切的材質：小格的頂面、小格的邊線、地塊。第一次用到時從原本的材質複製。 */
  let subFill: { material: THREE.Material; clip: SphereClip } | null = null
  let subEdges: { material: THREE.Material; clip: SphereClip } | null = null
  let parentFill: { material: THREE.Material; clip: SphereClip } | null = null
  /** 湖面（半透明）：兩邊都切在同一個圓上，不重疊——重疊的那圈會疊兩層變深。 */
  let subLakes: { material: THREE.Material; clip: SphereClip } | null = null
  let parentLakes: { material: THREE.Material; clip: SphereClip } | null = null
  /** 目前的裁切圓：圓心（單位向量）、小格的半徑（弧度）。 */
  let clipCenter = new THREE.Vector3()
  let clipAngle = 0
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

  /** 地塊 mesh 換上「只畫圓外」的材質（patch 換了新的 mesh 時要再換一次）。 */
  function applyParentClip() {
    const mesh = patchManager.patchMesh?.mesh
    if (!mesh) return
    parentFill ??= withSphereClip(mesh.material as THREE.Material, 'outside')
    mesh.material = parentFill.material
    parentFill.clip.center.copy(clipCenter)
    parentFill.clip.setAngle(clipAngle - CLIP_OVERLAP_METERS / meshOptions.planetRadiusMeters)
    parentFill.clip.setEnabled(true)
    const lakes = mesh.getObjectByName(LAKE_MESH_NAME) as THREE.Mesh | undefined
    if (lakes) {
      parentLakes ??= withSphereClip(lakes.material as THREE.Material, 'outside')
      lakes.material = parentLakes.material
    }
    if (parentLakes) {
      parentLakes.clip.center.copy(clipCenter)
      parentLakes.clip.setAngle(clipAngle)
      parentLakes.clip.setEnabled(true)
    }
  }

  /** 小格 mesh（與它的邊線）換上「只畫圓內」的材質。 */
  function applySubClip(mesh: THREE.Mesh) {
    subFill ??= withSphereClip(mesh.material as THREE.Material, 'inside')
    mesh.material = subFill.material
    const lines = mesh.getObjectByName(EDGE_LINES_NAME) as THREE.LineSegments | undefined
    if (lines) {
      subEdges ??= withSphereClip(lines.material as THREE.Material, 'inside')
      lines.material = subEdges.material
    }
    const lakes = mesh.getObjectByName(LAKE_MESH_NAME) as THREE.Mesh | undefined
    if (lakes) {
      subLakes ??= withSphereClip(lakes.material as THREE.Material, 'inside')
      lakes.material = subLakes.material
    }
    for (const fill of [subFill, subEdges, subLakes]) {
      if (!fill) continue
      fill.clip.center.copy(clipCenter)
      fill.clip.setAngle(clipAngle)
      fill.clip.setEnabled(true)
    }
  }

  function subNeighbours(sub: SubTileDto): SubTileDto[] {
    return subPatch ? (neighborsOf(subPatch, sub.tileId) as SubTileDto[]) : []
  }

  /** 射線在 `before` 之前打到水面的話，回傳那一點。 */
  function waterHitBefore(raycaster: THREE.Raycaster, before: number): THREE.Vector3 | null {
    const lakes = subMesh?.mesh.getObjectByName(LAKE_MESH_NAME) as THREE.Mesh | undefined
    if (!lakes) return null
    const hits: THREE.Intersection[] = []
    // 水面 mesh 把 raycast 關掉了（平常點選要點到水底那一格），這裡直接呼叫原本的實作
    THREE.Mesh.prototype.raycast.call(lakes, raycaster, hits)
    let nearest: THREE.Intersection | null = null
    for (const h of hits) if (h.distance < before && (!nearest || h.distance < nearest.distance)) nearest = h
    return nearest ? nearest.point : null
  }

  function rebuildConstructions() {
    if (constructions) {
      group.remove(constructions.group)
      constructions.dispose()
      constructions = null
    }
    if (!subPatch) return
    const built = subPatch.tiles as SubTileDto[]
    if (!built.some((sub) => sub.structure)) return
    constructions = buildConstructionLayer(built, subNeighbours, subOptions)
    group.add(constructions.group)
  }

  function clear() {
    generation++ // 還在路上的那一批作廢
    subPatch = null
    rebuildConstructions()
    if (subMesh) {
      group.remove(subMesh.mesh)
      subMesh.dispose()
      subMesh = null
    }
    if (hiddenIn === patchManager.patchMesh) setParentsHidden(hiddenParents, false)
    hiddenParents = new Set()
    parentFill?.clip.setEnabled(false)
    parentLakes?.clip.setEnabled(false)
    subsByParent = new Map()
    hiddenIn = null
    lastCenter = null
  }

  async function load(center: THREE.Vector3) {
    const patch = patchManager.patch
    if (!patch) return
    const myGeneration = ++generation
    loading = true
    try {
      // 圓內的每一點都要有小格：小格可能歸屬到圓外一個地塊外接圓＋一個小格以內的地塊，這些都要。由近到遠，最多 MAX_PARENTS 個
      const planetRadius = meshOptions.planetRadiusMeters
      const parentReach = parentEdgeMeters + SUBTILE_EDGE_METERS
      const candidates: { tile: TileDto; angle: number }[] = []
      for (const tile of patch.tiles) {
        const angle = latLonToVector3(tile.latitude, tile.longitude, 1).angleTo(center)
        if (angle * planetRadius <= SUBTILE_RADIUS_METERS + parentReach)
          candidates.push({ tile, angle })
      }
      candidates.sort((a, b) => a.angle - b.angle)
      const chosen = candidates.slice(0, MAX_PARENTS)
      if (chosen.length === 0) return
      const parents = chosen.map((c) => c.tile.tileId)
      // 超過上限的話，圓縮到第一個沒要到的地塊碰不到的地方
      const excluded = candidates[MAX_PARENTS]
      const radiusMeters = excluded
        ? Math.min(SUBTILE_RADIUS_METERS, excluded.angle * planetRadius - parentReach)
        : SUBTILE_RADIUS_METERS
      // 整格都在地塊的裁切圓內（看不到）的地塊藏起來
      const hideWithinMeters = radiusMeters - CLIP_OVERLAP_METERS - parentEdgeMeters
      const nextHidden = new Set(
        chosen.filter((c) => c.angle * planetRadius <= hideWithinMeters).map((c) => c.tile.tileId),
      )

      const subs = await fetchSubTiles(parents)
      if (myGeneration !== generation) return
      await seasonData.ensure(subs)
      if (myGeneration !== generation) return

      const nextPatch = createTilePatch(subs)
      const nextBrightness = new Map<number, number>()
      for (const sub of subs)
        nextBrightness.set(sub.tileId, subTileBrightness(sub, neighborsOf(nextPatch, sub.tileId)))
      const built = await buildTilePatchMeshSliced(
        subs,
        subOptions,
        (tile) => shadedColor(tile, nextBrightness),
        (tile) => neighborsOf(nextPatch, tile.tileId),
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
      subPatch = nextPatch
      rebuildConstructions()
      clipCenter = center.clone()
      clipAngle = radiusMeters / planetRadius
      applySubClip(built.mesh)
      applyParentClip()
      group.add(built.mesh)
      if (hiddenIn === patchManager.patchMesh) {
        setParentsHidden(
          [...hiddenParents].filter((id) => !nextHidden.has(id)),
          false,
        )
      }
      setParentsHidden(nextHidden, true)
      hiddenParents = nextHidden
      hiddenIn = patchManager.patchMesh
      const byParent = new Map<number, { sub: SubTileDto; center: THREE.Vector3 }[]>()
      for (const sub of subs) {
        let list = byParent.get(sub.parentTileId)
        if (!list) byParent.set(sub.parentTileId, (list = []))
        list.push({ sub, center: latLonToVector3(sub.latitude, sub.longitude, 1) })
      }
      subsByParent = byParent
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
        applyParentClip()
        setParentsHidden(hiddenParents, true)
        hiddenIn = patchManager.patchMesh
      }
      if (loading) return
      focusUnit.copy(focus).normalize()
      if (refreshRequested && lastCenter) {
        refreshRequested = false
        void load(focusUnit.clone())
        return
      }
      if (
        lastCenter &&
        focusUnit.angleTo(lastCenter) * meshOptions.planetRadiusMeters < SUBTILE_REFRESH_METERS
      )
        return
      void load(focusUnit.clone())
    },

    surfaceRadiusAt(unit, tile) {
      if (
        !subMesh ||
        unit.angleTo(clipCenter) >= clipAngle - CLIP_OVERLAP_METERS / meshOptions.planetRadiusMeters
      )
        return null
      // 小格歸屬離它最近的地塊中心，所以腳下那一格在這個地塊或它的鄰居底下
      const patch = patchManager.patch
      const parents = [
        tile.tileId,
        ...(patch ? neighborsOf(patch, tile.tileId).map((t) => t.tileId) : []),
      ]
      let best: SubTileDto | null = null
      let bestDot = -Infinity
      for (const id of parents) {
        for (const { sub, center } of subsByParent.get(id) ?? []) {
          const dot = center.dot(unit)
          if (dot > bestDot) {
            bestDot = dot
            best = sub
          }
        }
      }
      return best ? tileSurfaceRadius(best, subOptions) : null
    },

    recolor() {
      subMesh?.recolorAll((tile) => shadedColor(tile, brightness))
    },

    get ready() {
      return subMesh !== null && subPatch !== null
    },

    subOptions,

    pick(raycaster) {
      if (!subMesh || !subPatch) return null
      const hit = raycaster.intersectObject(subMesh.mesh, false)[0]
      if (!hit || hit.faceIndex == null) return null
      if (hit.point.clone().normalize().angleTo(clipCenter) >= clipAngle) return null
      const id = subMesh.tileIdAtFace(hit.faceIndex)
      if (id === undefined) return null
      const index = subPatch.indexByTileId.get(id)
      if (index === undefined) return null
      const owner = subPatch.tiles[index] as SubTileDto
      // 水面是半透明的、本身不接受點選：射線穿過水面打到水底，或穿過凹下去的河道打到對岸的側壁
      // （傾斜視角下 160 公尺深的河道會偏掉超過半格）。先看水面在不在地形前面，在的話選水面底下那一格
      const water = waterHitBefore(raycaster, hit.distance)
      if (!water) return owner
      const unit = water.clone().normalize()
      let best: SubTileDto | null = null
      let bestDot = -Infinity
      for (const candidate of [owner, ...subNeighbours(owner)]) {
        if (!hasWaterSurface(candidate)) continue
        const dot = latLonToVector3(candidate.latitude, candidate.longitude, 1).dot(unit)
        if (dot > bestDot) {
          bestDot = dot
          best = candidate
        }
      }
      return best ?? owner
    },

    neighboursOf: subNeighbours,

    applyEdit(subTileId, structure, terrainEdit, terrainChanged) {
      const index = subPatch?.indexByTileId.get(subTileId)
      const sub = index === undefined ? undefined : (subPatch!.tiles[index] as SubTileDto)
      if (sub) {
        sub.structure = structure
        sub.terrainEdit = terrainEdit
      }
      if (terrainChanged) refreshRequested = true
      else rebuildConstructions()
    },

    dispose() {
      clear()
      scene.remove(group)
    },
  }
}
