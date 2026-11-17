import { describe, expect, it } from 'vitest'
import * as THREE from 'three'
import type { SubTileDto } from '@/types/tile'
import { latLonToVector3 } from './sphereCoordinates'
import { tileSurfaceRadius } from './tileMeshBuilder'
import {
  GRASS_HEIGHTS_METERS,
  GRASS_TUFTS_PER_SUBTILE,
  TREE_HEIGHTS_METERS,
  TREES_PER_SUBTILE,
  buildVegetationLayer,
  crownColor,
  forestTrees,
  grassColor,
  grassTufts,
  treeKindAt,
} from './vegetationModels'

const options = { renderRadius: 100, planetRadiusMeters: 1_852_259.92, edgeLengthMeters: 250 }

function forest(heightCategory: number, tileId = 1000555357508): SubTileDto {
  return {
    tileId,
    parentTileId: 17574965,
    shape: 'Hexagon',
    faceIndex: 17,
    latitude: 12.3,
    longitude: 45.6,
    terrain: 'Forest',
    heightCategory,
    elevationLevel: 40,
    seaLevel: 0,
  }
}

describe('forestTrees', () => {
  it('不是林地就沒有樹', () => {
    expect(forestTrees({ ...forest(7), terrain: 'Grassland' }, 15)).toEqual([])
  })

  it('heightCategory＝高度×2＋疏密：奇數是密林、偶數是疏林', () => {
    expect(forestTrees(forest(0), 15)).toHaveLength(TREES_PER_SUBTILE.sparse)
    expect(forestTrees(forest(1), 15)).toHaveLength(TREES_PER_SUBTILE.dense)
    expect(forestTrees(forest(6), 15)).toHaveLength(TREES_PER_SUBTILE.sparse)
    expect(forestTrees(forest(7), 15)).toHaveLength(TREES_PER_SUBTILE.dense)
  })

  it('樹高照高度分級（±20%），越高的分級樹越高', () => {
    for (let level = 0; level < 4; level++) {
      const base = TREE_HEIGHTS_METERS[level]!
      for (const tree of forestTrees(forest(level * 2 + 1), 15)) {
        expect(tree.height).toBeGreaterThanOrEqual(base * 0.8)
        expect(tree.height).toBeLessThanOrEqual(base * 1.2)
      }
    }
  })

  it('樹都種在小格裡（離中心 170 公尺內）', () => {
    for (const tree of forestTrees(forest(7), 15)) expect(Math.hypot(tree.x, tree.z)).toBeLessThanOrEqual(170)
  })

  it('同一格每次都一樣，不同格不一樣', () => {
    expect(forestTrees(forest(5), 15)).toEqual(forestTrees(forest(5), 15))
    expect(forestTrees(forest(5, 1), 15)).not.toEqual(forestTrees(forest(5, 2), 15))
  })
})

describe('treeKindAt', () => {
  it('寒冷全是針葉、熱帶全是常綠、溫帶依年均溫混合', () => {
    expect(treeKindAt(-5, 0.99)).toBe('conifer')
    expect(treeKindAt(25, 0)).toBe('evergreen')
    expect(treeKindAt(15, 0)).toBe('deciduous')
    // 6.5°C：針葉佔一半
    expect(treeKindAt(6.5, 0.49)).toBe('conifer')
    expect(treeKindAt(6.5, 0.51)).toBe('deciduous')
  })

  it('沒有四季資料當成溫帶落葉林', () => {
    expect(treeKindAt(undefined, 0)).toBe('deciduous')
  })
})

describe('crownColor', () => {
  it('落葉樹冬天沒有樹冠，常綠與針葉照樣有', () => {
    expect(crownColor('deciduous', '冬', false, 0.5)).toBeNull()
    expect(crownColor('evergreen', '冬', false, 0.5)).not.toBeNull()
    expect(crownColor('conifer', '冬', false, 0.5)).not.toBeNull()
  })

  it('針葉樹積雪時變白', () => {
    const plain = new THREE.Color(crownColor('conifer', '冬', false, 0.5)!)
    const snowy = new THREE.Color(crownColor('conifer', '冬', true, 0.5)!)
    expect(snowy.r + snowy.g + snowy.b).toBeGreaterThan(plain.r + plain.g + plain.b + 1)
  })

  it('落葉樹秋天偏紅黃（紅大於綠），夏天偏綠', () => {
    const autumn = new THREE.Color(crownColor('deciduous', '秋', false, 0.1)!)
    const summer = new THREE.Color(crownColor('deciduous', '夏', false, 0.1)!)
    expect(autumn.r).toBeGreaterThan(autumn.g)
    expect(summer.g).toBeGreaterThan(summer.r)
  })
})

describe('buildVegetationLayer', () => {
  const summer = () => ({ season: '夏' as const, snow: false, annualMeanC: 15 })

  it('每棵樹一根樹幹，夏天每棵樹都有樹冠', () => {
    const subs = [forest(1, 11), forest(0, 12)]
    const layer = buildVegetationLayer(subs, options, summer)
    expect(layer.treeCount).toBe(TREES_PER_SUBTILE.dense + TREES_PER_SUBTILE.sparse)
    const meshes = layer.group.children as THREE.InstancedMesh[]
    const instances = meshes.map((m) => m.count)
    expect(instances).toEqual([layer.treeCount, layer.treeCount]) // 樹幹、闊葉樹冠
    layer.dispose()
  })

  it('落葉林冬天只剩樹幹', () => {
    const layer = buildVegetationLayer([forest(1)], options, () => ({ season: '冬', snow: false, annualMeanC: 15 }))
    expect(layer.group.children).toHaveLength(1)
    layer.dispose()
  })

  it('樹幹底部在小格的地面上、在格心附近', () => {
    const sub = forest(3)
    const layer = buildVegetationLayer([sub], options, summer)
    const trunks = layer.group.children[0] as THREE.InstancedMesh
    const matrix = new THREE.Matrix4()
    const position = new THREE.Vector3()
    const center = latLonToVector3(sub.latitude, sub.longitude, 1)
    const surface = tileSurfaceRadius(sub, options)
    for (let i = 0; i < trunks.count; i++) {
      trunks.getMatrixAt(i, matrix)
      position.setFromMatrixPosition(matrix)
      // 離球心的距離≈地面（切平面上偏一點，誤差遠小於一公尺）
      expect(Math.abs(position.length() - surface)).toBeLessThan(1e-3)
      // 離格心不超過 170 公尺
      expect(position.clone().normalize().angleTo(center) * options.planetRadiusMeters).toBeLessThan(171)
    }
    layer.dispose()
  })

  it('樹不接受點選（點選照舊點到底下的小格）', () => {
    const layer = buildVegetationLayer([forest(1)], options, summer)
    const hits: THREE.Intersection[] = []
    for (const child of layer.group.children) child.raycast(new THREE.Raycaster(), hits)
    expect(hits).toHaveLength(0)
    layer.dispose()
  })
})

describe('grassTufts', () => {
  const grassland = (heightCategory: number, tileId = 77): SubTileDto => ({ ...forest(0, tileId), terrain: 'Grassland', heightCategory })

  it('不是草地就沒有草', () => {
    expect(grassTufts(forest(3))).toEqual([])
  })

  it('每格固定叢數，草高照分級（±25%），都比最矮的樹矮', () => {
    for (let level = 0; level < GRASS_HEIGHTS_METERS.length; level++) {
      const tufts = grassTufts(grassland(level))
      expect(tufts).toHaveLength(GRASS_TUFTS_PER_SUBTILE)
      for (const t of tufts) {
        expect(t.height).toBeGreaterThanOrEqual(GRASS_HEIGHTS_METERS[level]! * 0.75)
        expect(t.height).toBeLessThanOrEqual(GRASS_HEIGHTS_METERS[level]! * 1.25)
        expect(t.height).toBeLessThan(TREE_HEIGHTS_METERS[0] * 0.8)
        expect(Math.hypot(t.x, t.z)).toBeLessThanOrEqual(190)
      }
    }
  })

  it('同一格每次都一樣', () => {
    expect(grassTufts(grassland(2))).toEqual(grassTufts(grassland(2)))
  })
})

describe('grassColor', () => {
  it('積雪時矮草被埋住、高草露出來', () => {
    expect(grassColor('冬', true, 0, 1, 0.5)).toBeNull()
    expect(grassColor('冬', true, 0, 2, 0.5)).not.toBeNull()
  })

  it('溫帶冬天枯黃（紅大於綠的比例比夏天高），熱帶冬天照樣綠', () => {
    const ratio = (hex: number) => {
      const c = new THREE.Color(hex)
      return c.r / c.g
    }
    expect(ratio(grassColor('冬', false, 12, 2, 0.5)!)).toBeGreaterThan(ratio(grassColor('夏', false, 12, 2, 0.5)!))
    expect(grassColor('冬', false, 25, 2, 0.5)).toBe(grassColor('夏', false, 25, 2, 0.5))
  })
})

describe('buildVegetationLayer（草）', () => {
  const grassland = (heightCategory: number, tileId: number): SubTileDto => ({ ...forest(0, tileId), terrain: 'Grassland', heightCategory })

  it('草地長草、不長樹；雪埋住矮草就沒有', () => {
    const summer = buildVegetationLayer([grassland(2, 5)], options, () => ({ season: '夏', snow: false, annualMeanC: 12 }))
    expect(summer.grassCount).toBe(GRASS_TUFTS_PER_SUBTILE)
    expect(summer.treeCount).toBe(0)
    summer.dispose()
    const buried = buildVegetationLayer([grassland(0, 5)], options, () => ({ season: '冬', snow: true, annualMeanC: 0 }))
    expect(buried.grassCount).toBe(0)
    expect(buried.group.children).toHaveLength(0)
    buried.dispose()
  })

  it('草叢的葉片雙面著色（葉片很薄，背面也要看得到）', () => {
    const layer = buildVegetationLayer([grassland(4, 6)], options, () => ({ season: '夏', snow: false, annualMeanC: 12 }))
    const mesh = layer.group.children[0] as THREE.InstancedMesh
    expect((mesh.material as THREE.Material).side).toBe(THREE.DoubleSide)
    layer.dispose()
  })
})
