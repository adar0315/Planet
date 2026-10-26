import * as THREE from 'three'
import type { TileDto } from '@/types/tile'
import {
  computeTilePolygon,
  slopeNormalOf,
  tileSurfaceRadius,
  type TileMeshOptions,
} from './tileMeshBuilder'

/**
 * 把整個patch的Tile合併成「一個」BufferGeometry／一次draw call。
 *
 * 原本每格各自一個 THREE.Mesh（共用材質但geometry獨立），Three.js無法批次處理，
 * 2000格就是每幀2000次draw call，而且每次移動都要dispose+重建2000個geometry。
 * 實測（2026-08-30，瀏覽器實機）：
 *   建構2000格   76.8ms → 6.1ms（12.6倍）
 *   draw call    2000   → 1
 *   全量重新上色 2000次GPU上傳 → 1次（4.1ms）
 *
 * 頂點與三角形一律「依tiles陣列順序」連續配置，這是picking能成立的前提：
 * raycast回傳的 intersection.faceIndex 是三角形序號，六角格佔6個、五角格佔5個，
 * 再加上不定數量的側面牆，stride完全不固定，所以用一張 triangleToTileIndex 查表直接對應。
 *
 * 每格包含「上蓋」與「側面牆」：上蓋是共用角落構成的多邊形（與相鄰格密合），
 * 側面牆只有在「這格比鄰居高」時才畫，往下接到鄰居的表面高度，把高低差的裂縫補起來。
 * 每面牆只由較高的那一格畫，所以不會重複。
 */
export interface TilePatchMesh {
  /** 整個patch只有這一個Mesh（一次draw call）。 */
  readonly mesh: THREE.Mesh
  /** 這個patch包含的Tile，順序與geometry內的頂點/三角形配置順序一致。 */
  readonly tiles: readonly TileDto[]
  /** patch的外接球半徑（場景單位），供初始鏡頭取景用。 */
  readonly boundingRadius: number
  /** raycast命中的三角形序號 → 該三角形屬於哪個Tile的tileId；超出範圍回傳undefined。 */
  tileIdAtFace(faceIndex: number): number | undefined
  /** tileId → 在 tiles 陣列中的索引；不在這個patch裡回傳undefined。 */
  indexOfTile(tileId: number): number | undefined
  /** 只更新單一Tile的顏色（含它的側面牆），只上傳該格的頂點範圍。 */
  recolorTile(tileIndex: number, colorHex: number): void
  /** 重新計算全部Tile的顏色，最後只觸發一次GPU上傳。 */
  recolorAll(colorOf: (tile: TileDto, index: number) => number): void
  /**
   * 藏起／顯示一格（含側面牆）。近看細分時，被小六角取代的地塊要藏起來（水文地貌規劃 H7）。
   * 藏的做法是把這格的頂點全部收到格心——三角形面積變成 0，GPU 不會畫；原本的位置留一份備份，顯示時還原。
   */
  setTileHidden(tileIndex: number, hidden: boolean): void
  /** 釋放geometry。材質是module-level共用的，不能dispose。 */
  dispose(): void
}

const patchMaterial = new THREE.MeshLambertMaterial({ vertexColors: true, side: THREE.DoubleSide })

/** 格子邊線：淡淡的深色，不寫深度（線之間不互相遮）。 */
const edgeLineMaterial = new THREE.LineBasicMaterial({
  color: 0x000000,
  transparent: true,
  opacity: 0.22,
  depthWrite: false,
})

/** 邊線往上抬多少公尺（不含誇張倍率），避免跟頂面搶深度而閃爍。 */
const EDGE_LINE_LIFT_METERS = 5

/**
 * 近看細分用的額外畫法（2026-10-26，使用者要求「陸地一片平坦沒細節」的改善）。地塊本身不開。
 */
export interface PatchMeshExtras {
  /** 頂面用依鄰格高度算出的坡面法向量（山坡陰影）；水面維持朝正上方。 */
  slopeShading?: boolean
  /** 描出每一格的邊線（掛在 mesh 底下的一個 LineSegments）。 */
  edgeLines?: boolean
}

/** 每格最多的頂點數：上蓋(1中心+6角落) + 6面牆×4頂點。 */
const MAX_VERTICES_PER_TILE = 7 + 6 * 4
/** 每格最多的三角形數：上蓋6 + 6面牆×2。 */
const MAX_TRIANGLES_PER_TILE = 6 + 6 * 2

/** 取得某格的鄰居；回傳數量不足時 computeTilePolygon 會自動退回近似畫法。 */
export type NeighbourLookup = (tile: TileDto, index: number) => readonly TileDto[] | undefined

/** 分批建構用的控制介面，由 buildTilePatchMesh／buildTilePatchMeshSliced 各自驅動。 */
interface PatchMeshBuilder {
  readonly tileCount: number
  /** 填入 [from, to) 這段格子。回傳下一個還沒填的索引。 */
  fillRange(from: number, to: number): number
  /** 全部填完後產生實際的 geometry 與 TilePatchMesh。 */
  finish(): TilePatchMesh
}

/**
 * 一次同步建好整個patch的合併mesh。格子數多的時候會卡住主執行緒
 * （5萬格在瀏覽器實測約1秒），走動中要用 {@link buildTilePatchMeshSliced}。
 * 這支保留給測試與小量資料用。
 * @param tiles 這個patch的全部Tile（順序會被保留，picking與recolor都依賴這個順序）。
 * @param options 尺寸換算設定。
 * @param colorOf 取得每格顏色（0xrrggbb）的函式。
 * @param neighboursOf 取得每格鄰居的函式。有完整鄰居的格子才會用「共用角落」畫成密合的蜂窩。
 */
export function buildTilePatchMesh(
  tiles: readonly TileDto[],
  options: TileMeshOptions,
  colorOf: (tile: TileDto, index: number) => number,
  neighboursOf?: NeighbourLookup,
  extras?: PatchMeshExtras,
): TilePatchMesh {
  const builder = createPatchMeshBuilder(tiles, options, colorOf, neighboursOf, extras)
  builder.fillRange(0, builder.tileCount)
  return builder.finish()
}

/**
 * 一次處理幾格才檢查一次時間。太小的話 performance.now() 本身的成本就變顯著，
 * 太大則預算根本管不住——實測每格約20µs，256格一塊就要5ms，剛好等於整個預算，
 * 等於每次都超支一整塊。64格（約1.3ms）才留得下讓預算真正生效的餘裕。
 */
const SLICE_CHUNK_TILES = 64

/** 預設每幀最多花幾毫秒建 mesh，其餘時間留給渲染與輸入。 */
const DEFAULT_SLICE_BUDGET_MS = 5

/**
 * 把控制權交還給瀏覽器，讓它有機會繪製一幀並處理輸入。
 *
 * 用 MessageChannel 而不是：
 *   * `requestAnimationFrame`——在背景分頁或某些環境下不會觸發，建構會整個停住；
 *   * `setTimeout(0)`——巢狀計時器會被夾到最少4ms，分幾十批下來憑空多出上百毫秒；
 *   * `Promise.resolve()`——那是microtask，根本不會讓瀏覽器有機會繪製。
 */
function yieldToBrowser(): Promise<void> {
  return new Promise((resolve) => {
    const channel = new MessageChannel()
    channel.port1.onmessage = () => {
      channel.port1.close()
      resolve()
    }
    channel.port2.postMessage(null)
  })
}

/**
 * 跟 {@link buildTilePatchMesh} 相同，但把建構迴圈切成多幀完成，避免一次卡住主執行緒。
 *
 * 5萬格的填充迴圈實測約1秒的CPU工作量，一次做完就是整整1秒的凍結。
 * 切成每次約5ms之後（實測193段、整段期間主執行緒沒有超過46ms的區塊），
 * 牆鐘時間會拉長到約1.8秒（多出來的是每次交還控制權時瀏覽器順便繪製一幀的成本），
 * 但畫面與輸入全程保持回應。建構期間**舊的patch仍然掛在場景上**，所以使用者看不到任何空缺。
 *
 * 注意：換patch整體的卡頓不只這一段——`createTilePatch` 建索引（5萬格實測約296ms）
 * 與3MB回應的 `JSON.parse` 都還是同步的，那是另外兩處、要各自處理。
 *
 * @param isCancelled 每次交還控制權後檢查；回傳true就中止並回傳null（例如元件已卸載）。
 *   中止時還沒建立任何GPU資源（geometry是最後才產生的），直接丟掉即可，不需要dispose。
 */
export async function buildTilePatchMeshSliced(
  tiles: readonly TileDto[],
  options: TileMeshOptions,
  colorOf: (tile: TileDto, index: number) => number,
  neighboursOf?: NeighbourLookup,
  opts?: { budgetMs?: number; isCancelled?: () => boolean } & PatchMeshExtras,
): Promise<TilePatchMesh | null> {
  const builder = createPatchMeshBuilder(tiles, options, colorOf, neighboursOf, opts)
  const budgetMs = opts?.budgetMs ?? DEFAULT_SLICE_BUDGET_MS

  let next = 0
  while (next < builder.tileCount) {
    const deadline = performance.now() + budgetMs
    do {
      next = builder.fillRange(next, Math.min(next + SLICE_CHUNK_TILES, builder.tileCount))
    } while (next < builder.tileCount && performance.now() < deadline)

    if (next < builder.tileCount) {
      await yieldToBrowser()
      if (opts?.isCancelled?.()) return null
    }
  }

  return builder.finish()
}

function createPatchMeshBuilder(
  tiles: readonly TileDto[],
  options: TileMeshOptions,
  colorOf: (tile: TileDto, index: number) => number,
  neighboursOf?: NeighbourLookup,
  extras?: PatchMeshExtras,
): PatchMeshBuilder {
  const tileCount = tiles.length
  const edgePositions: number[] = []
  const edgeLift = 1 + EDGE_LINE_LIFT_METERS / options.planetRadiusMeters

  // 先照最壞情況配置，最後再用 subarray 裁到實際大小——
  // 側面牆的數量要算過多邊形才知道，分兩趟走訪等於把最貴的部分做兩次。
  const positions = new Float32Array(tileCount * MAX_VERTICES_PER_TILE * 3)
  const normals = new Float32Array(tileCount * MAX_VERTICES_PER_TILE * 3)
  const colors = new Float32Array(tileCount * MAX_VERTICES_PER_TILE * 3)
  const indices = new Uint32Array(tileCount * MAX_TRIANGLES_PER_TILE * 3)
  const triangleToTileIndex = new Uint32Array(tileCount * MAX_TRIANGLES_PER_TILE)

  // 每格實際用到的頂點/三角形範圍（recolor 與 picking 都要用）
  const vertexStart = new Uint32Array(tileCount + 1)
  const triangleStart = new Uint32Array(tileCount + 1)

  const indexByTileId = new Map<number, number>()
  const color = new THREE.Color()
  const scratchEdge = new THREE.Vector3()
  const scratchRadial = new THREE.Vector3()
  const scratchNormal = new THREE.Vector3()
  const scratchMid = new THREE.Vector3()
  const scratchSlope = new THREE.Vector3()

  let v = 0
  let t = 0

  function writeVertex(x: number, y: number, z: number, nx: number, ny: number, nz: number) {
    positions[v * 3] = x
    positions[v * 3 + 1] = y
    positions[v * 3 + 2] = z
    normals[v * 3] = nx
    normals[v * 3 + 1] = ny
    normals[v * 3 + 2] = nz
    colors[v * 3] = color.r
    colors[v * 3 + 1] = color.g
    colors[v * 3 + 2] = color.b
    v++
  }

  function writeTriangle(a: number, b: number, c: number, tileIndex: number) {
    indices[t * 3] = a
    indices[t * 3 + 1] = b
    indices[t * 3 + 2] = c
    triangleToTileIndex[t] = tileIndex
    t++
  }

  function fillTile(i: number) {
    const tile = tiles[i]!
    indexByTileId.set(tile.tileId, i)
    vertexStart[i] = v
    triangleStart[i] = t
    color.setHex(colorOf(tile, i))

    const neighbours = neighboursOf?.(tile, i)
    const { center, normal, ring, sideCount, edgeNeighbours } = computeTilePolygon(
      tile,
      options,
      neighbours,
    )

    // 山坡陰影：陸地的頂面改用坡面法向量（水面是平的，維持朝上）
    const capNormal =
      extras?.slopeShading && neighbours && tile.seaLevel <= tile.elevationLevel
        ? slopeNormalOf(tile, neighbours, options, scratchSlope)
        : normal

    // ---- 上蓋 ----
    const capBase = v
    writeVertex(center.x, center.y, center.z, capNormal.x, capNormal.y, capNormal.z)
    for (let k = 0; k < sideCount; k++) {
      const p = ring[k]!
      // 整格共用同一個切平面法向量（不呼叫computeVertexNormals，那會把相鄰格平滑化、毀掉格子邊界）
      writeVertex(p.x, p.y, p.z, capNormal.x, capNormal.y, capNormal.z)
    }

    // ---- 邊線 ----
    // 每條邊只畫一次：由較高的那一格畫（線在牆頂、從上面看得到）；一樣高時由編號大的畫；沒有鄰居的邊一律畫
    if (extras?.edgeLines) {
      for (let k = 0; k < sideCount; k++) {
        const neighbour = edgeNeighbours[k]
        if (
          neighbour &&
          (neighbour.elevationLevel > tile.elevationLevel ||
            (neighbour.elevationLevel === tile.elevationLevel && neighbour.tileId < tile.tileId))
        )
          continue
        const a = ring[k]!
        const b = ring[(k + 1) % sideCount]!
        edgePositions.push(
          a.x * edgeLift,
          a.y * edgeLift,
          a.z * edgeLift,
          b.x * edgeLift,
          b.y * edgeLift,
          b.z * edgeLift,
        )
      }
    }
    for (let k = 0; k < sideCount; k++) {
      writeTriangle(capBase, capBase + 1 + k, capBase + 1 + ((k + 1) % sideCount), i)
    }

    // ---- 側面牆 ----
    // 只有「比鄰居高」的那一格畫牆，往下接到鄰居的表面高度，所以每面牆剛好被畫一次。
    for (let k = 0; k < sideCount; k++) {
      const neighbour = edgeNeighbours[k]
      if (!neighbour || tile.elevationLevel <= neighbour.elevationLevel) continue

      const top0 = ring[k]!
      const top1 = ring[(k + 1) % sideCount]!
      const bottomRadius = tileSurfaceRadius(neighbour, options)

      // 牆的法向量：水平朝外。取邊向量與徑向的外積，再依「是否背離格子中心」決定正負號。
      scratchEdge.copy(top1).sub(top0)
      scratchRadial.copy(top0).normalize()
      scratchNormal.crossVectors(scratchEdge, scratchRadial).normalize()
      scratchMid.copy(top0).add(top1).multiplyScalar(0.5).sub(center)
      if (scratchNormal.dot(scratchMid) < 0) scratchNormal.negate()

      const wallBase = v
      writeVertex(top0.x, top0.y, top0.z, scratchNormal.x, scratchNormal.y, scratchNormal.z)
      writeVertex(top1.x, top1.y, top1.z, scratchNormal.x, scratchNormal.y, scratchNormal.z)

      scratchRadial.copy(top0).normalize().multiplyScalar(bottomRadius)
      writeVertex(
        scratchRadial.x,
        scratchRadial.y,
        scratchRadial.z,
        scratchNormal.x,
        scratchNormal.y,
        scratchNormal.z,
      )
      scratchRadial.copy(top1).normalize().multiplyScalar(bottomRadius)
      writeVertex(
        scratchRadial.x,
        scratchRadial.y,
        scratchRadial.z,
        scratchNormal.x,
        scratchNormal.y,
        scratchNormal.z,
      )

      writeTriangle(wallBase, wallBase + 2, wallBase + 1, i)
      writeTriangle(wallBase + 1, wallBase + 2, wallBase + 3, i)
    }
  }

  return {
    tileCount,

    fillRange(from: number, to: number): number {
      for (let i = from; i < to; i++) fillTile(i)
      return to
    },

    finish: finishBuild,
  }

  function finishBuild(): TilePatchMesh {
    vertexStart[tileCount] = v
    triangleStart[tileCount] = t

    const totalVertices = v
    const totalTriangles = t

    const geometry = new THREE.BufferGeometry()
    const positionArray = positions.subarray(0, totalVertices * 3)
    const positionAttribute = new THREE.BufferAttribute(positionArray, 3)
    geometry.setAttribute('position', positionAttribute)
    geometry.setAttribute(
      'normal',
      new THREE.BufferAttribute(normals.subarray(0, totalVertices * 3), 3),
    )
    const colorArray = colors.subarray(0, totalVertices * 3)
    const colorAttribute = new THREE.BufferAttribute(colorArray, 3)
    geometry.setAttribute('color', colorAttribute)
    geometry.setIndex(new THREE.BufferAttribute(indices.subarray(0, totalTriangles * 3), 1))
    // 主動算好：raycast本來就會需要，先算可以順便給初始鏡頭取景用（不必再走訪整個場景算Box3）。
    geometry.computeBoundingSphere()

    const mesh = new THREE.Mesh(geometry, patchMaterial)
    let edgeGeometry: THREE.BufferGeometry | null = null
    if (edgePositions.length > 0) {
      edgeGeometry = new THREE.BufferGeometry()
      edgeGeometry.setAttribute('position', new THREE.Float32BufferAttribute(edgePositions, 3))
      // 掛在 mesh 底下：跟著加進／移出場景，不必另外管理

      mesh.add(new THREE.LineSegments(edgeGeometry, edgeLineMaterial))
    }
    const hiddenBackup = new Map<number, Float32Array>()

    function writeTileColor(tileIndex: number, colorHex: number): void {
      color.setHex(colorHex)
      const from = vertexStart[tileIndex]!
      const to = vertexStart[tileIndex + 1]!
      for (let vertex = from; vertex < to; vertex++) {
        colorArray[vertex * 3] = color.r
        colorArray[vertex * 3 + 1] = color.g
        colorArray[vertex * 3 + 2] = color.b
      }
    }

    return {
      mesh,
      tiles,
      boundingRadius: geometry.boundingSphere?.radius ?? 0,

      tileIdAtFace(faceIndex: number): number | undefined {
        if (faceIndex < 0 || faceIndex >= totalTriangles) return undefined
        return tiles[triangleToTileIndex[faceIndex]!]?.tileId
      },

      indexOfTile(tileId: number): number | undefined {
        return indexByTileId.get(tileId)
      },

      recolorTile(tileIndex: number, colorHex: number): void {
        if (tileIndex < 0 || tileIndex >= tileCount) return
        writeTileColor(tileIndex, colorHex)
        // 只上傳這一格的頂點區間（上蓋＋側面牆），不是整個buffer。
        const from = vertexStart[tileIndex]!
        const to = vertexStart[tileIndex + 1]!
        colorAttribute.addUpdateRange(from * 3, (to - from) * 3)
        colorAttribute.needsUpdate = true
      },

      recolorAll(colorOf: (tile: TileDto, index: number) => number): void {
        for (let i = 0; i < tileCount; i++) {
          writeTileColor(i, colorOf(tiles[i]!, i))
        }
        // 整批改完只觸發一次上傳（原本是每格一次needsUpdate＝2000次bufferSubData）。
        colorAttribute.clearUpdateRanges()
        colorAttribute.needsUpdate = true
      },

      setTileHidden(tileIndex: number, hidden: boolean): void {
        if (tileIndex < 0 || tileIndex >= tileCount) return
        const from = vertexStart[tileIndex]!
        const to = vertexStart[tileIndex + 1]!
        if (hidden) {
          if (hiddenBackup.has(tileIndex)) return
          hiddenBackup.set(tileIndex, positionArray.slice(from * 3, to * 3))
          // 第一個頂點是上蓋的中心
          const cx = positionArray[from * 3]!
          const cy = positionArray[from * 3 + 1]!
          const cz = positionArray[from * 3 + 2]!
          for (let vertex = from; vertex < to; vertex++) {
            positionArray[vertex * 3] = cx
            positionArray[vertex * 3 + 1] = cy
            positionArray[vertex * 3 + 2] = cz
          }
        } else {
          const backup = hiddenBackup.get(tileIndex)
          if (!backup) return
          positionArray.set(backup, from * 3)
          hiddenBackup.delete(tileIndex)
        }
        positionAttribute.addUpdateRange(from * 3, (to - from) * 3)
        positionAttribute.needsUpdate = true
      },

      dispose(): void {
        geometry.dispose() // 材質是module-level共用的，不能在這裡dispose
        edgeGeometry?.dispose()
      },
    }
  }
}
