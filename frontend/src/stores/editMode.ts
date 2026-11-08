import { defineStore } from 'pinia'
import { ref } from 'vue'
import type { ConstructionAction } from '@/types/tile'

/** 工具列上的一個工具。 */
export interface EditTool {
  action: ConstructionAction
  label: string
  /** 滑鼠移上去時的說明。 */
  hint: string
}

/** 編輯模式的工具（2026-11-08）。順序就是工具列的順序。 */
export const EDIT_TOOLS: readonly EditTool[] = [
  { action: 'House', label: '房子', hint: '蓋在陸地上' },
  {
    action: 'Castle',
    label: '城堡',
    hint: '蓋在陸地上；點相鄰的格子會連成同一座城（最多 7 格），城牆沿整座城的外緣',
  },
  { action: 'Road', label: '道路', hint: '蓋在陸地上，會跟相鄰的路、橋、房子、城堡接起來' },
  { action: 'Farm', label: '農田', hint: '蓋在陸地上，隨季節變色：春綠、夏深綠、秋金黃、冬天空田' },
  { action: 'Orchard', label: '果園', hint: '蓋在陸地上：春天開花、秋天結果、冬天落葉' },
  { action: 'Pasture', label: '牧場', hint: '蓋在陸地上，有圍欄與羊；相鄰的牧場連成一片，冬天草枯黃' },
  { action: 'Bridge', label: '橋', hint: '蓋在水上（河、湖、海、挖出來的河道），兩岸接上道路' },
  { action: 'Channel', label: '挖河道', hint: '陸地挖成一段有水的河道；在填平過的地方挖等於復原' },
  { action: 'Fill', label: '填平', hint: '把河、湖、海填成陸地；在挖出來的河道上填等於復原' },
  { action: 'Demolish', label: '拆除', hint: '先拆建築物；沒有建築物時把改過的地形復原' },
]

/**
 * 編輯模式的狀態：開關、目前的工具、給玩家看的訊息。
 * 實際的點選與修改在 scene/editController.ts，模型在 map/constructionModels.ts。
 */
export const useEditModeStore = defineStore('editMode', () => {
  const active = ref(false)
  const tool = ref<ConstructionAction>('House')
  /** 最近一次操作的結果或錯誤；null＝不顯示。 */
  const message = ref<string | null>(null)
  const messageIsError = ref(false)
  /** 小格有沒有載入（鏡頭夠近）：沒有的話不能編輯。由場景每幀更新。 */
  const subTilesReady = ref(false)
  /** 有修改正在送往後端。 */
  const busy = ref(false)

  function setActive(value: boolean) {
    active.value = value
    if (!value) message.value = null
  }

  function setTool(action: ConstructionAction) {
    tool.value = action
  }

  function showMessage(text: string | null, isError = false) {
    message.value = text
    messageIsError.value = isError
  }

  return { active, tool, message, messageIsError, subTilesReady, busy, setActive, setTool, showMessage }
})
