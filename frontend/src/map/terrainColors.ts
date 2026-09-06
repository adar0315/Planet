/**
 * 地形顏色的唯一權威來源是資料庫的 `TerrainStyles` 表（透過 `/api/terrain-styles` 取得，
 * 見 `stores/terrainStyles.ts`）。**這裡刻意不再保留一份完整的調色盤複本。**
 *
 * 原本這個檔案有一份18種地形的硬編碼顏色當作 fallback，問題是：
 * 它只在後端拿不到時才會用到，所以兩份資料漂移了平常完全看不出來——
 * 只有在後端剛好掛掉的那一刻，畫面才會突然變成另一組顏色。
 * 與其維護一份注定會慢慢走偏又驗不出來的複本，不如讓「拿不到資料」這件事**看得出來**：
 * 統一顯示這個中性色，並由 UI 顯示警告（見 `stores/terrainStyles.ts` 的 loadError）。
 *
 * 後端在同樣情況下也是用單一 fallback 色（見 OverviewTextureGenerator.FallbackColor），
 * 兩邊行為一致。
 */
export const UNKNOWN_TERRAIN_COLOR = 0x8a8a8a
