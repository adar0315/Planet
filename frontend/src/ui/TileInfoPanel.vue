<script setup lang="ts">
import { computed } from 'vue'
import { useGameStateStore } from '@/stores/gameState'
import { useTerrainStyleStore } from '@/stores/terrainStyles'
import { useTileInspectorStore } from '@/stores/tileInspector'
import { describeTile } from '@/map/tileInfo'

/** 走動視角裡左鍵點到的方塊的資訊（2026-11-17）。內容由 map/tileInfo.ts 的 describeTile 決定。 */
const gameState = useGameStateStore()
const terrainStyleStore = useTerrainStyleStore()
const inspector = useTileInspectorStore()

const rows = computed(() => {
  const tile = inspector.selected
  if (!tile) return []
  const seasons = tile.sourceCellId !== undefined ? gameState.seasonsOf(tile.sourceCellId) : undefined
  return describeTile(
    tile,
    inspector.isSubTile,
    (terrain) => terrainStyleStore.getDisplayName(terrain),
    seasons,
    gameState.gameTime,
  )
})
</script>

<template>
  <div v-if="inspector.selected" class="tile-info">
    <div class="header">
      <span>方塊資訊</span>
      <button type="button" class="close" title="關閉" @click="inspector.clear()">×</button>
    </div>
    <table>
      <tbody>
        <tr v-for="row in rows" :key="row.label">
          <th>{{ row.label }}</th>
          <td>{{ row.value }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>

<style scoped>
/* 右側、在小地球（右下角）上面：右上角是模式切換與管理面板，左邊是座標 HUD 與指北針 */
.tile-info {
  position: fixed;
  right: 12px;
  bottom: 180px;
  max-width: 340px;
  padding: 8px 10px;
  color: #fff;
  background: rgba(0, 0, 0, 0.6);
  border: 1px solid rgba(77, 216, 255, 0.6);
  border-radius: 4px;
  font-family: monospace;
  font-size: 12px;
}

.header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 4px;
  color: #4dd8ff;
}

.close {
  border: none;
  background: transparent;
  color: #fff;
  font-size: 16px;
  line-height: 1;
  cursor: pointer;
}

table {
  border-collapse: collapse;
}

th {
  padding: 1px 8px 1px 0;
  text-align: left;
  font-weight: normal;
  color: #aaa;
  white-space: nowrap;
  vertical-align: top;
}

td {
  padding: 1px 0;
}
</style>
