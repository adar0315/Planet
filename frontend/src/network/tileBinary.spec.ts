import { describe, expect, it } from 'vitest'
import { decodeSubTiles, decodeTiles } from './tileBinary'

/**
 * 照後端 TileBinaryEncoder 的格式手工組一份資料。
 * 這裡只守「前端照格式讀」；前後端真的對得上，是拿同一個請求的 JSON 與二進位互相比對驗的。
 */
function build(
  header: object,
  count: number,
  columns: { doubles: number[][]; ints: number[][]; floats: number[][]; bytes: number[][] },
): ArrayBuffer {
  const json = new TextEncoder().encode(JSON.stringify(header))
  const padded = (json.length + 7) & ~7
  const size =
    16 +
    padded +
    columns.doubles.reduce((sum, c) => sum + c.length * 8, 0) +
    (columns.ints.length + columns.floats.length) * count * 4 +
    columns.bytes.length * count
  const buffer = new ArrayBuffer(size)
  const view = new DataView(buffer)
  view.setUint32(0, 0x31425450, true)
  view.setUint32(4, count, true)
  view.setUint32(8, json.length, true)
  new Uint8Array(buffer, 16, padded).fill(0x20)
  new Uint8Array(buffer, 16, json.length).set(json)

  let offset = 16 + padded
  const write = (column: number[], width: number, set: (at: number, v: number) => void) => {
    for (const v of column) {
      set(offset, v)
      offset += width
    }
  }
  for (const column of columns.doubles) write(column, 8, (at, v) => view.setFloat64(at, v, true))
  for (const column of columns.ints) write(column, 4, (at, v) => view.setInt32(at, v, true))
  for (const column of columns.floats) write(column, 4, (at, v) => view.setFloat32(at, v, true))
  for (const column of columns.bytes) write(column, 1, (at, v) => view.setUint8(at, v))
  return buffer
}

const NONE = -2147483648

describe('decodeTiles', () => {
  // 標頭長度不是 8 的倍數，順便驗對齊的補白
  const header = { kind: 'tile', shapes: ['Hexagon', 'Pentagon'], terrains: ['Rock', 'Seawater'] }
  const buffer = build(header, 2, {
    doubles: [
      [15471163, 7],
      [6.668932, -89.5],
      [22.262288, -179.999999],
      [15470108, 15471162, NaN, 15472216, 15471164, 15470109, 1, 2, 3, 4, 5, NaN],
    ],
    ints: [
      [15, 0],
      [0, 6],
      [900, -964],
      [0, 0],
      [174772, NONE],
      [NONE, 4321],
    ],
    floats: [[0, 12.5]],
    bytes: [
      [0, 1],
      [0, 1],
      [6, 5],
    ],
  })

  it('組回跟 JSON 一樣的物件，沒有值的選用欄位不出現', () => {
    expect(decodeTiles(buffer)).toEqual([
      {
        tileId: 15471163,
        shape: 'Hexagon',
        faceIndex: 15,
        latitude: 6.668932,
        longitude: 22.262288,
        terrain: 'Rock',
        heightCategory: 0,
        elevationLevel: 900,
        seaLevel: 0,
        neighborIds: [15470108, 15471162, null, 15472216, 15471164, 15470109],
        sourceCellId: 174772,
      },
      {
        tileId: 7,
        shape: 'Pentagon',
        faceIndex: 0,
        latitude: -89.5,
        longitude: -179.999999,
        terrain: 'Seawater',
        heightCategory: 6,
        elevationLevel: -964,
        seaLevel: 0,
        // 五角形只有 5 個鄰居：陣列長度照原本的，不補第 6 個
        neighborIds: [1, 2, 3, 4, 5],
        riverWidthMeters: 12.5,
        riverCellId: 4321,
      },
    ])
    expect(Object.keys(decodeTiles(buffer)[0]!)).not.toContain('riverWidthMeters')
  })

  it('資料被截斷或種類不對時丟錯，不回傳半套', () => {
    expect(() => decodeTiles(buffer.slice(0, buffer.byteLength - 1))).toThrow()
    expect(() => decodeTiles(new ArrayBuffer(32))).toThrow('不是地塊二進位資料')
    expect(() => decodeSubTiles(buffer)).toThrow('預期是小格')
  })
})

describe('decodeSubTiles', () => {
  it('編號放進 tileId，建築與地形修改查字典', () => {
    const header = {
      kind: 'subTile',
      shapes: ['Hexagon'],
      terrains: ['Rock', 'Freshwater'],
      structures: ['House', 'Castle'],
      terrainEdits: ['Channel'],
    }
    const buffer = build(header, 2, {
      doubles: [
        [1000486946029, 1000486946030],
        [15379144, 15379144],
        [10.274593, 10.28],
        [20.132742, 20.14],
        [1000486940368, NaN, NaN, NaN, NaN, NaN, 1, 2, 3, 4, 5, 6],
      ],
      ints: [
        [0, 2],
        [723, 700],
        [0, 0],
        [173738, 173738],
        [NONE, 99],
      ],
      floats: [
        [0, 8],
        [0, 250],
      ],
      bytes: [
        [0, 0],
        [0, 1],
        [6, 6],
        [0, 1],
        [2, 0],
        [0, 1],
      ],
    })

    expect(decodeSubTiles(buffer)).toEqual([
      {
        tileId: 1000486946029,
        parentTileId: 15379144,
        shape: 'Hexagon',
        latitude: 10.274593,
        longitude: 20.132742,
        terrain: 'Rock',
        heightCategory: 0,
        elevationLevel: 723,
        seaLevel: 0,
        neighborIds: [1000486940368, null, null, null, null, null],
        sourceCellId: 173738,
        structure: 'Castle',
      },
      {
        tileId: 1000486946030,
        parentTileId: 15379144,
        shape: 'Hexagon',
        latitude: 10.28,
        longitude: 20.14,
        terrain: 'Freshwater',
        heightCategory: 2,
        elevationLevel: 700,
        seaLevel: 0,
        neighborIds: [1, 2, 3, 4, 5, 6],
        sourceCellId: 173738,
        riverCellId: 99,
        riverDistanceMeters: 250,
        riverWidthMeters: 8,
        floodplain: true,
        terrainEdit: 'Channel',
      },
    ])
  })
})
