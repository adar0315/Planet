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
   * @param flying 飛行中換成三角翼外型，並畫一條垂到地面的線。
   * @param ground 正下方地面的場景座標（只在飛行時用）。
   */
  update(
    focus: THREE.Vector3,
    forward: THREE.Vector3,
    lengthScene: number,
    flying: boolean,
    ground: THREE.Vector3,
  ): void
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
  const walkGeometry = new THREE.ConeGeometry(radius, 1, 16)
  walkGeometry.rotateX(-Math.PI / 2)

  // 飛行：三角錐壓扁成三角翼。底面三個頂點轉完後一個朝上、兩個朝左右，
  // 把上下壓扁就只剩左右兩個翼尖，從上方看是一個指向前方的三角形。
  const flyGeometry = new THREE.ConeGeometry(0.45, 1, 3)
  flyGeometry.rotateX(-Math.PI / 2)
  flyGeometry.scale(1, 0.15, 1)

  const material = new THREE.MeshStandardMaterial({
    color: 0xffcc33,
    emissive: 0x553300, // 夜晚也要看得到
    roughness: 0.6,
  })
  const body = new THREE.Mesh(walkGeometry, material)
  body.matrixAutoUpdate = false

  // 飛行時垂到地面的線，讓人看得出飛多高。直接用世界座標，所以不掛在 body 底下
  const lineGeometry = new THREE.BufferGeometry()
  const linePositions = new Float32Array(6)
  lineGeometry.setAttribute('position', new THREE.BufferAttribute(linePositions, 3))
  const lineMaterial = new THREE.LineBasicMaterial({
    color: 0xffcc33,
    transparent: true,
    opacity: 0.6,
  })
  const line = new THREE.Line(lineGeometry, lineMaterial)
  line.frustumCulled = false // 端點每幀都在變，包圍球不會跟著更新
  line.visible = false

  const object = new THREE.Group()
  object.add(body)
  object.add(line)

  const normal = new THREE.Vector3()
  const right = new THREE.Vector3()
  const back = new THREE.Vector3()
  const lifted = new THREE.Vector3()
  const scratchScale = new THREE.Vector3()

  function update(
    focus: THREE.Vector3,
    forward: THREE.Vector3,
    lengthScene: number,
    flying: boolean,
    ground: THREE.Vector3,
  ) {
    normal.copy(focus).normalize()
    right.crossVectors(forward, normal).normalize()
    back.copy(forward).negate()

    // 右手座標基底：x＝右、y＝上（地表法線）、z＝後。
    // right × normal = −forward = back，所以這組基底是右手的，不會鏡像。
    // 走路時往上抬半個身體，才不會一半埋在地裡；飛行時就在飛機的位置
    body.geometry = flying ? flyGeometry : walkGeometry
    lifted.copy(focus)
    if (!flying) lifted.addScaledVector(normal, radius * lengthScene)
    body.matrix
      .makeBasis(right, normal, back)
      .scale(scratchScale.setScalar(lengthScene))
      .setPosition(lifted)
    body.matrixWorldNeedsUpdate = true

    line.visible = flying
    if (flying) {
      focus.toArray(linePositions, 0)
      ground.toArray(linePositions, 3)
      lineGeometry.attributes.position!.needsUpdate = true
    }
  }

  return {
    object,
    update,
    dispose() {
      walkGeometry.dispose()
      flyGeometry.dispose()
      material.dispose()
      lineGeometry.dispose()
      lineMaterial.dispose()
    },
  }
}
