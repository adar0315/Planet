import { describe, expect, it } from 'vitest'
import * as THREE from 'three'
import type { SubTileDto } from '@/types/tile'
import {
  castleGroups,
  BRIDGE_CLEARANCE_METERS,
  bridgeDeckRadius,
  buildConstructionLayer,
  connectedNeighbours,
} from './constructionModels'
import { tileFloorRadius, tileSurfaceRadius, type TileMeshOptions } from './tileMeshBuilder'

const options: TileMeshOptions = { renderRadius: 50, planetRadiusMeters: 1_852_000, edgeLengthMeters: 250 }

function sub(id: number, lon: number, extra: Partial<SubTileDto> = {}): SubTileDto {
  return {
    tileId: id,
    parentTileId: 1,
    shape: 'Hexagon',
    faceIndex: 0,
    latitude: 10,
    longitude: lon,
    terrain: 'Grassland',
    heightCategory: 0,
    elevationLevel: 20,
    seaLevel: 0,
    ...extra,
  }
}

/** 相鄰小格的格心約差 433 公尺（經度約 0.0136°，在北緯 10° 再除以 cos）。 */
const STEP = (433 / 1_852_000) * (180 / Math.PI) / Math.cos((10 * Math.PI) / 180)

describe('constructionModels', () => {
  it('roads connect to roads, bridges and buildings; bridges only to roads and bridges', () => {
    const road = sub(1, 0, { structure: 'Road' })
    const house = sub(2, STEP, { structure: 'House' })
    const bridge = sub(3, -STEP, { structure: 'Bridge' })
    const empty = sub(4, 2 * STEP)
    expect(connectedNeighbours(road, [house, bridge, empty]).map((s) => s.tileId)).toEqual([2, 3])
    expect(connectedNeighbours(bridge, [road, house]).map((s) => s.tileId)).toEqual([1])
  })

  it('bridge deck clears the water and is not below a connected bank road', () => {
    const water = sub(1, 0, { structure: 'Bridge', terrain: 'Freshwater', elevationLevel: 10, seaLevel: 14 })
    const clearance = (BRIDGE_CLEARANCE_METERS / options.planetRadiusMeters) * options.renderRadius
    expect(bridgeDeckRadius(water, [], options)).toBeCloseTo(tileFloorRadius(water, options) + clearance, 10)

    const highBank = sub(2, STEP, { structure: 'Road', elevationLevel: 200 })
    expect(bridgeDeckRadius(water, [highBank], options)).toBeGreaterThan(tileSurfaceRadius(highBank, options))
  })

  it('builds one mesh per material, sitting on the tile', () => {
    const subs = [sub(1, 0, { structure: 'Castle' }), sub(2, STEP, { structure: 'House' }), sub(3, 2 * STEP)]
    const layer = buildConstructionLayer(subs, () => [], options)
    // 城堡：石牆、屋頂；房子：牆、屋頂
    expect(layer.group.children).toHaveLength(4)
    const surface = tileSurfaceRadius(subs[0]!, options)
    for (const child of layer.group.children) {
      const geometry = (child as THREE.Mesh).geometry
      geometry.computeBoundingBox()
      const position = geometry.getAttribute('position')
      let minRadius = Infinity
      let maxRadius = 0
      const v = new THREE.Vector3()
      for (let i = 0; i < position.count; i++) {
        const r = v.fromBufferAttribute(position, i).length()
        minRadius = Math.min(minRadius, r)
        maxRadius = Math.max(maxRadius, r)
      }
      // 底部貼著地面（誤差是切平面離開球面的那一點點），最高不超過 300 公尺
      expect(minRadius).toBeGreaterThan(surface - 1e-5)
      expect(maxRadius).toBeLessThan(surface + (300 / options.planetRadiusMeters) * options.renderRadius)
    }
    layer.dispose()
    expect(layer.group.children).toHaveLength(0)
  })

  it('faces wind outward (the local frame is not mirrored)', () => {
    // 鏡射的變換會讓三角形的繞向反過來，但法向量照樣朝外：畫出來是內側（背面被剔除）
    const subs = [
      sub(1, 0, { structure: 'House' }),
      sub(2, STEP, { structure: 'Road' }),
      sub(3, 2 * STEP, { structure: 'Castle' }),
      sub(4, 3 * STEP, { structure: 'Castle' }),
      sub(5, 4 * STEP, { structure: 'Farm' }),
      sub(6, 5 * STEP, { structure: 'Orchard' }),
      sub(7, 6 * STEP, { structure: 'Pasture' }),
    ]
    const layer = buildConstructionLayer(subs, (s) => subs.filter((o) => Math.abs(o.tileId - s.tileId) === 1), options, () => ({
      season: '秋',
      snow: false,
    }))
    const a = new THREE.Vector3()
    const b = new THREE.Vector3()
    const c = new THREE.Vector3()
    const n = new THREE.Vector3()
    for (const child of layer.group.children) {
      const geometry = (child as THREE.Mesh).geometry
      const position = geometry.getAttribute('position')
      const normal = geometry.getAttribute('normal')
      const index = geometry.getIndex()
      const count = index ? index.count : position.count
      for (let t = 0; t < count; t += 3) {
        const [i, j, k] = index ? [index.getX(t), index.getX(t + 1), index.getX(t + 2)] : [t, t + 1, t + 2]
        a.fromBufferAttribute(position, i)
        b.fromBufferAttribute(position, j)
        c.fromBufferAttribute(position, k)
        const face = b.sub(a).cross(c.sub(a))
        if (face.lengthSq() === 0) continue
        n.fromBufferAttribute(normal, i)
        expect(face.dot(n)).toBeGreaterThan(0)
      }
    }
  })

  it('adjacent castle cells form one castle, keep in the best-connected cell', () => {
    const line = [1, 2, 3, 4].map((id) => sub(id, id * STEP, { structure: 'Castle' }))
    const apart = sub(9, 20 * STEP, { structure: 'Castle' })
    const all = [...line, apart, sub(5, 5 * STEP)]
    const neighbours = (s: SubTileDto) => all.filter((o) => Math.abs(o.tileId - s.tileId) === 1)
    const groups = castleGroups(all, neighbours)
    expect(groups.map((g) => g.map((s) => s.tileId).sort())).toEqual([[1, 2, 3, 4], [9]])
    expect(groups[0]![0]!.tileId).toBe(2) // 2 與 3 都有兩個城堡鄰居，取編號小的
  })

  it('farm, orchard and pasture change with the season', () => {
    const subs = [sub(1, 0, { structure: 'Farm' }), sub(2, 3 * STEP, { structure: 'Orchard' })]
    const colours = (season: '春' | '夏' | '秋' | '冬', snow = false) => {
      const layer = buildConstructionLayer(subs, () => [], options, () => ({ season, snow }))
      const result = layer.group.children
        .map((c) => ((c as THREE.Mesh).material as THREE.MeshLambertMaterial).color.getHex())
        .sort()
      layer.dispose()
      return result
    }
    expect(colours('春')).not.toEqual(colours('秋'))
    // 冬天果樹落葉：少了樹冠那一種顏色；秋天多了果子
    expect(colours('冬').length).toBeLessThan(colours('夏').length)
    expect(colours('秋').length).toBeGreaterThan(colours('夏').length)
    expect(colours('冬', true)).toContain(0xeef2f5)
  })

  it('a lone sub without structures builds nothing', () => {
    expect(buildConstructionLayer([sub(1, 0)], () => [], options).group.children).toHaveLength(0)
  })
})
