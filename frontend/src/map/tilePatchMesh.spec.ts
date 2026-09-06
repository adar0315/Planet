import { describe, expect, it } from 'vitest'
import { buildTilePatchMesh, buildTilePatchMeshSliced } from './tilePatchMesh'
import type { TileDto, TileShape } from '@/types/tile'

/**
 * 整個patch合併成單一geometry之後，「點到哪個三角形」→「那是哪一格」的對應是全前端最容易
 * 出錯的地方：六角格佔6個三角形、五角格佔5個，stride並不固定，任何「用乘法推算」的寫法
 * 都會在遇到五角形之後整個錯位，而且錯位是靜默的——玩家只會發現點A卻走到B。
 *
 * 這裡用窮舉（每一個三角形都驗）而不是抽樣，因為錯位通常只發生在特定位置。
 */

const OPTIONS = { renderRadius: 50, planetRadiusMeters: 1_851_000 }

function makeTile(id: number, shape: TileShape, latitude: number, longitude: number): TileDto {
  return {
    tileId: id,
    shape,
    faceIndex: 0,
    latitude,
    longitude,
    terrain: 'Grassland',
    heightCategory: 0,
    elevationLevel: 100,
    seaLevel: 0,
  }
}

/** 產生一批散布在小範圍內的Tile，shape 由 shapeAt 決定。 */
function makePatch(count: number, shapeAt: (i: number) => TileShape): TileDto[] {
  return Array.from({ length: count }, (_, i) =>
    makeTile(1000 + i, shapeAt(i), 0.02 + (i % 20) * 0.03, 0.06 + Math.floor(i / 20) * 0.03),
  )
}

/** 走訪每一個三角形，確認 tileIdAtFace 回傳的都是它真正所屬的那一格。 */
function expectEveryTriangleMapsToItsTile(tiles: TileDto[]) {
  const patch = buildTilePatchMesh(tiles, OPTIONS, () => 0x8fae4e)
  let triangle = 0
  for (const tile of tiles) {
    const sides = tile.shape === 'Pentagon' ? 5 : 6
    for (let k = 0; k < sides; k++) {
      expect(patch.tileIdAtFace(triangle)).toBe(tile.tileId)
      triangle++
    }
  }
  return { patch, totalTriangles: triangle }
}

describe('buildTilePatchMesh', () => {
  it('把整個patch合併成單一Mesh（一次draw call）', () => {
    const patch = buildTilePatchMesh(
      makePatch(50, () => 'Hexagon'),
      OPTIONS,
      () => 0x8fae4e,
    )
    expect(patch.mesh.type).toBe('Mesh')
    expect(patch.mesh.children).toHaveLength(0)
  })

  it('全六角形：頂點與三角形數量正確', () => {
    const tiles = makePatch(40, () => 'Hexagon')
    const patch = buildTilePatchMesh(tiles, OPTIONS, () => 0x8fae4e)
    // 每格 1中心+6角落 = 7頂點、6個三角形
    expect(patch.mesh.geometry.getAttribute('position').count).toBe(40 * 7)
    expect(patch.mesh.geometry.getIndex()!.count / 3).toBe(40 * 6)
  })

  it('全五角形：頂點與三角形數量正確（不是套用六角形的數字）', () => {
    const tiles = makePatch(40, () => 'Pentagon')
    const patch = buildTilePatchMesh(tiles, OPTIONS, () => 0x8fae4e)
    expect(patch.mesh.geometry.getAttribute('position').count).toBe(40 * 6)
    expect(patch.mesh.geometry.getIndex()!.count / 3).toBe(40 * 5)
  })

  it('六角形patch：每個三角形都對應到正確的Tile', () => {
    const { totalTriangles } = expectEveryTriangleMapsToItsTile(makePatch(60, () => 'Hexagon'))
    expect(totalTriangles).toBe(360)
  })

  it('五角/六角交錯：每個三角形都對應到正確的Tile（固定stride在這裡一定會錯位）', () => {
    const tiles = makePatch(60, (i) => (i % 2 === 0 ? 'Pentagon' : 'Hexagon'))
    const { totalTriangles } = expectEveryTriangleMapsToItsTile(tiles)
    expect(totalTriangles).toBe(30 * 5 + 30 * 6)
  })

  it('只有第一格是五角形：後面所有六角格都會整體位移一格，最容易漏掉的情況', () => {
    expectEveryTriangleMapsToItsTile(makePatch(30, (i) => (i === 0 ? 'Pentagon' : 'Hexagon')))
  })

  it('只有最後一格是五角形', () => {
    expectEveryTriangleMapsToItsTile(makePatch(30, (i) => (i === 29 ? 'Pentagon' : 'Hexagon')))
  })

  it('超出範圍的faceIndex回傳undefined，不會回傳錯的格子', () => {
    const tiles = makePatch(10, () => 'Hexagon')
    const patch = buildTilePatchMesh(tiles, OPTIONS, () => 0x8fae4e)
    expect(patch.tileIdAtFace(-1)).toBeUndefined()
    expect(patch.tileIdAtFace(60)).toBeUndefined()
    expect(patch.tileIdAtFace(59)).toBe(tiles[9]!.tileId)
  })

  it('indexOfTile 能把tileId換回patch內的索引', () => {
    const tiles = makePatch(20, () => 'Hexagon')
    const patch = buildTilePatchMesh(tiles, OPTIONS, () => 0x8fae4e)
    tiles.forEach((t, i) => expect(patch.indexOfTile(t.tileId)).toBe(i))
    expect(patch.indexOfTile(999999)).toBeUndefined()
  })

  it('recolorAll 會把每格改成colorOf算出來的顏色', () => {
    const tiles = makePatch(12, () => 'Hexagon')
    const patch = buildTilePatchMesh(tiles, OPTIONS, () => 0x000000)
    patch.recolorAll((_, i) => (i === 3 ? 0xff0000 : 0x00ff00))

    const colors = patch.mesh.geometry.getAttribute('color')
    // 第3格的7個頂點應該是紅色
    expect(colors.getX(3 * 7)).toBeCloseTo(1)
    expect(colors.getY(3 * 7)).toBeCloseTo(0)
    // 其他格是綠色
    expect(colors.getX(0)).toBeCloseTo(0)
    expect(colors.getY(0)).toBeCloseTo(1)
  })

  it('recolorTile 只改指定那一格，不影響鄰居', () => {
    const tiles = makePatch(12, () => 'Hexagon')
    const patch = buildTilePatchMesh(tiles, OPTIONS, () => 0x00ff00)
    patch.recolorTile(5, 0xff0000)

    const colors = patch.mesh.geometry.getAttribute('color')
    for (let v = 5 * 7; v < 6 * 7; v++) expect(colors.getX(v)).toBeCloseTo(1)
    expect(colors.getX(4 * 7)).toBeCloseTo(0) // 前一格沒被動到
    expect(colors.getX(6 * 7)).toBeCloseTo(0) // 後一格沒被動到
  })

  it('recolorTile 索引超出範圍時安靜忽略，不會爆或寫壞buffer', () => {
    const patch = buildTilePatchMesh(
      makePatch(5, () => 'Hexagon'),
      OPTIONS,
      () => 0x00ff00,
    )
    expect(() => patch.recolorTile(-1, 0xff0000)).not.toThrow()
    expect(() => patch.recolorTile(99, 0xff0000)).not.toThrow()
    expect(patch.mesh.geometry.getAttribute('color').getY(0)).toBeCloseTo(1)
  })

  it('索引值都落在頂點範圍內（不會指到別格或越界）', () => {
    const tiles = makePatch(40, (i) => (i % 3 === 0 ? 'Pentagon' : 'Hexagon'))
    const patch = buildTilePatchMesh(tiles, OPTIONS, () => 0x8fae4e)
    const index = patch.mesh.geometry.getIndex()!
    const vertexCount = patch.mesh.geometry.getAttribute('position').count
    for (let i = 0; i < index.count; i++) {
      const v = index.getX(i)
      expect(v).toBeGreaterThanOrEqual(0)
      expect(v).toBeLessThan(vertexCount)
    }
  })

  it('會算出boundingRadius供鏡頭取景用', () => {
    const patch = buildTilePatchMesh(
      makePatch(40, () => 'Hexagon'),
      OPTIONS,
      () => 0x8fae4e,
    )
    expect(patch.boundingRadius).toBeGreaterThan(0)
  })

  it('空patch不會爆', () => {
    const patch = buildTilePatchMesh([], OPTIONS, () => 0x8fae4e)
    expect(patch.tileIdAtFace(0)).toBeUndefined()
    expect(() => patch.recolorAll(() => 0)).not.toThrow()
    expect(() => patch.dispose()).not.toThrow()
  })
})

/**
 * 切片版本的重點不是「快」，而是「切了之後結果要一模一樣」。
 * 建構過程被切成好幾段、中間交還控制權給瀏覽器，任何依賴「迴圈一路跑到底」的隱含假設
 * （例如共用的暫存變數、累加中的偏移量）都會在這裡露餡，而且症狀是幾何靜默錯位。
 * 所以直接拿同步版本的輸出當基準逐位元組比對。
 */
describe('buildTilePatchMeshSliced', () => {
  /** budgetMs=0 代表每填一塊（256格）就交還一次控制權，把切片次數逼到最多，最容易驗出狀態殘留。 */
  const FORCE_MANY_SLICES = { budgetMs: 0 }

  const colorOf = (tile: TileDto, index: number) => (tile.tileId * 7 + index) & 0xffffff

  it('切片建出來的geometry跟同步版本完全相同', async () => {
    const tiles = makePatch(600, (i) => (i % 37 === 0 ? 'Pentagon' : 'Hexagon'))
    const sync = buildTilePatchMesh(tiles, OPTIONS, colorOf)
    const sliced = await buildTilePatchMeshSliced(
      tiles,
      OPTIONS,
      colorOf,
      undefined,
      FORCE_MANY_SLICES,
    )

    expect(sliced).not.toBeNull()
    for (const name of ['position', 'normal', 'color'] as const) {
      expect(sliced!.mesh.geometry.getAttribute(name).array).toEqual(
        sync.mesh.geometry.getAttribute(name).array,
      )
    }
    expect(sliced!.mesh.geometry.getIndex()!.array).toEqual(sync.mesh.geometry.getIndex()!.array)
    expect(sliced!.boundingRadius).toBe(sync.boundingRadius)
  })

  it('切片之後三角形→格子的對應仍然正確（五角形不會讓後面整片錯位）', async () => {
    const tiles = makePatch(600, (i) => (i % 37 === 0 ? 'Pentagon' : 'Hexagon'))
    const patch = await buildTilePatchMeshSliced(
      tiles,
      OPTIONS,
      colorOf,
      undefined,
      FORCE_MANY_SLICES,
    )

    let triangle = 0
    for (const tile of tiles) {
      for (let k = 0; k < (tile.shape === 'Pentagon' ? 5 : 6); k++) {
        expect(patch!.tileIdAtFace(triangle)).toBe(tile.tileId)
        triangle++
      }
    }
  })

  it('真的有把控制權交還出去（不是同步跑完）', async () => {
    const tiles = makePatch(600, () => 'Hexagon')
    let resolved = false
    const promise = buildTilePatchMeshSliced(
      tiles,
      OPTIONS,
      colorOf,
      undefined,
      FORCE_MANY_SLICES,
    ).then((mesh) => {
      resolved = true
      return mesh
    })

    // 同一個task內還不該完成——否則切片等於沒作用，主執行緒照樣被佔滿。
    expect(resolved).toBe(false)
    expect(await promise).not.toBeNull()
  })

  it('isCancelled回傳true就中止並回傳null', async () => {
    const tiles = makePatch(600, () => 'Hexagon')
    const mesh = await buildTilePatchMeshSliced(tiles, OPTIONS, colorOf, undefined, {
      ...FORCE_MANY_SLICES,
      isCancelled: () => true,
    })
    expect(mesh).toBeNull()
  })

  it('格子少到一批就填完時不會卡住（不需要交還控制權也要結束）', async () => {
    const mesh = await buildTilePatchMeshSliced(
      makePatch(10, () => 'Hexagon'),
      OPTIONS,
      colorOf,
      undefined,
      FORCE_MANY_SLICES,
    )
    expect(mesh!.tileIdAtFace(0)).toBe(1000)
  })

  it('空patch不會爆也不會卡住', async () => {
    const mesh = await buildTilePatchMeshSliced([], OPTIONS, colorOf)
    expect(mesh!.tileIdAtFace(0)).toBeUndefined()
  })
})
