/// <reference types="node" />
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'
import { decodeSubTiles, decodeTiles } from './tileBinary'

/**
 * 讀後端測試產生的標準檔（backend/tests/Planet.Service.Tests/Fixtures）。
 *
 * 後端 TileBinaryEncoderTests 驗「編碼器的輸出跟標準檔逐位元組相同」，這裡驗「解碼器讀得懂同一份標準檔」，
 * 兩邊合起來才保證前後端的格式對得上。刻意改格式時：先改這裡的解碼器與期望值，
 * 再在後端用 PLANET_UPDATE_GOLDEN=1 重新產生標準檔。
 */
function fixture(name: string): ArrayBuffer {
  const path = fileURLToPath(
    new URL(`../../../backend/tests/Planet.Service.Tests/Fixtures/${name}`, import.meta.url),
  )
  const file = readFileSync(path)
  return file.buffer.slice(file.byteOffset, file.byteOffset + file.byteLength) as ArrayBuffer
}

describe('後端產生的標準檔', () => {
  it('地塊：解出跟後端 SampleTiles 相同的物件', () => {
    expect(decodeTiles(fixture('tiles.bin'))).toEqual([
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
        neighborIds: [1, 2, 3, 4, 5],
        riverWidthMeters: 12.5,
        riverCellId: 4321,
      },
      {
        // 沒有鄰居陣列、沒有任何選用欄位：解出來也不能多出 neighborIds
        tileId: 8,
        shape: 'Hexagon',
        faceIndex: 3,
        latitude: 0,
        longitude: 0,
        terrain: 'Seawater',
        heightCategory: 6,
        elevationLevel: -964,
        seaLevel: 0,
      },
    ])
  })

  it('小格：解出跟後端 SampleSubTiles 相同的物件', () => {
    expect(decodeSubTiles(fixture('subtiles.bin'))).toEqual([
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
      {
        tileId: 1000486946031,
        parentTileId: 15379144,
        shape: 'Pentagon',
        latitude: -10.28,
        longitude: -20.14,
        terrain: 'Sand',
        heightCategory: 1,
        elevationLevel: 5,
        seaLevel: 0,
        neighborIds: [10, 11, 12, 13, 14],
      },
    ])
  })
})
