import * as THREE from 'three'
import { buildTileOutline, type TileMeshOptions } from '@/map/tileMeshBuilder'
import { neighborsOf } from '@/map/tilePatch'
import { CLICK_MAX_MOVE_PX } from '@/map/overviewPicking'
import { useTileInspectorStore } from '@/stores/tileInspector'
import type { SubTileDto, TileDto } from '@/types/tile'
import type { PatchManager } from './patchManager'
import type { SubTileManager } from './subTileManager'

export interface TileInspector {
  /** 每幀呼叫：面板關掉、或進了編輯模式時拿掉外框。 */
  tick(): void
  dispose(): void
}

/** 被點選的方塊的外框顏色：跟鄰居外框（黃）、編輯模式的外框分得開。 */
const SELECTED_OUTLINE = new THREE.LineBasicMaterial({ color: 0x4dd8ff, linewidth: 2, depthTest: false })

/**
 * 走動視角的「點一下看方塊資訊」（2026-11-17）：左鍵按下到放開沒怎麼動（{@link CLICK_MAX_MOVE_PX}）就算點選，
 * 動了就是環顧（cameraRig 照舊處理），兩者不衝突。
 *
 * 先挑近看的小格（鏡頭附近、看得到細格子的那一圈），沒有才挑地塊；點到天空就關掉面板。
 * 編輯模式時左鍵是蓋東西，這裡不處理。
 */
export function createTileInspector(
  canvas: HTMLCanvasElement,
  camera: THREE.Camera,
  scene: THREE.Scene,
  patchManager: PatchManager,
  subTiles: SubTileManager,
  meshOptions: TileMeshOptions,
  isEditing: () => boolean,
): TileInspector {
  const store = useTileInspectorStore()
  const raycaster = new THREE.Raycaster()
  const pointer = new THREE.Vector2()
  let down: { id: number; x: number; y: number } | null = null

  let outline: THREE.LineLoop | null = null
  let outlined: TileDto | null = null

  function setOutline(tile: TileDto | null, subTile = false) {
    if (tile === outlined) return
    if (outline) {
      scene.remove(outline)
      outline.geometry.dispose() // 材質是共用的，不釋放
      outline = null
    }
    outlined = tile
    if (!tile) return
    const patch = patchManager.patch
    outline = subTile
      ? buildTileOutline(tile, subTiles.subOptions, subTiles.neighboursOf(tile as SubTileDto))
      : buildTileOutline(tile, meshOptions, patch ? neighborsOf(patch, tile.tileId) : undefined)
    outline.material = SELECTED_OUTLINE
    scene.add(outline)
  }

  function pickTile(): TileDto | null {
    const patch = patchManager.patch
    const patchMesh = patchManager.patchMesh
    if (!patch || !patchMesh) return null
    const hit = raycaster.intersectObject(patchMesh.mesh, false)[0]
    if (!hit || hit.faceIndex == null) return null
    const id = patchMesh.tileIdAtFace(hit.faceIndex)
    if (id === undefined) return null
    const index = patch.indexByTileId.get(id)
    return index === undefined ? null : (patch.tiles[index] ?? null)
  }

  function pickAt(clientX: number, clientY: number) {
    const rect = canvas.getBoundingClientRect()
    pointer.set(((clientX - rect.left) / rect.width) * 2 - 1, -((clientY - rect.top) / rect.height) * 2 + 1)
    raycaster.setFromCamera(pointer, camera)
    const sub = subTiles.ready ? subTiles.pick(raycaster) : null
    if (sub) {
      store.select(sub, true)
      setOutline(sub, true)
      return
    }
    const tile = pickTile()
    if (tile) {
      store.select(tile, false)
      setOutline(tile, false)
      return
    }
    store.clear()
  }

  function onPointerDown(event: PointerEvent) {
    if (event.button !== 0 || isEditing()) return
    down = { id: event.pointerId, x: event.clientX, y: event.clientY }
  }

  function onPointerUp(event: PointerEvent) {
    const start = down
    down = null
    if (!start || event.button !== 0 || event.pointerId !== start.id || isEditing()) return
    if (Math.hypot(event.clientX - start.x, event.clientY - start.y) > CLICK_MAX_MOVE_PX) return // 拖曳＝環顧
    pickAt(event.clientX, event.clientY)
  }

  function onPointerCancel() {
    down = null
  }

  canvas.addEventListener('pointerdown', onPointerDown)
  canvas.addEventListener('pointerup', onPointerUp)
  canvas.addEventListener('pointercancel', onPointerCancel)

  return {
    tick() {
      if (isEditing() && store.selected) store.clear()
      if (!store.selected && outlined) setOutline(null)
    },
    dispose() {
      setOutline(null)
      store.clear()
      canvas.removeEventListener('pointerdown', onPointerDown)
      canvas.removeEventListener('pointerup', onPointerUp)
      canvas.removeEventListener('pointercancel', onPointerCancel)
    },
  }
}
