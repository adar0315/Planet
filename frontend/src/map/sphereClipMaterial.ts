import * as THREE from 'three'

/**
 * 球面圓形裁切（近看細分的邊界，2026-11-07）：材質只畫球面上離某點「多遠以內」或「多遠以外」的部分。
 *
 * 小格歸屬離它最近的地塊，被取代的地塊整格藏起來的話，兩邊的邊界對不齊——
 * 小格的六角跟地塊的六角交錯，留下一圈半個小格寬的鋸齒縫。改成在著色器裡用同一個圓切：
 * 小格只畫圓內，地塊只畫圓（稍微往內縮）以外，邊界是一條平順的圓弧。
 *
 * 距離用「單位向量之間的弦長」比，不用 dot 的 cos：幾十公里在半徑兩千公里的星球上，cos 跟 1 只差 2e-4，
 * float32 只剩下約 600 公尺的解析度；弦長本身就是小數字，精度沒問題。
 */
export interface SphereClip {
  /** 圓心（單位向量）。 */
  readonly center: THREE.Vector3
  /** 設定圓的半徑（弧長換成的角度，弧度）。 */
  setAngle(radians: number): void
  /** 開關；關掉時整個材質照常畫。 */
  setEnabled(enabled: boolean): void
}

/**
 * 複製一份材質，加上球面圓形裁切。
 * @param base 原本的材質（會被複製，不會被修改）。
 * @param keep 'inside'：只畫圓內；'outside'：只畫圓外。
 */
export function withSphereClip<T extends THREE.Material>(
  base: T,
  keep: 'inside' | 'outside',
): { material: T; clip: SphereClip } {
  const material = base.clone() as T
  const uniforms = {
    uClipCenter: { value: new THREE.Vector3(1, 0, 0) },
    uClipChord: { value: 0 },
    uClipEnabled: { value: 0 },
  }
  material.onBeforeCompile = (shader) => {
    Object.assign(shader.uniforms, uniforms)
    shader.vertexShader = shader.vertexShader
      .replace('#include <common>', '#include <common>\nvarying vec3 vClipWorld;')
      .replace(
        '#include <project_vertex>',
        '#include <project_vertex>\nvClipWorld = (modelMatrix * vec4(transformed, 1.0)).xyz;',
      )
    const test = keep === 'inside' ? 'chord > uClipChord' : 'chord < uClipChord'
    shader.fragmentShader = shader.fragmentShader
      .replace(
        '#include <common>',
        '#include <common>\nvarying vec3 vClipWorld;\nuniform vec3 uClipCenter;\nuniform float uClipChord;\nuniform float uClipEnabled;',
      )
      .replace(
        'void main() {',
        `void main() {\n  if (uClipEnabled > 0.5) {\n    float chord = length(normalize(vClipWorld) - uClipCenter);\n    if (${test}) discard;\n  }`,
      )
  }
  material.customProgramCacheKey = () => `sphere-clip-${keep}`
  return {
    material,
    clip: {
      center: uniforms.uClipCenter.value,
      setAngle(radians) {
        uniforms.uClipChord.value = 2 * Math.sin(radians / 2)
      },
      setEnabled(enabled) {
        uniforms.uClipEnabled.value = enabled ? 1 : 0
      },
    },
  }
}
