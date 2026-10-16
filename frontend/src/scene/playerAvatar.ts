import * as THREE from 'three'

/**
 * 玩家的角色模型——目前是一個指向前方的圓錐（2026-10-16 決策：先放簡單幾何體，之後再換真的模型）。
 *
 * 在這之前畫面上沒有角色，鏡頭就是玩家。連續移動之後需要一個看得見的身體：
 * 少了它，橫移與轉身在畫面上只是「整個世界在動」，分不清是角色在動還是鏡頭在動。
 */
export interface PlayerAvatar {
  readonly object: THREE.Object3D
  /**
   * 每幀呼叫：把角色放到腳下的地表、朝向面向。
   * @param focus 腳下地表的場景座標。
   * @param forward 面向（單位切向量）。
   * @param lengthScene 這一幀的角色長度（場景單位）。
   */
  update(focus: THREE.Vector3, forward: THREE.Vector3, lengthScene: number): void
  dispose(): void
}

/**
 * 角色的大小跟著鏡頭距離縮放，在畫面上維持差不多大。
 *
 * 一開始用固定的 600 公尺，結果在預設 20 公里的鏡頭距離下只剩幾個像素、看不出朝向——
 * 拉遠會消失、拉近又會擋住整個畫面。在還沒有真的角色模型之前，它的用途是「標出位置與面向」，
 * 所以比照地圖上的定位標記，維持固定的螢幕大小比較合用。
 */
export function createPlayerAvatar(): PlayerAvatar {
  // 幾何體做成長度 1，每幀再依實際大小縮放
  const radius = 0.3

  // 圓錐預設尖端朝 +Y。轉成尖端朝 −Z，搭配下面「z 軸＝後方」的基底，尖端就會指向前方。
  const geometry = new THREE.ConeGeometry(radius, 1, 16)
  geometry.rotateX(-Math.PI / 2)
  const material = new THREE.MeshStandardMaterial({
    color: 0xffcc33,
    emissive: 0x553300, // 夜晚也要看得到
    roughness: 0.6,
  })
  const mesh = new THREE.Mesh(geometry, material)
  const object = new THREE.Group()
  object.add(mesh)
  object.matrixAutoUpdate = false

  const normal = new THREE.Vector3()
  const right = new THREE.Vector3()
  const back = new THREE.Vector3()
  const lifted = new THREE.Vector3()
  const scratchScale = new THREE.Vector3()

  function update(focus: THREE.Vector3, forward: THREE.Vector3, lengthScene: number) {
    normal.copy(focus).normalize()
    right.crossVectors(forward, normal).normalize()
    back.copy(forward).negate()

    // 右手座標基底：x＝右、y＝上（地表法線）、z＝後。
    // right × normal = −forward = back，所以這組基底是右手的，不會鏡像。
    lifted.copy(focus).addScaledVector(normal, radius * lengthScene)
    object.matrix
      .makeBasis(right, normal, back)
      .scale(scratchScale.setScalar(lengthScene))
      .setPosition(lifted)
    object.matrixWorldNeedsUpdate = true
  }

  return {
    object,
    update,
    dispose() {
      geometry.dispose()
      material.dispose()
    },
  }
}
