import * as THREE from 'three'
import { postConstruction } from '@/network/apiClient'
import { buildTileOutline } from '@/map/tileMeshBuilder'
import { CLICK_MAX_MOVE_PX } from '@/map/overviewPicking'
import { EDIT_TOOLS, useEditModeStore } from '@/stores/editMode'
import type { SubTileDto } from '@/types/tile'
import type { SubTileManager } from './subTileManager'

export interface EditController {
  /** 每幀呼叫：更新「小格好了沒」與滑鼠底下那一格的外框。 */
  tick(): void
  dispose(): void
}

/**
 * 編輯模式的滑鼠操作（2026-11-08）：滑鼠底下的小格畫外框，點一下就用目前的工具修改它。
 *
 * 左鍵拖曳仍然是環顧（cameraRig），所以「點一下」要分辨：按下到放開移動不超過 {@link CLICK_MAX_MOVE_PX} 才算，
 * 跟星球總覽選起點的做法一樣。只能改小格（鏡頭拉近到細分距離以內），角色移動不受影響。
 */
export function createEditController(
  canvas: HTMLCanvasElement,
  camera: THREE.Camera,
  scene: THREE.Scene,
  subTiles: SubTileManager,
): EditController {
  const edit = useEditModeStore()
  const raycaster = new THREE.Raycaster()
  const pointer = new THREE.Vector2()
  let pointerInside = false
  let hoverDirty = false
  let downAt: { x: number; y: number } | null = null

  let outline: THREE.LineLoop | null = null
  let outlined: SubTileDto | null = null

  function setPointer(event: PointerEvent) {
    const rect = canvas.getBoundingClientRect()
    pointer.set(
      ((event.clientX - rect.left) / rect.width) * 2 - 1,
      -((event.clientY - rect.top) / rect.height) * 2 + 1,
    )
  }

  function pickUnderPointer(): SubTileDto | null {
    if (!pointerInside) return null
    raycaster.setFromCamera(pointer, camera)
    return subTiles.pick(raycaster)
  }

  function setOutline(sub: SubTileDto | null) {
    if (sub === outlined) return
    if (outline) {
      scene.remove(outline)
      outline.geometry.dispose() // 材質是共用的，不釋放
      outline = null
    }
    outlined = sub
    if (!sub) return
    outline = buildTileOutline(sub, subTiles.subOptions, subTiles.neighboursOf(sub))
    scene.add(outline)
  }

  async function apply(sub: SubTileDto) {
    if (edit.busy) return
    const action = edit.tool
    const label = EDIT_TOOLS.find((t) => t.action === action)?.label ?? action
    edit.busy = true
    try {
      const result = await postConstruction(sub.tileId, sub.parentTileId, action)
      subTiles.applyEdit(
        result.subTileId,
        result.structure ?? undefined,
        result.terrainEdit ?? undefined,
        result.terrainChanged,
      )
      edit.showMessage(
        action === 'Demolish'
          ? '已拆除。'
          : result.terrainChanged
            ? `${label}完成，地形更新中…`
            : `${label}完成。`,
      )
      hoverDirty = true
    } catch (err) {
      edit.showMessage(err instanceof Error ? err.message : String(err), true)
    } finally {
      edit.busy = false
    }
  }

  function onPointerMove(event: PointerEvent) {
    pointerInside = true
    setPointer(event)
    hoverDirty = true
  }

  function onPointerLeave() {
    pointerInside = false
    hoverDirty = true
  }

  function onPointerDown(event: PointerEvent) {
    downAt = event.button === 0 ? { x: event.clientX, y: event.clientY } : null
  }

  function onPointerUp(event: PointerEvent) {
    const start = downAt
    downAt = null
    if (!start || event.button !== 0 || !edit.active) return
    if (Math.hypot(event.clientX - start.x, event.clientY - start.y) > CLICK_MAX_MOVE_PX) return
    if (!subTiles.ready) {
      edit.showMessage('鏡頭拉近一點（看得到小格時）才能編輯。', true)
      return
    }
    setPointer(event)
    pointerInside = true
    const sub = pickUnderPointer()
    if (!sub) {
      edit.showMessage('要點在小格上（鏡頭附近一圈看得到細格子的地方）。', true)
      return
    }
    void apply(sub)
  }

  canvas.addEventListener('pointermove', onPointerMove)
  canvas.addEventListener('pointerleave', onPointerLeave)
  canvas.addEventListener('pointerdown', onPointerDown)
  canvas.addEventListener('pointerup', onPointerUp)

  /**
   * 小格 mesh 有幾十萬個三角形，射線檢查不便宜：滑鼠動了才馬上重挑，否則（鏡頭在動、小格重新載入）每隔一段時間挑一次。
   */
  const REPICK_INTERVAL_MS = 150
  let lastPickAt = 0
  return {
    tick() {
      const ready = subTiles.ready
      if (ready !== edit.subTilesReady) edit.subTilesReady = ready
      if (!edit.active || !ready) {
        setOutline(null)
        return
      }
      const now = performance.now()
      if (!hoverDirty && now - lastPickAt < REPICK_INTERVAL_MS) return
      hoverDirty = false
      lastPickAt = now
      setOutline(pickUnderPointer())
    },
    dispose() {
      setOutline(null)
      canvas.removeEventListener('pointermove', onPointerMove)
      canvas.removeEventListener('pointerleave', onPointerLeave)
      canvas.removeEventListener('pointerdown', onPointerDown)
      canvas.removeEventListener('pointerup', onPointerUp)
      edit.subTilesReady = false
    },
  }
}
