namespace Planet.Simulation;

/// <summary>地殼類型。板塊運動要區分這兩種——密度不同，隱沒時是海洋地殼潛入大陸地殼下方。</summary>
public enum CrustType : byte
{
    /// <summary>海洋地殼：薄、密度大、年輕（會不斷被隱沒消耗再從中洋脊新生）。</summary>
    Oceanic = 0,

    /// <summary>大陸地殼：厚、密度小、老（不容易被隱沒，所以能保存數十億年）。</summary>
    Continental = 1,
}

/// <summary>板塊邊界的類型。由兩側板塊的相對運動決定，每步重算。</summary>
public enum BoundaryType : byte
{
    /// <summary>不在板塊邊界上（周圍鄰居都屬於同一個板塊）。</summary>
    None = 0,

    /// <summary>聚合邊界：兩側互相靠近。造山、隱沒、島弧都發生在這裡。</summary>
    Convergent = 1,

    /// <summary>張裂邊界：兩側互相遠離。中洋脊、裂谷，新的海洋地殼在這裡生成。</summary>
    Divergent = 2,

    /// <summary>轉形邊界：兩側互相錯動，垂直方向的效果很小（主要是斷層與地震）。</summary>
    Transform = 3,
}

/// <summary>
/// 模擬過程中**會變動**的所有狀態。不會變的（位置、鄰接關係）在 <see cref="SimulationGrid"/>。
///
/// 刻意用 SoA（每個欄位一個陣列）而不是 AoS（每格一個物件）：模擬的核心是 stencil 運算
/// （讀自己與6個鄰居的同一個欄位），SoA 讓那些讀取在記憶體中連續，對快取friendly得多。
/// 階段0實測 22.5萬格的完整狀態約 8.6 MB，整份常駐記憶體完全沒問題。
///
/// **每步不寫資料庫**：模擬是即時的，每步落地會被 I/O 淹沒。只有使用者按下「採用這個結果」時
/// 才把最終狀態放大寫回 <c>Tiles</c>（規劃中的階段8）。
/// </summary>
public sealed class SimulationState
{
    /// <summary>格數，與 <see cref="SimulationGrid.CellCount"/> 相同。</summary>
    public int CellCount { get; }

    /// <summary>
    /// 基岩表面高度（公尺，相對海平面；負值代表在海平面以下）。
    /// **不含**上面覆蓋的沉積物/有機物/植被，那些各自有厚度欄位，實際地表高度見 <see cref="SurfaceElevationAt"/>。
    /// </summary>
    public float[] BedrockElevation { get; }

    /// <summary>沉積物厚度（公尺）：風化、沖刷帶來的砂石堆積在這格上面。</summary>
    public float[] SedimentThickness { get; }

    /// <summary>有機沉積厚度（公尺）：海中生物遺骸累積（之後會變成石灰岩之類的沉積岩）。</summary>
    public float[] OrganicThickness { get; }

    /// <summary>植被厚度（公尺）：陸地上的植被層。</summary>
    public float[] VegetationThickness { get; }

    /// <summary>
    /// 冰蓋厚度（公尺）。只累積在**露出水面**的格子上——浮在海上的海冰不影響海平面
    /// （阿基米德原理），只有擱淺在陸地上的冰才會把水從海洋裡鎖走，見 <see cref="Cryosphere"/>。
    /// </summary>
    public float[] IceThickness { get; }

    /// <summary>水深（公尺）。0 代表沒有水體；&gt;0 代表這格被水覆蓋（海或湖，靠高度與連通性區分）。</summary>
    public float[] WaterDepth { get; }

    /// <summary>
    /// 陸地含水量（公釐／年的逕流當量）：降水扣掉蒸發之後留在這格、可以往下游流的水量。
    /// 這是階段4流水侵蝕的輸入——沖刷力大致正比於流量。
    /// </summary>
    public float[] Moisture { get; }

    /// <summary>
    /// 大氣中的可降水量（公釐）。由海面蒸發產生，被風帶著走，遇冷或被地形抬升就降下來。
    /// **這是大氣的狀態不是地表的**，跟 <see cref="Moisture"/> 是兩回事。
    /// </summary>
    public float[] AtmosphericMoisture { get; }

    /// <summary>降水量（公釐／年）。雨影、赤道多雨帶、副熱帶沙漠都是從這個欄位看出來的。</summary>
    public float[] Precipitation { get; }

    /// <summary>地表溫度（攝氏）。決定冰河作用發不發生、風化速率、植被能不能長。</summary>
    public float[] Temperature { get; }

    /// <summary>這格屬於哪個板塊（板塊編號）。階段2才會真正用到，在那之前全部是0。</summary>
    public int[] PlateId { get; }

    /// <summary>地殼類型。</summary>
    public CrustType[] Crust { get; }

    /// <summary>地殼年齡（百萬年）。海洋地殼從中洋脊新生後年齡歸零，越靠近隱沒帶越老。</summary>
    public float[] CrustAge { get; }

    /// <summary>
    /// 地殼密度（公斤／立方公尺）。
    ///
    /// 跟厚度一起決定浮力：**同樣厚度下密度低的浮得高、也隱沒不下去**。
    /// 起始值不是均勻的——星球早期被大型隕石撞擊過，撞擊會熔融、分異、挖掘地殼，
    /// 留下大區塊的密度差異（月球的月海／高地、火星的南北二分都是這樣來的），
    /// 那些低密度的大區塊就是後來大陸生長的**核心**，見 <see cref="CreateInitialOceanWorld"/>。
    /// </summary>
    public float[] CrustDensity { get; }

    /// <summary>
    /// 地殼厚度（公里）。這是驅動地形高度的**主要變數**——地殼浮在地函上，
    /// 厚的地方浮得高（大陸、山脈），薄的地方浮得低（海洋盆地），見 <c>Isostasy</c>。
    /// 地球的參考值：大陸地殼約35公里、海洋地殼約7公里、青藏高原下方可達70公里。
    /// </summary>
    public float[] CrustThickness { get; }

    /// <summary>每一格的板塊邊界類型，每步依兩側板塊的相對運動重算。</summary>
    public BoundaryType[] Boundary { get; }

    /// <summary>
    /// 這一步的火山活動強度（公里地殼／百萬年），每步重算。
    ///
    /// 三種來源都會加總進來：中洋脊（張裂）、島弧（隱沒）、地函熱柱（板塊內部）。
    /// **這個欄位純粹是為了讓火山看得見**——地殼增厚本身已經寫進 <see cref="CrustThickness"/> 了，
    /// 但「哪裡正在噴發」如果不另外記下來，渲染時只能從厚度變化反推，那既慢又測不準。
    /// </summary>
    public float[] VolcanicActivity { get; }

    /// <summary>全球海平面高度（公尺）。用單一數值而不是逐格——海面本來就是等位面。</summary>
    public float SeaLevelMeters { get; set; }

    /// <summary>模擬已經跑了多少年。這是**顯示用**的時間，實際推進是一步幾萬年（見 docs 的決策1）。</summary>
    public double ElapsedYears { get; set; }

    /// <summary>已經跑了幾步。</summary>
    public long StepCount { get; set; }

    /// <summary>
    /// 這一格的地殼是否**永遠不能變成大陸地殼**。
    ///
    /// 對應「那兩個高密度板塊維持一直都是海洋」的設定：它們上面可以長出**火山島**
    /// （玄武岩堆高、露出水面，像夏威夷與冰島），但地殼本身不會演變成大陸地殼。
    ///
    /// **這個性質跟著地殼走，不是跟著板塊區域走**，這裡踩過一次坑（2026-09-06）：
    /// 原本記的是「哪幾個板塊是重的」，但板塊範圍是球面 Voronoi 幾何決定的、會**掃過**
    /// 既有的大陸地殼，那些格子就變成重板塊的一部分了——實測跑到1億年時，
    /// 「應該永遠是海洋」的板塊上出現了277格大陸地殼，而閘門完全沒攔到，
    /// 因為它們不是在那裡「轉換」的，是漂進來的。
    ///
    /// 所以旗標掛在會跟著地殼一起移動的標記（marker）上，跟厚度／年齡／密度走同一條路徑：
    /// 重地殼走到哪都還是不能變大陸，別處漂過來的大陸也不會被誤殺。
    /// 中途曾經改用「密度是否超過門檻」來間接判斷，但那不夠穩——島弧增生會讓密度
    /// 慢慢往大陸地殼靠，判準有一天會安靜地失效，所以還是用明確的旗標。
    /// </summary>
    public bool[] OceanicOnly { get; }

    /// <summary>
    /// 把這一格轉成大陸地殼——除非它的地殼被標記為永遠只能是海洋（<see cref="OceanicOnly"/>）。
    ///
    /// **所有「變成大陸地殼」的路徑都必須走這裡。** 目前有四條：島弧增生、重力擴展、
    /// 地函熱柱、早期大轟炸的原始高地。分散在四個檔案裡各自判斷的話，
    /// 之後新增第五條路徑時一定會漏掉，而症狀（重板塊上長出大陸）只有盯著畫面才看得出來。
    /// </summary>
    /// <param name="cellIndex">格子索引。</param>
    public void TryMakeContinental(int cellIndex)
    {
        if (OceanicOnly[cellIndex]) return;
        Crust[cellIndex] = CrustType.Continental;
    }

    /// <summary>建立全部歸零的狀態。實際的起始條件用 <see cref="CreateInitialOceanWorld"/>。</summary>
    /// <param name="cellCount">格數。</param>
    public SimulationState(int cellCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellCount);
        CellCount = cellCount;
        BedrockElevation = new float[cellCount];
        SedimentThickness = new float[cellCount];
        OrganicThickness = new float[cellCount];
        VegetationThickness = new float[cellCount];
        IceThickness = new float[cellCount];
        WaterDepth = new float[cellCount];
        Moisture = new float[cellCount];
        AtmosphericMoisture = new float[cellCount];
        Precipitation = new float[cellCount];
        Temperature = new float[cellCount];
        PlateId = new int[cellCount];
        Crust = new CrustType[cellCount];
        CrustAge = new float[cellCount];
        CrustDensity = new float[cellCount];
        CrustThickness = new float[cellCount];
        Boundary = new BoundaryType[cellCount];
        VolcanicActivity = new float[cellCount];
        OceanicOnly = new bool[cellCount];
    }

    /// <summary>
    /// 實際地表高度（公尺）＝基岩 ＋ 沉積物 ＋ 有機物 ＋ 植被。
    /// 「沉澱物、砂石、植被有厚度會覆蓋在格子上」這個需求就是靠這幾層相加表現的，
    /// 渲染時每一層可以畫成不同顏色/材質。
    /// </summary>
    /// <param name="cellIndex">格子索引。</param>
    public float SurfaceElevationAt(int cellIndex) =>
        BedrockElevation[cellIndex]
        + SedimentThickness[cellIndex]
        + OrganicThickness[cellIndex]
        + VegetationThickness[cellIndex]
        + IceThickness[cellIndex];

    /// <summary>水面高度（公尺）＝地表高度 ＋ 水深。沒有水的格子等於地表高度。</summary>
    /// <param name="cellIndex">格子索引。</param>
    public float WaterSurfaceAt(int cellIndex) => SurfaceElevationAt(cellIndex) + WaterDepth[cellIndex];

    /// <summary>這格有沒有被水覆蓋。</summary>
    /// <param name="cellIndex">格子索引。</param>
    public bool IsSubmerged(int cellIndex) => WaterDepth[cellIndex] > 0f;

    /// <summary>
    /// 建立起始狀態：**裸露的星球，大部分被海水覆蓋，已經有早期大轟炸留下的大尺度海陸雛形**。
    ///
    /// 沉積物／有機物／植被全部為0（裸露），溫度先給一個隨緯度變化的粗略分布，
    /// 讓畫面一開始就看得出南北極比較冷；真正的氣候模型在階段3。
    ///
    /// 地殼**不是**均勻的——見 <see cref="ApplyEarlyBombardment"/>。
    /// （原本的規格是「完全被海水覆蓋」，2026-09-06 使用者取消了這個限制：
    /// 完全均質的起點跑不出大陸，實測跑到6億年陸地仍然只有0.28%。）
    /// </summary>
    /// <summary>
    /// 起始的全球海洋深度（公尺）＝這顆星球的總水量。
    ///
    /// **這個值直接決定海洋佔全球表面的比例**：水量守恆（見 <c>SimulationEngine.SolveSeaLevel</c>），
    /// 所以水多海平面就高、淹掉更多低地。
    ///
    /// 取1,150：在 n=150 上**實測夾逼**出來的，讓陸地在使用者選定的參考窗口
    /// （50~100 Myr，「最接近地球的時刻」）的後半段佔約29%，就是地球的比例。
    /// 陸地佔比會隨時間增加，所以它是「在哪個時刻等於29%」而不是一個定值：
    ///
    /// | 平均海深 | 25 Myr | 50 Myr | 75 Myr | 100 Myr |
    /// |---|---|---|---|---|
    /// | 1,000 | 18.7% | 24.1% | 33.2% | – |
    /// | **1,150** | 16.3% | 21.7% | **30.1%** | **28.3%** |
    /// | 1,350 | 14.3% | 19.2% | 26.2% | 24.1% |
    ///
    /// **這張表已經重量過三次，每次都是因為模型改了**（2026-09-07 一天之內）：
    /// 修好「借鄰居地殼查錯資料結構」、加上「OceanicOnly 的厚度上限」、
    /// 改成「一個板塊多個種子」——最後那一項連亂數序列都變了，等於換了一顆星球。
    /// **模型一改就要重新校準這個值**，它不是獨立參數，是整個地形演化的函數。
    ///
    /// **不可以用「靜態反算」**（2026-09-06 踩過）：一度用當下的地形高度分布直接算出
    /// 「要多少水才有29%陸地」，得到980公尺，實測卻是39~48%——因為改變水量會改變地形的演化。
    /// 水少→陸地多→冰蓋變厚→冰把水從海洋鎖到陸地→海平面再降→露出更多陸地，
    /// 是個正回饋，單點反算完全抓不到。只能實測夾逼。
    ///
    /// **上表的舊版數字（980→43%、1,600→20%…）已經作廢**：那是在
    /// <c>PlateTectonics.BorrowFromNeighbours</c> 把海洋地殼打成3公里、
    /// 以及珊瑚礁無上限累積的狀態下量的（兩者都在 2026-09-07 修掉）。
    /// **模型改了就要重新校準這個值**，它不是獨立參數，是整個地形演化的函數。
    ///
    /// **這個值遠小於地球的3,700公尺，那不是水太少，是這顆星球的地形起伏比地球小**：
    /// 大陸地殼27~30公里、海洋地殼7公里，落差換算成高度約3.6~4.2公里；
    /// 地球是35~40對7，落差5.1~6.0公里。海盆不夠深，就裝不下那麼多水還露出三成陸地。
    /// 要讓水量也回到地球的量級，得先讓地形的起伏變大（缺的機制見 docs 的階段規劃）。
    ///
    /// **這個值一定要在 n=150 上校準**：粗細網格的地形高度分布不同，n=24 量到的套不過來。
    /// </summary>
    public const float DefaultOceanDepthMeters = 1150f;

    /// <param name="grid">要建立狀態的網格。</param>
    /// <param name="oceanDepthMeters">起始的全球海洋深度（公尺），也就是這顆星球的總水量。</param>
    /// <param name="seed">亂數種子，決定early bombardment留下的不均勻分布。</param>
    public static SimulationState CreateInitialOceanWorld(
        SimulationGrid grid, float oceanDepthMeters = DefaultOceanDepthMeters, int seed = 42)
    {
        var state = new SimulationState(grid.CellCount)
        {
            SeaLevelMeters = 0f,
            ElapsedYears = 0,
            StepCount = 0,
        };

        for (var i = 0; i < grid.CellCount; i++)
        {
            state.WaterDepth[i] = oceanDepthMeters;
            state.Crust[i] = CrustType.Oceanic;
            state.CrustThickness[i] = Isostasy.OceanicCrustThicknessKm;
            state.CrustDensity[i] = Isostasy.OceanicCrustDensity;

            // 粗略的緯度溫度分布（30 - 55·sin²緯度）：赤道約30°C、極區約-25°C。
            // 階段3會換成真正的氣候模型（含海陸差異、高度遞減率、洋流），
            // 這裡只是為了一開始畫面就看得出南北極比較冷、不是一片死白。
            var sinLat = Math.Sin(grid.Latitude[i] * Math.PI / 180.0);
            state.Temperature[i] = (float)(30.0 - 55.0 * sinLat * sinLat);
        }

        ApplyEarlyBombardment(grid, state, seed);

        for (var i = 0; i < grid.CellCount; i++)
        {
            state.BedrockElevation[i] = Isostasy.BedrockElevation(
                state.Crust[i], state.CrustThickness[i], state.CrustAge[i], state.CrustDensity[i]);
            state.WaterDepth[i] = MathF.Max(0f, state.SeaLevelMeters - state.BedrockElevation[i]);
        }

        return state;
    }

    /// <summary>
    /// 早期大轟炸：在還沒有板塊運動之前，大型隕石撞擊就已經讓地殼**不均勻**了。
    ///
    /// ## 為什麼一定要有這一段
    ///
    /// 完全均質的海洋世界跑不出大陸（2026-09-05／06 實測：跑到6億年陸地只有0.28%）。
    /// 板塊運動只在邊界上製造聚合與增生，而邊界會隨板塊移動掃過整顆星球，
    /// 增厚被攤薄在各處，沒有任何地方能持續累積到25公里的大陸門檻。
    /// **大陸需要核心**，而真實星球的核心就是早期不均勻留下來的。
    ///
    /// ## 物理依據
    ///
    /// 這不是為了讓模擬好看而加的隨機噪音，是實際觀測到的行星特徵：
    /// 月球的月海（薄、緻密的玄武岩）對比高地（厚達60公里、低密度的斜長岩）、
    /// 火星南北二分（南方高地厚、北方低地薄，一般認為是一次巨大撞擊造成）。
    /// 大型撞擊會挖掘地殼、造成大規模熔融與分異，熔體上浮形成低密度的厚地殼，
    /// 殘餘的緻密物質則留在盆地底部——所以留下的是**成對的**厚/低密度區與薄/高密度區。
    ///
    /// 撞擊的影響範圍刻意取得很大（球面角距20~45度），因為要的是**大區塊**的差異；
    /// 小而密的隨機噪音會被後續的重力擴展抹平，長不出連成片的大陸。
    /// </summary>
    private static void ApplyEarlyBombardment(SimulationGrid grid, SimulationState state, int seed)
    {
        var rng = new Random(seed);

        for (var impact = 0; impact < EarlyImpactCount; impact++)
        {
            // 撞擊點：球面上的均勻隨機方向（z均勻分布法）
            var z = rng.NextDouble() * 2.0 - 1.0;
            var theta = rng.NextDouble() * 2.0 * Math.PI;
            var horizontal = Math.Sqrt(1.0 - z * z);
            var cx = horizontal * Math.Cos(theta);
            var cy = horizontal * Math.Sin(theta);
            var cz = z;

            var radiusRad = (EarlyImpactMinRadiusDegrees
                + rng.NextDouble() * (EarlyImpactMaxRadiusDegrees - EarlyImpactMinRadiusDegrees))
                * Math.PI / 180.0;
            var strength = 0.5 + rng.NextDouble(); // 0.5~1.5 倍的標準強度

            // 一半的撞擊留下低密度的厚地殼高地（＝原始陸塊），一半留下緻密的薄地殼盆地（＝原始洋盆）。
            // 這就是使用者要的「大區塊密度高、大區塊密度低」，也是月球高地／月海、
            // 火星南北二分那種行星尺度的二分結構。
            var sign = impact % 2 == 0 ? 1.0 : -1.0;

            for (var cell = 0; cell < grid.CellCount; cell++)
            {
                var dot = grid.UnitVector[cell * 3] * cx
                    + grid.UnitVector[cell * 3 + 1] * cy
                    + grid.UnitVector[cell * 3 + 2] * cz;
                var distanceRad = Math.Acos(Math.Clamp(dot, -1.0, 1.0));
                if (distanceRad > radiusRad) continue;

                // 鐘形剖面：中心最強、到邊緣平滑歸零。
                //
                // **邊緣一定要歸零**，這裡踩過一次坑（2026-09-05）：原本用 cos(π·r/R)，
                // 它在 r=R 處是 −1、到範圍外又突然變成 0，撞擊邊緣因此有一道6公里的地殼厚度斷崖。
                // 那是相鄰格之間的高頻噪音、不是大區塊，會被重力擴展直接抹平
                // （實測相鄰格落差15公里，佔全球落差的四分之三）。
                var normalized = distanceRad / radiusRad;
                var profile = (1.0 + Math.Cos(Math.PI * normalized)) * 0.5;
                var scaled = (float)(profile * strength * sign);

                state.CrustThickness[cell] += scaled * EarlyImpactThicknessAmplitudeKm;
                state.CrustDensity[cell] -= scaled * EarlyImpactDensityAmplitude;
            }
        }

        // 撞擊會疊加，夾在物理上合理的範圍內；夠厚夠輕的區塊直接就是大陸地殼（原始陸塊）
        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            state.CrustThickness[cell] = Math.Clamp(state.CrustThickness[cell], 3f, Isostasy.MaxCrustThicknessKm);
            state.CrustDensity[cell] = Math.Clamp(state.CrustDensity[cell], 2650f, 3100f);

            if (state.CrustThickness[cell] >= Isostasy.ContinentalThresholdKm)
            {
                state.TryMakeContinental(cell);
            }
        }
    }

    /// <summary>早期大轟炸的撞擊次數。取8：夠讓整顆星球都有大尺度的起伏，又不會互相疊到抹平。</summary>
    private const int EarlyImpactCount = 8;

    /// <summary>撞擊影響範圍的最小球面角距（度）。</summary>
    private const double EarlyImpactMinRadiusDegrees = 20.0;

    /// <summary>撞擊影響範圍的最大球面角距（度）。要的是大區塊的差異，所以刻意取得很大。</summary>
    private const double EarlyImpactMaxRadiusDegrees = 45.0;

    /// <summary>
    /// 一次撞擊在中心造成的地殼厚度變化（公里）。
    ///
    /// 取22：從7公里的底值算起，強度1.5的高地中心可以到40公里、遠超過25公里的大陸門檻，
    /// 一開始就有貨真價實的原始陸塊；盆地那一側則被壓到下限3公里，成為深邃的原始洋盆。
    /// **這個值刻意取得大**——早期大轟炸的規模本來就遠超過後來的板塊運動，
    /// 而且沒有這個起始的大尺度二分，大陸完全長不出來（見 <see cref="ApplyEarlyBombardment"/> 的說明）。
    /// </summary>
    private const float EarlyImpactThicknessAmplitudeKm = 22f;

    /// <summary>
    /// 一次撞擊在中心造成的地殼密度變化（公斤／立方公尺）。
    /// 取200：正好是海洋地殼（2900）與大陸地殼（2700）的密度差，所以高地中心的密度會落在大陸地殼的量級。
    /// </summary>
    private const float EarlyImpactDensityAmplitude = 200f;
}
