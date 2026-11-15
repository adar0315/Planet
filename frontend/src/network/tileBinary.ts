import type { StructureKind, SubTileDto, TerrainEditKind, TerrainType, TileDto, TileShape } from '@/types/tile'

/**
 * 地塊與小格的二進位格式（後端 `Planet.Service/TileBinaryEncoder.cs`，兩邊要一起改）。
 *
 * 一次 5 萬個地塊的 JSON 有 13 MB，大部分是重複的欄位名與文字化的數字；
 * 二進位把同一欄的值排在一起，大小約三分之一，這裡用 TypedArray 直接讀、再組回原本的物件。
 * 解出來的物件跟 JSON 的一樣：沒有值的選用欄位不會出現（不是 undefined 或 0）。
 * 唯一的差別是河寬與離河距離是 32 位元浮點數，會跟 JSON 的十進位寫法差在第 7 位有效數字之後。
 */

/** 請求二進位格式時放在 Accept 標頭的 MIME 類型。 */
export const TILE_BINARY_CONTENT_TYPE = 'application/vnd.planet.tiles'

const MAGIC = 0x31425450 // "PTB1"
const NEIGHBOR_SLOTS = 6
const NO_VALUE = -2147483648
const NO_NEIGHBOR_ARRAY = 255

interface BinaryHeader {
  kind: 'tile' | 'subTile'
  shapes: TileShape[]
  terrains: TerrainType[]
  structures?: StructureKind[]
  terrainEdits?: TerrainEditKind[]
}

/** 依序從緩衝區切出各欄；欄位由寬到窄排，每一欄的起點都對齊自己的寬度。 */
class ColumnReader {
  private offset: number

  constructor(
    private readonly buffer: ArrayBuffer,
    start: number,
    private readonly count: number,
  ) {
    this.offset = start
  }

  doubles(perRow = 1): Float64Array {
    const column = new Float64Array(this.buffer, this.offset, this.count * perRow)
    this.offset += column.byteLength
    return column
  }

  ints(): Int32Array {
    const column = new Int32Array(this.buffer, this.offset, this.count)
    this.offset += column.byteLength
    return column
  }

  floats(): Float32Array {
    const column = new Float32Array(this.buffer, this.offset, this.count)
    this.offset += column.byteLength
    return column
  }

  bytes(): Uint8Array {
    const column = new Uint8Array(this.buffer, this.offset, this.count)
    this.offset += column.byteLength
    return column
  }

  /** 讀完之後應該剛好用完整個緩衝區，否則就是前後端的欄位對不上。 */
  assertFinished(): void {
    if (this.offset !== this.buffer.byteLength) {
      throw new Error(`地塊二進位資料長度不符：讀到 ${this.offset}，實際 ${this.buffer.byteLength}`)
    }
  }
}

function readHeader(buffer: ArrayBuffer): { header: BinaryHeader; count: number; dataStart: number } {
  if (buffer.byteLength < 16) throw new Error('地塊二進位資料太短')
  const view = new DataView(buffer)
  if (view.getUint32(0, true) !== MAGIC) throw new Error('不是地塊二進位資料')
  const count = view.getUint32(4, true)
  const headerLength = view.getUint32(8, true)
  const header = JSON.parse(new TextDecoder().decode(new Uint8Array(buffer, 16, headerLength))) as BinaryHeader
  return { header, count, dataStart: 16 + ((headerLength + 7) & ~7) }
}

function neighborsOf(neighbors: Float64Array, lengths: Uint8Array, row: number): (number | null)[] | undefined {
  const length = lengths[row]!
  if (length === NO_NEIGHBOR_ARRAY) return undefined
  const result: (number | null)[] = []
  for (let k = 0; k < length; k++) {
    const id = neighbors[row * NEIGHBOR_SLOTS + k]!
    result.push(Number.isNaN(id) ? null : id)
  }
  return result
}

/** 解出一批地塊（`GET /api/tiles/nearby`）。 */
export function decodeTiles(buffer: ArrayBuffer): TileDto[] {
  const { header, count, dataStart } = readHeader(buffer)
  if (header.kind !== 'tile') throw new Error(`預期是地塊，收到的是 ${header.kind}`)

  const reader = new ColumnReader(buffer, dataStart, count)
  const tileId = reader.doubles()
  const latitude = reader.doubles()
  const longitude = reader.doubles()
  const neighbors = reader.doubles(NEIGHBOR_SLOTS)
  const faceIndex = reader.ints()
  const heightCategory = reader.ints()
  const elevationLevel = reader.ints()
  const seaLevel = reader.ints()
  const sourceCellId = reader.ints()
  const riverCellId = reader.ints()
  const riverWidthMeters = reader.floats()
  const shape = reader.bytes()
  const terrain = reader.bytes()
  const neighborCount = reader.bytes()
  reader.assertFinished()

  const tiles: TileDto[] = []
  for (let i = 0; i < count; i++) {
    const tile: TileDto = {
      tileId: tileId[i]!,
      shape: header.shapes[shape[i]!]!,
      faceIndex: faceIndex[i]!,
      latitude: latitude[i]!,
      longitude: longitude[i]!,
      terrain: header.terrains[terrain[i]!]!,
      heightCategory: heightCategory[i]!,
      elevationLevel: elevationLevel[i]!,
      seaLevel: seaLevel[i]!,
    }
    const neighborIds = neighborsOf(neighbors, neighborCount, i)
    if (neighborIds) tile.neighborIds = neighborIds
    if (sourceCellId[i] !== NO_VALUE) tile.sourceCellId = sourceCellId[i]!
    if (riverWidthMeters[i] !== 0) tile.riverWidthMeters = riverWidthMeters[i]!
    if (riverCellId[i] !== NO_VALUE) tile.riverCellId = riverCellId[i]!
    tiles.push(tile)
  }
  return tiles
}

/** 解出一批小格（`POST /api/tiles/subtiles`）。後端的 subTileId 在這裡就放進 tileId。 */
export function decodeSubTiles(buffer: ArrayBuffer): SubTileDto[] {
  const { header, count, dataStart } = readHeader(buffer)
  if (header.kind !== 'subTile') throw new Error(`預期是小格，收到的是 ${header.kind}`)
  const structures = header.structures ?? []
  const terrainEdits = header.terrainEdits ?? []

  const reader = new ColumnReader(buffer, dataStart, count)
  const subTileId = reader.doubles()
  const parentTileId = reader.doubles()
  const latitude = reader.doubles()
  const longitude = reader.doubles()
  const neighbors = reader.doubles(NEIGHBOR_SLOTS)
  const heightCategory = reader.ints()
  const elevationLevel = reader.ints()
  const seaLevel = reader.ints()
  const sourceCellId = reader.ints()
  const riverCellId = reader.ints()
  const riverWidthMeters = reader.floats()
  const riverDistanceMeters = reader.floats()
  const shape = reader.bytes()
  const terrain = reader.bytes()
  const neighborCount = reader.bytes()
  const floodplain = reader.bytes()
  const structure = reader.bytes()
  const terrainEdit = reader.bytes()
  reader.assertFinished()

  const subTiles: SubTileDto[] = []
  for (let i = 0; i < count; i++) {
    // 小格沒有 faceIndex（後端的 SubTileDto 就沒有這一欄），跟 JSON 的路徑一致
    const sub = {
      parentTileId: parentTileId[i]!,
      shape: header.shapes[shape[i]!]!,
      latitude: latitude[i]!,
      longitude: longitude[i]!,
      terrain: header.terrains[terrain[i]!]!,
      heightCategory: heightCategory[i]!,
      elevationLevel: elevationLevel[i]!,
      seaLevel: seaLevel[i]!,
      neighborIds: neighborsOf(neighbors, neighborCount, i) ?? [],
    } as SubTileDto
    if (sourceCellId[i] !== NO_VALUE) sub.sourceCellId = sourceCellId[i]!
    if (riverCellId[i] !== NO_VALUE) sub.riverCellId = riverCellId[i]!
    if (riverDistanceMeters[i] !== 0) sub.riverDistanceMeters = riverDistanceMeters[i]!
    if (riverWidthMeters[i] !== 0) sub.riverWidthMeters = riverWidthMeters[i]!
    if (floodplain[i] !== 0) sub.floodplain = true
    if (structure[i] !== 0) sub.structure = structures[structure[i]! - 1]!
    if (terrainEdit[i] !== 0) sub.terrainEdit = terrainEdits[terrainEdit[i]! - 1]!
    sub.tileId = subTileId[i]!
    subTiles.push(sub)
  }
  return subTiles
}
