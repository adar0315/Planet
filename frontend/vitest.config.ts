import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vitest/config'

/**
 * 測試針對純邏輯模組：src/map（座標、鄰接、方向指派、合併geometry的索引對應）
 * 與 src/stores 裡不依賴元件的部分（例如自訂按鍵），
 * 這些是整個前端最容易寫錯又最難用肉眼發現的部分，而且完全不需要瀏覽器：
 * Three.js 的 BufferGeometry/Vector3 純粹是記憶體運算，不用 WebGL context，
 * 所以用預設的 node 環境即可，不必裝 jsdom。
 *
 * 刻意不載入 vite.config.ts 的 plugin（vue、devtools）——這裡沒有測試 .vue 元件，
 * 載進來只會拖慢啟動。共用的只有 @ 路徑別名。
 */
export default defineConfig({
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  test: {
    environment: 'node',
    include: ['src/**/*.spec.ts'],
  },
})
