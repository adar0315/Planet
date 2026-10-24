/**
 * PlanetScene 用到的固定常數，集中放這裡跟其他邏輯模組（cameraRig、patchManager…）共用，
 * 避免同一個數字（例如 RENDER_RADIUS）散落在好幾個檔案裡各自寫一份、之後各自漂移。
 */

/** 場景中星球的顯示半徑（場景單位，與公尺無關），Tile大小依此比例換算。 */
export const RENDER_RADIUS = 50

export const REALTIME_TICK_MS = 3000

/** 預設視角：從正上方往「玩家背後」偏斜的俯角，比純垂直俯視更有立體感、看得到地形起伏。 */
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

/**
 * 連續移動的速度（公尺／秒，星球實際比例）。
 *
 * 細網格一格邊長約 1 公里、格心間距約 1.7 公里。真實步行 1.4 m/s 走過一格要二十分鐘，
 * 在兩千萬格的星球上等於不能玩，所以這是「遊戲速度」：走一格約 8 秒、跑一格約 3 秒。
 * 這等於把角色的尺度定在遠大於人的程度，跟鏡頭預設 20 公里的距離與 8 倍高度誇張是一致的。
 * 2026-10-16 依使用者選的建議值設定，實際手感要玩過再調。
 */
export const WALK_SPEED_MPS = 200
export const RUN_SPEED_MPS = 600
/** 後退與橫移比前進慢：動作遊戲的慣例，也讓「轉身往前走」比「倒著走」划算。 */
export const BACK_SPEED_FACTOR = 0.6
export const STRAFE_SPEED_FACTOR = 0.75

/**
 * 加速度與減速度（公尺／秒²）。起步約 0.25 秒到全速、停下約 0.17 秒——
 * 有加減速才不會像以前跳格那樣生硬，但又短到不會覺得「滑」。
 */
export const ACCELERATION_MPS2 = 800
export const DECELERATION_MPS2 = 1200

/** 按住轉向鍵時的角速度（度／秒）。一圈 3 秒。 */
export const TURN_RATE_DEG_PER_SEC = 120

/**
 * 每幀最長的時間步（秒）。分頁切到背景再回來時 requestAnimationFrame 會給出好幾秒的 dt，
 * 不夾住的話玩家會一幀之內飛過好幾格——而跨格偵測只看目前格與相鄰格，跨兩格以上會漏掉。
 * 0.1 秒以跑步速度是 60 公尺，遠小於一格。
 */
export const MAX_FRAME_DT_SEC = 0.1

/**
 * 角色模型的長度是鏡頭距離的幾倍。60° 視角下約佔畫面高度的 7%（600 像素高的畫面約 40 像素），
 * 拉遠拉近都維持這個大小。見 playerAvatar.ts 為什麼不用固定的公尺數。
 */
export const AVATAR_SIZE_PER_CAMERA_DISTANCE = 0.08

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

/**
 * 上帝視角（docs/flight-and-god-view-plan.md）。高度就是鏡頭距離，見 godView.ts。
 *
 * 最高 5,000 公里：星球半徑約 1,852 公里，從那裡看整顆星球的視角半徑約 16°，60° 的視野裝得下。
 * 鏡頭的 far 是 10,000 場景單位（約 37 萬公里），遠大於這個距離。
 *
 * 最低距離沿用走路時的縮放下限（約 4 公里），不是規劃寫的 500 公尺：鏡頭的 near 是 0.1 場景單位
 * （約 3.7 公里），比 near 還近的話，注視點底下的地面會被整個裁掉（見 MIN_DISTANCE_EDGE_MULTIPLE）。
 */
export const GOD_VIEW_MAX_DISTANCE_METERS = 5_000_000

/** 鏡頭距離高於這個值就不再跟著補載地塊，只看星球貼圖：高空看不出單一格子，高速時補載也跟不上。 */
export const GOD_VIEW_PATCH_CEILING_METERS = 300_000

/** 水平速度＝每秒移動目前的鏡頭距離，但不低於這個值（公尺／秒）。 */
export const GOD_VIEW_MIN_SPEED_MPS = 500

/**
 * 近看細分（水文地貌規劃 H7）：鏡頭距離低於這個值（公尺）就把附近的地塊換成約 250 公尺的小六角。
 * 預設鏡頭 20 公里時看不出小格，拉近到幾公里才看得到河道、沙灘的形狀。
 */
export const SUBTILE_CAMERA_DISTANCE_METERS = 8_000

/** 細分多大的範圍（公尺，半徑）。約 300 個地塊、五千個小格。 */
export const SUBTILE_RADIUS_METERS = 12_000

/** 注視點離上一次細分的中心超過這麼遠（公尺）才重新要一批。 */
export const SUBTILE_REFRESH_METERS = 4_000

/** 小格的邊長（公尺）：地塊的四分之一（後端 SubTileGrid.Factor = 4）。 */
export const SUBTILE_EDGE_METERS = 250
