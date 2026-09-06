/**
 * PlanetScene 用到的固定常數，集中放這裡跟其他邏輯模組（cameraRig、patchManager…）共用，
 * 避免同一個數字（例如 RENDER_RADIUS）散落在好幾個檔案裡各自寫一份、之後各自漂移。
 */

/** 場景中星球的顯示半徑（場景單位，與公尺無關），Tile大小依此比例換算。 */
export const RENDER_RADIUS = 50

export const REALTIME_TICK_MS = 3000

/** 預設視角：從正上方往「南」偏斜的俯角，比純垂直俯視更有立體感、看得到地形起伏。 */
export const DEFAULT_TILT_DEG = 60

/**
 * 預設鏡頭到玩家所在格的直線距離（公尺，星球實際比例）。
 * 注意「高度」不是能獨立設定的參數——傾斜角與距離一旦決定，高度就固定了
 * （60°＋20公里 ⇒ 高度約 10,080 公尺）。HUD 上三個數字都會顯示，但可調的只有前兩個。
 */
export const DEFAULT_CAMERA_DISTANCE_METERS = 20_000

/**
 * 一次載入的Tile數。後端的查詢框會依這個數字反推（見TileRepository.cs的NearbyQueryOptions），
 * 讓傾斜視角+縮小時畫面範圍內都有地形，不會露出黑色空白。
 * 合併成單一geometry後，載入量對渲染成本幾乎沒有影響（永遠是1次draw call），
 * 所以刻意載大一點，換取「連走很多步都不必再打網路」。
 * 實測（相對 6000 格時的半徑 64 公里）：
 *   patch半徑約187公里——已經接近60度視角下的地平線距離（約193公里），所以遠處看得到的
 *   幾乎都是真正的格子，貼圖遠景只補最後一小段。
 *   代價：換patch時卡頓約217ms、壓縮後傳輸約3.1MB、後端查詢約1秒、頂點緩衝區約67MB。
 *   這些都在背景進行且舊patch持續可用，但卡頓是同步的，再往上加會開始有感。
 * 後端的 MaxCount / MaxDeltaDegrees 已配合放寬（見 NearbyQueryOptions）。
 */
export const NEARBY_TILE_COUNT = 50_000

/**
 * 玩家離已載入範圍邊緣剩幾步以內就開始在背景補載下一批。
 * 50000格的patch半徑約112格。設10的話要走到只剩10格（約16公里）才補載，
 * 在60度傾斜視角下那個邊界已經近到很明顯了；設30代表大約每走82步補一次，
 * 而前方邊界永遠保持在30格（約50公里）以外。
 */
export const REFETCH_DEPTH_THRESHOLD = 30

/** 長按方向鍵時每步之間的最小間隔（毫秒）。約11格/秒。 */
export const MOVE_COOLDOWN_MS = 90

/**
 * 轉向的鏡頭動畫時長（毫秒）。刻意設成比 MOVE_COOLDOWN_MS 長好幾倍：
 * 單按一下要看得出鏡頭確實轉了一圈，而不是瞬間跳過去；長按連續轉向時，
 * 新的目標角度會接著前一次動畫還沒轉完的角度繼續延伸（見 cameraRig.ts 的 turn 動畫狀態），
 * 效果是連續平滑地轉，不會卡頓成一格一格的跳動。
 */
export const FACING_TURN_DURATION_MS = 220

/**
 * controls.minDistance 是邊長的幾倍。必須讓 minDistance 大於 camera.near(0.1)，
 * 否則拉到最近時近平面會跑到目標點後面，把腳下的格子整個裁掉。
 * edgeLengthScene ≈ 0.027，所以 4 倍 ≈ 0.108 > 0.1。
 */
export const MIN_DISTANCE_EDGE_MULTIPLE = 4

/**
 * pixelRatio上限2：高DPI螢幕（3x）不設限的話fragment工作量是2倍上限的2.25倍，
 * 但這種每格純色的畫面在2x以上肉眼已經看不出差別，純粹是白燒GPU。
 */
export const MAX_PIXEL_RATIO = 2
