<script setup lang="ts">
import { computed } from 'vue'
import { EDIT_TOOLS, useEditModeStore } from '@/stores/editMode'

/**
 * 編輯模式的工具列（2026-11-08）：畫面下方中央。關著的時候只有一個「編輯」按鈕。
 * 實際的點選在 scene/editController.ts。
 */
const edit = useEditModeStore()

const currentHint = computed(() => EDIT_TOOLS.find((t) => t.action === edit.tool)?.hint ?? '')
</script>

<template>
  <div class="edit-toolbar">
    <div v-if="edit.active" class="status">
      <span v-if="!edit.subTilesReady" class="warn">鏡頭拉近一點（看得到細格子時）才能編輯</span>
      <span v-else-if="edit.message" :class="{ warn: edit.messageIsError }">{{ edit.message }}</span>
      <span v-else>點一下或按著左鍵拖曳（右鍵拖曳轉鏡頭）：{{ currentHint }}</span>
    </div>
    <div class="row">
      <button
        type="button"
        class="toggle"
        :class="{ active: edit.active }"
        :title="edit.active ? '離開編輯模式' : '蓋房子、城堡、橋、路，修改河道'"
        @click="edit.setActive(!edit.active)"
      >
        {{ edit.active ? '× 結束編輯' : '🔨 編輯' }}
      </button>
      <template v-if="edit.active">
        <button
          v-for="tool in EDIT_TOOLS"
          :key="tool.action"
          type="button"
          :class="{ active: edit.tool === tool.action, demolish: tool.action === 'Demolish' }"
          :title="tool.hint"
          @click="edit.setTool(tool.action)"
        >
          {{ tool.label }}
        </button>
      </template>
    </div>
  </div>
</template>

<style scoped>
.edit-toolbar {
  position: fixed;
  bottom: 48px;
  left: 50%;
  transform: translateX(-50%);
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 6px;
  max-width: calc(100vw - 32px);
}

.row {
  display: flex;
  flex-wrap: wrap;
  justify-content: center;
  gap: 6px;
}

.status {
  padding: 4px 10px;
  background: rgba(0, 0, 0, 0.6);
  color: #fff;
  font-family: monospace;
  font-size: 12px;
  border-radius: 4px;
}

.warn {
  color: #ffc97f;
}

button {
  padding: 6px 10px;
  font-family: monospace;
  font-size: 12px;
  background: rgba(0, 0, 0, 0.55);
  color: #ddd;
  border: 1px solid rgba(255, 255, 255, 0.25);
  border-radius: 4px;
  cursor: pointer;
}

button:hover {
  border-color: rgba(255, 255, 255, 0.6);
  color: #fff;
}

button.active {
  background: rgba(60, 140, 220, 0.85);
  color: #fff;
  border-color: rgba(255, 255, 255, 0.6);
}

button.toggle.active {
  background: rgba(200, 120, 40, 0.85);
}

button.demolish.active {
  background: rgba(190, 60, 50, 0.85);
}
</style>
