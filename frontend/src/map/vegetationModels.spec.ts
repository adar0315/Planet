import { describe, expect, it } from 'vitest'
import * as THREE from 'three'
import type { SubTileDto } from '@/types/tile'
import { latLonToVector3 } from './sphereCoordinates'
import { tileSurfaceRadius } from './tileMeshBuilder'
import {
  TREE_HEIGHTS_METERS,
  TREES_PER_SUBTILE,
  buildVegetationLayer,
  crownColor,
  forestTrees,
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
