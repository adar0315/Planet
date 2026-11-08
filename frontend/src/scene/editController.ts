import * as THREE from 'three'
import { postConstruction } from '@/network/apiClient'
import { buildTileOutline } from '@/map/tileMeshBuilder'
import { latLonToVector3 } from '@/map/sphereCoordinates'
import { EDIT_TOOLS, useEditModeStore } from '@/stores/editMode'
import type { ConstructionAction, SubTileDto } from '@/types/tile'
import type { SubTileManager } from './subTileManager'

export interface EditController {
  /** 每幀呼叫：更新「小格好了沒」與滑鼠底下那一格的外框。 */
  tick(): void
  dispose(): void
}

/** 拖曳時兩次取樣之間跳過的格子，最多補幾格（滑鼠一幀移動很遠也補得上，又不會失控）。 */
const MAX_FILL_STEPS = 40

/**
 * 編輯模式的滑鼠操作（2026-11-08）：滑鼠底下的小格畫外框，左鍵按下去就用目前的工具修改它，
 * 按著拖曳經過的每一格都修改（連續畫路、一整片農田、一路挖河道）。編輯模式時左鍵不環顧（cameraRig），
 * 轉鏡頭用右鍵。
 *
 * 滑鼠移動很快時，兩次取樣之間會跳過幾格：沿著小格的鄰居一步一步往新的那一格走，把中間的格子補上，
 * 路才不會斷。修改依序送往後端（一次一格），同一筆畫裡同一格只送一次。
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

  let outline: THREE.LineLoop | null = null
  let outlined: SubTileDto | null = null

  interface Stroke {
    pointerId: number
    action: ConstructionAction
    visited: Set<number>
    last: SubTileDto | null
    done: number
    failed: number
    lastError: string | null
    /** 有沒有哪一格的地形變了（要重新載入小格）。 */
    terrainChanged: boolean
  }
  /** 目前這一筆（按下到放開）。 */
  let stroke: Stroke | null = null
  /** 待送的格子（依序送）。記著屬於哪一筆：放開時還在路上的格子也要算進那一筆。 */
  const queue: { sub: SubTileDto; owner: Stroke }[] = []
  let sending = false

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

  /** 從 `from` 沿鄰居一步一步走到 `to`（每步挑離目標最近的鄰居），回傳經過的格子（不含 from、含 to）。 */
  function pathBetween(from: SubTileDto, to: SubTileDto): SubTileDto[] {
    const target = latLonToVector3(to.latitude, to.longitude, 1)
    const path: SubTileDto[] = []
    let current = from
    for (let step = 0; step < MAX_FILL_STEPS && current.tileId !== to.tileId; step++) {
      let best: SubTileDto | null = null
      let bestDot = latLonToVector3(current.latitude, current.longitude, 1).dot(target)
      for (const n of subTiles.neighboursOf(current)) {
        const dot = latLonToVector3(n.latitude, n.longitude, 1).dot(target)
        if (dot > bestDot) {
          bestDot = dot
          best = n
        }
      }
      if (!best) break // 走不過去（鄰居不在這一批裡）：只畫目標那一格
      current = best
      path.push(current)
    }
    if (current.tileId !== to.tileId) path.push(to)
    return path
  }

  /** 已經是這個樣子了就不必送（拖曳經過已經有路的格子很常見，送了只會收到一堆錯誤）。 */
  function alreadyDone(sub: SubTileDto, action: ConstructionAction): boolean {
    if (action === 'Demolish') return !sub.structure && !sub.terrainEdit
    if (action === 'Channel' || action === 'Fill') return sub.terrainEdit === action
    return sub.structure === action
  }

  function enqueue(sub: SubTileDto) {
    if (!stroke) return
    const cells = stroke.last ? pathBetween(stroke.last, sub) : [sub]
    stroke.last = sub
    for (const cell of cells) {
      if (stroke.visited.has(cell.tileId)) continue
      stroke.visited.add(cell.tileId)
      if (alreadyDone(cell, stroke.action)) continue
      queue.push({ sub: cell, owner: stroke })
    }
    void drain()
  }

  async function drain() {
    if (sending) return
    sending = true
    edit.busy = true
    try {
      while (queue.length > 0) {
        const { sub, owner } = queue.shift()!
        try {
          const result = await postConstruction(sub.tileId, sub.parentTileId, owner.action)
          subTiles.applyEdit(
            result.subTileId,
            result.structure ?? undefined,
            result.terrainEdit ?? undefined,
            result.terrainChanged,
          )
          owner.done++
          if (result.terrainChanged) owner.terrainChanged = true
        } catch (err) {
          owner.failed++
          owner.lastError = err instanceof Error ? err.message : String(err)
        }
      }
    } finally {
      sending = false
      edit.busy = false
      hoverDirty = true
    }
  }

  function report(action: ConstructionAction, done: number, failed: number, error: string | null, terrain: boolean) {
    const label = EDIT_TOOLS.find((t) => t.action === action)?.label ?? action
    if (done === 0 && failed === 0) {
      edit.showMessage('這裡已經是這樣了。')
      return
    }
    if (done === 0) {
      edit.showMessage(error ?? '修改失敗。', true)
      return
    }
    const what = action === 'Demolish' ? `已拆除 ${done} 格` : `${label}完成 ${done} 格`
    const suffix = terrain ? '，地形更新中…' : '。'
    edit.showMessage(failed > 0 ? `${what}；${failed} 格不行：${error}` : what + suffix, failed > 0)
  }

  async function finishStroke(event?: PointerEvent) {
    const finished = stroke
    if (!finished) return
    // 放開那一刻滑鼠底下的格子：拖曳中的格子是在 tick 裡挑的，最後一段可能還沒挑到
    if (event) {
      setPointer(event)
      const sub = pickUnderPointer()
      if (sub && sub.tileId !== finished.last?.tileId) enqueue(sub)
    }
    stroke = null
    canvas.releasePointerCapture?.(finished.pointerId)
    // 等這一筆送完再結算、再讓地形重載
    while (sending || queue.length > 0) await new Promise((r) => setTimeout(r, 30))
    subTiles.holdRefresh(false)
    report(finished.action, finished.done, finished.failed, finished.lastError, finished.terrainChanged)
  }

  function onPointerMove(event: PointerEvent) {
    pointerInside = true
    setPointer(event)
    hoverDirty = true // 拖曳中的格子在 tick 裡挑：滑鼠事件一秒可能上百次，射線一幀挑一次就夠
  }

  function onPointerLeave() {
    pointerInside = false
    hoverDirty = true
  }

  function onPointerDown(event: PointerEvent) {
    if (event.button !== 0 || !edit.active || stroke) return
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
    canvas.setPointerCapture?.(event.pointerId)
    stroke = {
      pointerId: event.pointerId,
      action: edit.tool,
      visited: new Set(),
      last: null,
      done: 0,
      failed: 0,
      lastError: null,
      terrainChanged: false,
    }
    subTiles.holdRefresh(true)
    enqueue(sub)
  }

  function onPointerUp(event: PointerEvent) {
    if (stroke && event.pointerId === stroke.pointerId) void finishStroke(event)
  }

  canvas.addEventListener('pointermove', onPointerMove)
  canvas.addEventListener('pointerleave', onPointerLeave)
  canvas.addEventListener('pointerdown', onPointerDown)
  canvas.addEventListener('pointerup', onPointerUp)
  canvas.addEventListener('pointercancel', onPointerUp)

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
        if (!edit.active && stroke) void finishStroke()
        return
      }
      const now = performance.now()
      if (!hoverDirty && now - lastPickAt < REPICK_INTERVAL_MS) return
      hoverDirty = false
      lastPickAt = now
      const sub = pickUnderPointer()
      setOutline(sub)
      if (stroke && sub && sub.tileId !== stroke.last?.tileId) enqueue(sub)
    },
    dispose() {
      setOutline(null)
      queue.length = 0
      stroke = null
      subTiles.holdRefresh(false)
      canvas.removeEventListener('pointermove', onPointerMove)
      canvas.removeEventListener('pointerleave', onPointerLeave)
      canvas.removeEventListener('pointerdown', onPointerDown)
      canvas.removeEventListener('pointerup', onPointerUp)
      canvas.removeEventListener('pointercancel', onPointerUp)
      edit.subTilesReady = false
    },
  }
}
