import * as THREE from 'three'
import type { TileDto } from '@/types/tile'
import { latLonToVector3, tangentBasisAt } from './sphereCoordinates'

export interface TileMeshOptions {
  /** 場景中星球的顯示半徑（場景單位，不是公尺）。 */
  renderRadius: number
  /** 星球實際半徑（公尺），用來把Tile的真實邊長換算成場景比例。 */
  planetRadiusMeters: number
  /** Tile邊長（公尺），需與後端生成時的設定一致（預設1公里）。 */
  edgeLengthMeters?: number
}

const outlineMaterial = new THREE.LineBasicMaterial({ color: 0xffe066, linewidth: 2 })

/**
 * 地形起伏的視覺誇張倍率：ElevationLevel每級5公尺，相對星球半徑(~1851公里)是極小的比例，
 * 照實際比例畫幾乎看不出高低差，所以垂直方向乘一個誇張倍率讓山地/海溝的相對起伏看得出來
 * （水平方向的Tile大小不變，純粹是視覺誇張，不影響實際地理資料）。
 *
 * export出來是為了讓HUD能誠實揭露「畫面上的起伏被放大幾倍」——不揭露的話很容易把
 * 誇張後的陡峭程度誤當成真實地形。
 */
export const ELEVATION_EXAGGERATION = 8

/** 每個 ElevationLevel 代表幾公尺。HUD 換算海拔時要用同一個值，不要再寫死一份。 */
export const METERS_PER_ELEVATION_LEVEL = 5

/** 把Tile的海拔換算成場景單位的徑向偏移（含視覺誇張倍率）。 */
function elevationOffset(tile: TileDto, options: TileMeshOptions): number {
  const elevationMeters = tile.elevationLevel * METERS_PER_ELEVATION_LEVEL
  return (elevationMeters / options.planetRadiusMeters) * options.renderRadius * ELEVATION_EXAGGERATION
}

/**
 * 把鄰居依「在tile切平面上的方位角」排序，回傳排好序的鄰居。
 *
 * 後端 `neighborIds` 的順序來自 ITileNeighborResolver 的推導過程，**不保證是繞著格子的角度順序**，
 * 所以一定要自己排——順序錯了角落就會連成星形而不是凸多邊形。
 */
function sortNeighboursByAngle(
  tile: TileDto,
  neighbours: readonly TileDto[],
  options: TileMeshOptions,
): TileDto[] {
  const center = latLonToVector3(tile.latitude, tile.longitude, options.renderRadius)
  const { north, east } = tangentBasisAt(center)

  return [...neighbours].sort((a, b) => angleOf(a) - angleOf(b))

  function angleOf(neighbour: TileDto): number {
    const to = latLonToVector3(neighbour.latitude, neighbour.longitude, options.renderRadius).sub(center)
    return Math.atan2(to.dot(north), to.dot(east))
  }
}

/**
 * 三個互相相鄰的格子共用的那個角落的「方向」（單位向量，未含海拔）。
 *
 * Goldberg多面體是測地線球的對偶：格子中心＝測地線的頂點，格子角落＝相鄰三頂點所圍三角面的中心。
 * 這裡用三點的球面重心當那個中心——對Goldberg的對偶三角形（接近正三角形）而言重心與外心幾乎重合。
 *
 * **密合性的關鍵不在於這個公式幾何上有多精確，而在於三個共用這個角的格子都用同樣的公式、
 * 同樣的三個輸入去算**，所以必定得到同一個點，接縫由建構方式保證，不是靠調參數湊出來的。
 * 也因此這裡刻意不做任何「依呼叫者是誰」而改變的處理。
 */
export function sharedCornerDirection(
  a: THREE.Vector3,
  b: THREE.Vector3,
  c: THREE.Vector3,
  target: THREE.Vector3,
): THREE.Vector3 {
  return target.copy(a).add(b).add(c).normalize()
}

/** computeTilePolygon 的回傳內容。 */
export interface TilePolygon {
  center: THREE.Vector3
  normal: THREE.Vector3
  /** 角落頂點，依繞著格子的角度順序排列。 */
  ring: THREE.Vector3[]
  sideCount: number
  /**
   * ring[i] 到 ring[i+1] 這條邊「對面」的鄰居；沒有鄰居資料時為 null。
   * 用來決定要不要補側面牆（只有比鄰居高的那一格畫牆，每面牆才會剛好畫一次）。
   */
  edgeNeighbours: (TileDto | null)[]
}

/**
 * 算出Tile的中心點與六（或五）個角落頂點。
 *
 * 有提供**完整**鄰居時走「共用角落」：角落由相鄰三格一起決定，因此相鄰格算出來的角落完全相同，
 * 拼起來是真正密合的蜂窩。沒有鄰居（或鄰居不齊，例如patch最外圈）時退回舊的近似法：
 * 以固定半徑、固定朝向畫一個正多邊形——那個畫法的朝向是任意的（相對真實蜂窩朝向差達±22°~29°，
 * 且隨地區變化），三格交會處會對不齊而留下三角形小洞，所以只當作邊緣格的退路。
 *
 * export出來給 tilePatchMesh.ts 的合併geometry用——地形mesh與鄰居外框(buildTileOutline)一定要用
 * 同一份頂點數學，各自複製一份的話黃色外框會跟腳下的格子對不齊。
 */
export function computeTilePolygon(
  tile: TileDto,
  options: TileMeshOptions,
  neighbours?: readonly TileDto[],
): TilePolygon {
  const baseCenter = latLonToVector3(tile.latitude, tile.longitude, options.renderRadius)
  const { normal } = tangentBasisAt(baseCenter)
  const offset = elevationOffset(tile, options)
  const center = baseCenter.clone().addScaledVector(normal, offset)

  const sideCount = tile.shape === 'Pentagon' ? 5 : 6

  if (neighbours && neighbours.length === sideCount) {
    return computeSharedCornerPolygon(tile, options, neighbours, center, normal, sideCount, offset)
  }
  return computeApproximatePolygon(tile, options, center, normal, sideCount)
}

/** 共用角落：角落＝(自己, 鄰居i, 鄰居i+1) 三格的球面重心，所以相鄰格會算出同一點。 */
function computeSharedCornerPolygon(
  tile: TileDto,
  options: TileMeshOptions,
  neighbours: readonly TileDto[],
  center: THREE.Vector3,
  normal: THREE.Vector3,
  sideCount: number,
  offset: number,
): TilePolygon {
  const sorted = sortNeighboursByAngle(tile, neighbours, options)

  // 角落方向一律用「未加海拔」的單位向量算，海拔最後才乘上去——
  // 否則相鄰格因為海拔不同會算出不同的角落方向，密合性就沒了。
  const selfUnit = latLonToVector3(tile.latitude, tile.longitude, 1)
  const neighbourUnits = sorted.map((n) => latLonToVector3(n.latitude, n.longitude, 1))

  const ring: THREE.Vector3[] = []
  const edgeNeighbours: (TileDto | null)[] = []
  const cornerRadius = options.renderRadius + offset

  for (let i = 0; i < sideCount; i++) {
    const next = (i + 1) % sideCount
    const direction = sharedCornerDirection(
      selfUnit,
      neighbourUnits[i]!,
      neighbourUnits[next]!,
      new THREE.Vector3(),
    )
    ring.push(direction.multiplyScalar(cornerRadius))
    // 角落i由(自己,Nᵢ,Nᵢ₊₁)決定、角落i+1由(自己,Nᵢ₊₁,Nᵢ₊₂)決定
    // → 這兩個角落之間那條邊，對面的鄰居是 Nᵢ₊₁。
    edgeNeighbours.push(sorted[next]!)
  }

  return { center, normal, ring, sideCount, edgeNeighbours }
}

/** 舊的近似法：固定半徑、朝向對齊當地east/north。只在鄰居資料不齊時使用。 */
function computeApproximatePolygon(
  tile: TileDto,
  options: TileMeshOptions,
  center: THREE.Vector3,
  normal: THREE.Vector3,
  sideCount: number,
): TilePolygon {
  const edgeLengthMeters = options.edgeLengthMeters ?? 1000
  const edgeLengthScene = (edgeLengthMeters / options.planetRadiusMeters) * options.renderRadius
  const { north, east } = tangentBasisAt(center)

  // 正六邊形的外接半徑等於邊長；正五邊形外接半徑 = 邊長 / (2*sin(pi/5))，讓五邊形跟六邊形共邊時邊長一致。
  const circumradius = sideCount === 5 ? edgeLengthScene / (2 * Math.sin(Math.PI / 5)) : edgeLengthScene

  const ring: THREE.Vector3[] = []
  for (let i = 0; i < sideCount; i++) {
    const angle = (i / sideCount) * Math.PI * 2
    ring.push(
      center
        .clone()
        .addScaledVector(east, Math.cos(angle) * circumradius)
        .addScaledVector(north, Math.sin(angle) * circumradius),
    )
  }

  return { center, normal, ring, sideCount, edgeNeighbours: ring.map(() => null) }
}

/**
 * 某一格「地表」在場景中的半徑（含誇張後的海拔）。
 *
 * 用在兩個地方，兩者都必須用同一個值：側面牆要往下延伸到鄰居的地表高度；
 * 鏡頭的 orbit target 也要落在玩家腳下的地表上——放在海平面的話，
 * 傾斜視角下鏡頭會瞄準地形「底下」的一個點，地形整片被推出畫面外。
 */
export function tileSurfaceRadius(tile: TileDto, options: TileMeshOptions): number {
  return options.renderRadius + elevationOffset(tile, options)
}

/**
 * 沿Tile邊界畫一圈亮色外框，用來標示玩家目前可以移動過去的合法鄰居格。
 * `neighbours` 一定要跟建地形mesh時傳的是同一份，否則外框會跟腳下的格子對不齊。
 */
export function buildTileOutline(
  tile: TileDto,
  options: TileMeshOptions,
  neighbours?: readonly TileDto[],
): THREE.LineLoop {
  const { ring } = computeTilePolygon(tile, options, neighbours)

  const positions = new Float32Array(ring.length * 3)
  ring.forEach((point, i) => positions.set([point.x, point.y, point.z], i * 3))

  const geometry = new THREE.BufferGeometry()
  geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3))

  return new THREE.LineLoop(geometry, outlineMaterial)
}
