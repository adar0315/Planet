namespace Planet.Simulation;

/// <summary>
/// 把網格、狀態、板塊與各種地質作用組裝起來，提供「推進 N 步」的介面。
///
/// 目前只有內營力（階段2）。之後的水循環、外營力、岩性都會掛在 <see cref="Step"/> 裡，
/// 順序有物理意義：先有地形（內營力）才談得上水往哪裡流，先有水才談得上侵蝕與沉積。
/// </summary>
public sealed class SimulationEngine
{
    /// <summary>
    /// 每步代表幾年的預設值。
    ///
    /// 地質作用的時間尺度決定了不能逐年模擬（造一條山脈要數千萬年，逐年跑要5,000萬步）。
    /// 5萬年是「一步夠大、跑得完整部星球史」與「一步夠小、板塊運動不會跳過細節」的折衷：
    /// 45億年 ÷ 5萬年 = 9萬步，以階段0實測的速度全速跑約20分鐘。
    /// </summary>
    public const double DefaultYearsPerStep = 50_000;

    /// <summary>網格（建好就不變）。</summary>
    public SimulationGrid Grid { get; }

    /// <summary>目前的模擬狀態。</summary>
    public SimulationState State { get; private set; }

    /// <summary>目前的板塊配置。</summary>
    public PlateSet Plates { get; private set; }

    /// <summary>每步代表幾年。</summary>
    public double YearsPerStep { get; set; } = DefaultYearsPerStep;

    /// <summary>內營力模型，診斷用（要看各來源的地殼收支）。</summary>
    public PlateTectonics Tectonics => _tectonics;

    /// <summary>裂縫火山，診斷用（地殼收支要算它）。</summary>
    public FissureVolcanism Fissures => _fissures;

    /// <summary>侵蝕模型，診斷用（要看它實際移除了多少地殼）。</summary>
    public Erosion ErosionDiagnostics => _erosion;

    /// <summary>目前的地函熱柱（板塊內部的火山作用）。</summary>
    public MantlePlumes Plumes => _plumes;

    private readonly int _seed;
    private readonly float _oceanDepthMeters;
    private readonly int _densePlateCount;
    private PlateTectonics _tectonics;
    private MantlePlumes _plumes;

    /// <summary>氣候模型。風場只跟緯度有關，所以建一次就重複使用，Reset 也不用重建。</summary>
    private readonly Climate _climate;

    /// <summary>造礁作用。沒有自己的狀態（全部寫在 <c>OrganicThickness</c> 裡），Reset 也不用重建。</summary>
    private readonly CoralReefs _reefs = new();

    /// <summary>重板塊斷層帶的裂縫火山。沒有自己的狀態，Reset 也不用重建。</summary>
    private readonly FissureVolcanism _fissures = new();

    /// <summary>外營力（流水／冰川／海浪／風化）。只有暫存緩衝，Reset 也不用重建。</summary>
    private readonly Erosion _erosion;

    /// <summary>冰凍圈。沒有自己的狀態（全部寫在 <c>IceThickness</c> 裡），Reset 也不用重建。</summary>
    private readonly Cryosphere _ice = new();

    /// <summary>被陸地封死的海會被鑿開出口。沒有自己的狀態，Reset 不用重建。</summary>
    private readonly SeaOutletBreach _breach = new();

    /// <summary>氣候多久重解一次（年）。見 <see cref="StepOnce"/> 裡的說明。</summary>
    private const double ClimateSolveIntervalYears = 2_000_000;

    private double _yearsSinceClimateSolve = double.MaxValue; // 第一步就先解一次

    /// <summary>多久檢查一次有沒有海被圍死（年）。見 <see cref="StepOnce"/> 裡的說明。</summary>
    private const double BreachCheckIntervalYears = 200_000;

    private double _yearsSinceBreachCheck = double.MaxValue; // 第一步就先檢查一次

    /// <summary>診斷用：海洋出口的鑿通狀況（上一次檢查鑿開了幾個、哪些屏障太高）。</summary>
    public SeaOutletBreach Breach => _breach;

    /// <summary>全球水量（立方公尺）。起始狀態決定之後就固定不變，海平面由它反解出來。</summary>
    private double _totalWaterVolume;

    /// <summary>每步算好一次的地表高度，給海平面的二分搜尋重複掃描用。</summary>
    private readonly float[] _surfaceElevation;

    /// <summary>
    /// 水體積平行加總要切成幾段。
    ///
    /// **這個數字一定要是常數，不可以用 <c>Environment.ProcessorCount</c>**（2026-09-10 修）。
    /// 浮點加法沒有結合律，切幾段就決定了加總順序，所以段數一改，總水量就在最低位元上不同、
    /// 解出來的海平面跟著差一點點——而模擬是混沌的，這點差異會被放大。
    ///
    /// 實測：同一個行程裡跑三次（含一次跑完與分批跑）結果完全相同，
    /// 但線上服務跑到同樣的第1,250步是 35.05%、測試行程是 34.949%，**差227格**。
    /// 兩邊種子都是42、程式碼相同，差的就是這個段數。
    /// 症狀特別容易誤判成資料競爭，因為「同機器重跑一致、換個行程就不一致」正是競爭的典型描述；
    /// 差別在於這個是**決定性的**——同樣的段數永遠給同樣的答案。
    ///
    /// 取 64：夠多段讓任何核心數的機器都能平行滿載，又跟機器規格無關。
    /// </summary>
    private const int WaterVolumePartitionCount = 64;

    /// <summary>水體積平行加總的分段結果。固定長度＋固定加總順序，結果才與執行緒排程無關。</summary>
    private readonly double[] _volumePartials;

    /// <summary>建立模擬引擎，狀態為起始的全海洋星球。</summary>
    /// <param name="grid">網格。</param>
    /// <param name="seed">亂數種子，決定板塊配置。同種子會得到完全一樣的演化。</param>
    /// <param name="oceanDepthMeters">
    /// 起始的全球海洋深度（公尺），也就是這顆星球的總水量。水量守恆，所以這個值直接決定海面佔比。
    /// </param>
    /// <param name="densePlateCount">有幾個板塊一開始就是高密度的海洋板塊，見 <see cref="PlateSet.DensePlateCount"/>。</param>
    public SimulationEngine(
        SimulationGrid grid,
        int seed = 42,
        float oceanDepthMeters = SimulationState.DefaultOceanDepthMeters,
        int densePlateCount = PlateSet.DensePlateCount)
    {
        _oceanDepthMeters = oceanDepthMeters;
        _densePlateCount = densePlateCount;
        Grid = grid;
        _seed = seed;
        _surfaceElevation = new float[grid.CellCount];
        _volumePartials = new double[WaterVolumePartitionCount];
        _climate = new Climate(grid);
        _erosion = new Erosion(grid);
        Plates = PlateSet.CreateRandom(grid, seed, densePlateCount: _densePlateCount);
        State = SimulationState.CreateInitialOceanWorld(grid, _oceanDepthMeters, seed);
        _tectonics = new PlateTectonics(grid, Plates.Plates.Length);
        _plumes = new MantlePlumes(seed);
        SyncPlateIdIntoState();
        // 一定要排在 _tectonics.Reset 之前：標記的密度是從 State.CrustDensity 拷過去的，
        // 順序反了的話重板塊的密度異常永遠不會進到標記裡。
        // 也要排在 SyncPlateIdIntoState 之後：TryMakeContinental 要讀 State.PlateId。
        Plates.ApplyDensityAnomaliesTo(State);
        _tectonics.Reset(grid, State, Plates);
        InitializeWater();
    }

    /// <summary>推進指定步數。</summary>
    /// <param name="steps">要跑幾步。</param>
    public void Step(int steps = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(steps);
        for (var i = 0; i < steps; i++) StepOnce();
    }

    /// <summary>
    /// 推進一步。**順序有物理意義**：
    /// 各種地質作用先改地殼（厚度、類型、年齡），最後才由均衡統一換算成高度、再注水。
    /// 火山排在均衡之前，堆出來的地殼才會在同一步就浮起來變成島。
    /// </summary>
    private void StepOnce()
    {
        DecayVolcanicActivity();

        _tectonics.Step(Grid, State, Plates, YearsPerStep);
        _plumes.Step(Grid, State, YearsPerStep / 1_000_000.0);
        // 裂縫火山要讀這一步剛分類好的板塊邊界（斷層帶在哪），所以排在板塊運動之後
        _fissures.Step(Grid, State, Plates, YearsPerStep / 1_000_000.0);

        // 造礁與侵蝕排在均衡注水**之前**，用的是上一步的水深／水溫／逕流。
        //
        // **不能排在注水之後**：注水用的是那一刻解出來的海平面，之後再改地表高度的話，
        // 那一步結束時水深跟地表就對不上了，總水量不再守恆——不可能既固定海平面又守恆。
        // 排在前面，下面那一次求解就會把造礁堆高、侵蝕削低、沉積墊高全部一起算進去。
        // 用上一步的水深判斷海陸有一步的延遲，但一步是5萬年、地形變化極小，完全可以接受。
        _reefs.Step(State, YearsPerStep);
        _erosion.Step(State, YearsPerStep);

        // 冰蓋用上一步的水深（判斷擱淺在陸地上）與氣溫。它鎖住的水會在下面的海平面
        // 求解裡從總水量扣掉——冰長海平面就降、冰融海平面就升。
        _ice.Step(State, _totalWaterVolume, Grid.AverageCellAreaSquareMeters);

        // 板塊運動會把海圍起來（島弧接上大陸、兩塊陸地夾攏），而那種屏障在地質時間尺度上
        // 不穩定——溢流下切、斷層陷落、兩端的波浪侵蝕都會把它切穿。所以被封死的海要鑿出口。
        //
        // **排在這裡**：用上一步的水深判斷海陸（跟造礁／侵蝕同一個慣例），
        // 改的是地殼厚度，而且趕在 WriteGridBackToMarkers 之前，改動才會寫回標記。
        // 排到均衡之後的話，下一次 Rasterize 會把它從標記蓋回去、等於沒鑿。
        //
        // **頻率不能拉太低**：鑿開的通道會被板塊運動填回去。板塊約5公分／年，一步5萬年
        // 就是2.5公里，而 n=150 格距8.6公里——標記3~4步就穿過一格。
        // 一開始設每100萬年（20步）檢查一次，實測完全無效：機制每次都回報「鑿開了39~52個」，
        // 主洋卻一度只剩水域的56.6%，因為通道在下一次檢查之前早就被填死了。
        // 現在每20萬年（4步）一次，搭配約三格寬的通道（見 SeaOutletBreach.Carve）。
        // 這也才是對的物理：真實的海峽靠洋流持續刷深維持暢通，不是切開一次就永遠留著。
        _yearsSinceBreachCheck += YearsPerStep;
        if (_yearsSinceBreachCheck >= BreachCheckIntervalYears)
        {
            _breach.Step(Grid, State);
            _yearsSinceBreachCheck = 0;
        }

        // **所有會改地殼的作用都跑完了才回寫標記。**
        // 這一步不能省、也不能提前：地殼的性質掛在標記上，而 Rasterize 會用標記覆蓋格子。
        // 原本回寫寫在 PlateTectonics.Step 結尾，排在它後面的火山與侵蝕就會被下一次
        // Rasterize 靜靜蓋掉——n=150 大約每2步取樣一次，等於一半的產出憑空消失（2026-09-06）。
        _tectonics.WriteGridBackToMarkers(State);

        ApplyIsostasyAndFillWater();

        // 氣候排在地形之後：水往哪裡流、哪裡下雨完全取決於**當下的**海陸分布與高度。
        // 這一段是解穩態不是往前推進——氣候的時間尺度是幾天，一步是5萬年（見 Climate 的說明）。
        //
        // 但**不需要每一步都重解**：氣候完全由地形決定，而地形在5萬年內幾乎沒有變化。
        // 每200萬年重解一次，對氣候帶的位置沒有可見影響（板塊200萬年只移動約60公里、
        // 在正式解析度下是4格），成本卻少了40倍——解一次氣候比其他所有作用加起來還貴好幾倍。
        _yearsSinceClimateSolve += YearsPerStep;
        if (_yearsSinceClimateSolve >= ClimateSolveIntervalYears)
        {
            _climate.Solve(State);
            _yearsSinceClimateSolve = 0;
        }

        // 板塊範圍每步都在動（PlateSet.Advance），顯示用的副本要跟著，
        // 不然畫出來的板塊邊界會落後到上一次取樣的位置
        SyncPlateIdIntoState();

        State.ElapsedYears += YearsPerStep;
        State.StepCount++;
    }

    /// <summary>
    /// 火山活動是**衰減**而不是每步清零的。
    ///
    /// 各種火山作用發生的頻率差很多（中洋脊只在板塊真的搬動的那幾步生成新地殼），
    /// 每步清零的話畫面上的火山會一閃一閃；改成指數衰減就等於一個「近期活躍度」的移動平均，
    /// 看起來是一座持續冒煙的火山而不是雜訊。
    /// </summary>
    private void DecayVolcanicActivity()
    {
        const float decayPerStep = 0.9f;
        for (var cell = 0; cell < State.CellCount; cell++)
        {
            State.VolcanicActivity[cell] *= decayPerStep;
        }
    }

    /// <summary>
    /// 所有地質作用共同的收尾：地殼老化 → 均衡換算地表高度 → 依**守恆的水量**定出海平面 → 注水。
    ///
    /// 放在引擎層而不是 <see cref="PlateTectonics"/> 裡面，是因為它跟板塊構造無關——
    /// 階段3的水循環、階段4的侵蝕改完地殼之後，需要的也是同一組收尾。
    /// </summary>
    private void ApplyIsostasyAndFillWater()
    {
        var deltaMyr = (float)(YearsPerStep / 1_000_000.0);
        Parallel.For(0, State.CellCount, cell =>
        {
            State.CrustAge[cell] += deltaMyr;
            // 基岩高度 = 地殼自己的浮力 − 疊在上面的東西把它壓下去的量。
            // 後面那一項是「沉積盆地會越積越深」與「冰後回彈」的來源，見 Isostasy.LoadDepressionMeters。
            State.BedrockElevation[cell] = Isostasy.BedrockElevation(
                    State.Crust[cell], State.CrustThickness[cell], State.CrustAge[cell], State.CrustDensity[cell])
                - Isostasy.LoadDepressionMeters(
                    State.SedimentThickness[cell], State.IceThickness[cell]);
        });

        // 地表高度先算一次存起來：二分搜尋要掃描全球十幾次，每次都重算「基岩＋沉積＋有機＋植被」
        // 四層相加的話，光這件事就會吃掉三分之二的運算時間（實測 25 步/秒 → 7.9 步/秒）。
        Parallel.For(0, State.CellCount, cell => _surfaceElevation[cell] = State.SurfaceElevationAt(cell));

        State.SeaLevelMeters = SolveSeaLevel();

        Parallel.For(0, State.CellCount, cell =>
        {
            State.WaterDepth[cell] = MathF.Max(0f, State.SeaLevelMeters - _surfaceElevation[cell]);
        });
    }

    /// <summary>
    /// 由**守恆的水量**反解出海平面高度。
    ///
    /// ## 為什麼一定要守恆而不是用固定海平面
    ///
    /// 固定海平面會讓「陸地比例」變成一個沒有物理意義的量：大陸長高長大時海水不會被排開，
    /// 於是陸地比例完全由地殼厚度的絕對值決定，而那個絕對值又取決於一堆還沒校準好的參數。
    /// 2026-09-06 就是因為這樣一路對著假的基準線調參——看起來「穩定在8~12%」的平衡，
    /// 實際上是地殼頂到 <see cref="Isostasy.MaxCrustThicknessKm"/> 被硬切掉造成的質量漏洞，
    /// 把增生調小、不再頂到上限之後，大陸立刻無限成長到覆蓋80%的星球。
    ///
    /// 水量守恆讓這件事自我調節：大陸長大 → 排開海水 → 海平面上升 → 淹回去。
    /// 陸地比例於是由**地形的分布形狀**（hypsometry）決定，而不是由絕對高度決定——
    /// 這才是地球29%那個數字真正的來源，也才能拿來比較。
    ///
    /// 用二分搜尋：海平面對水量是單調的，格數再多也只要幾十次掃描。
    /// </summary>
    private float SolveSeaLevel()
    {
        // 海平面一步之內變化很小，所以從上一步的值往外找一個夠寬的括號就好，
        // 不必每次都從「全球最低點到最高點」開始二分——那要多花十幾次全域掃描。
        var low = State.SeaLevelMeters - SeaLevelBracketMeters;
        var high = State.SeaLevelMeters + SeaLevelBracketMeters;

        // 括號不夠寬就往外倍增（地形劇烈變動時才會發生）
        // **要用液態水量，不是總水量**：冰蓋鎖住的水已經不在海洋裡了。
        // 這就是「冰期海平面下降」的來源——地球上冰期時海平面比現在低約120公尺。
        var liquid = _totalWaterVolume - Cryosphere.LockedWaterVolume(State, Grid.AverageCellAreaSquareMeters);

        // **液態水量不可以是負的。** 冰鎖住的水比整顆星球擁有的還多時就會發生，
        // 而 WaterVolumeAt 最小只能是 0，`0 > 負數` 恆成立，下面往下擴張的迴圈
        // 就永遠跳不出去。2026-09-09 實測 liquid = −4.96e15 立方公尺，
        // 括號一路掉到 −5.9e20 公尺，測試空轉了 9 小時、沒有任何訊息。
        // 供給上限現在擋在 Cryosphere.Step 裡（冰只能從現有的水來），這裡是第二道防線。
        if (liquid < 0.0)
        {
            throw new InvalidOperationException(
                $"液態水量是負的（{liquid} 立方公尺）——冰蓋鎖住的水超過總水量 "
                + $"{_totalWaterVolume}，Cryosphere 的供給上限沒有生效。");
        }

        // **倍增一定要有上限。** 地表高度只要出現 NaN，內層的 `depth > 0f` 就恆為 false、
        // WaterVolumeAt 永遠回傳 0，`0 < liquid` 於是恆成立，括號怎麼往外加都跳不出去——
        // 而每一圈都掃過全部格子。**無窮迴圈比例外難查太多**，所以寧可炸掉。
        // 正常情況用不到 5 圈；60 圈之後 float 早就溢位成無窮大了。
        const int maxExpansions = 60;

        var expansions = 0;
        while (WaterVolumeAt(low) > liquid)
        {
            low -= high - low;
            if (++expansions > maxExpansions)
            {
                throw new InvalidOperationException(
                    $"海平面括號往下擴張 {maxExpansions} 次仍未包住液態水量"
                    + $"（low={low}、liquid={liquid}）——地形高度或水量很可能已經是 NaN。");
            }
        }

        expansions = 0;
        while (WaterVolumeAt(high) < liquid)
        {
            high += high - low;
            if (++expansions > maxExpansions)
            {
                throw new InvalidOperationException(
                    $"海平面括號往上擴張 {maxExpansions} 次仍未包住液態水量"
                    + $"（high={high}、liquid={liquid}）——地形高度或水量很可能已經是 NaN。");
            }
        }

        for (var iteration = 0; iteration < SeaLevelIterations; iteration++)
        {
            var mid = (low + high) * 0.5f;
            if (WaterVolumeAt(mid) > liquid) high = mid;
            else low = mid;
        }

        return (low + high) * 0.5f;
    }

    /// <summary>海平面在指定高度時，全球的水體積（立方公尺）。</summary>
    private double WaterVolumeAt(float seaLevel)
    {
        Parallel.For(
            0,
            _volumePartials.Length,
            slice =>
            {
                var from = (int)((long)State.CellCount * slice / _volumePartials.Length);
                var to = (int)((long)State.CellCount * (slice + 1) / _volumePartials.Length);
                var sum = 0.0;
                for (var cell = from; cell < to; cell++)
                {
                    var depth = seaLevel - _surfaceElevation[cell];
                    if (depth > 0f) sum += depth;
                }
                _volumePartials[slice] = sum;
            });

        // 固定的切片數＋固定的加總順序：結果與執行緒排程無關，同種子才會跑出同一顆星球
        var total = 0.0;
        foreach (var partial in _volumePartials) total += partial;
        return total * Grid.AverageCellAreaSquareMeters;
    }

    /// <summary>
    /// 二分搜尋的次數。括號寬度 <see cref="SeaLevelBracketMeters"/> 的兩倍經過18次二分之後
    /// 誤差小於千分之一公尺，遠比地形本身的不確定性小。
    /// </summary>
    /// <summary>
    /// 海平面二分搜尋的迭代次數。
    ///
    /// 從 18 提高到 60（2026-09-06）：冰蓋加進來之後，冰佔總水量的 23~35%，
    /// 海平面每步的擺動比先前大得多，原本的次數留下約 1.3e-6 的相對殘差、
    /// 超過水量守恆測試的 1e-6 容差。這不是漏水（誤差有界、不累積），是收斂不夠。
    /// </summary>
    private const int SeaLevelIterations = 60;

    /// <summary>從上一步的海平面往兩側各展開多少公尺當作二分搜尋的起始括號。</summary>
    private const float SeaLevelBracketMeters = 128f;

    /// <summary>整個重來：回到起始的全海洋星球，板塊也重新生成。</summary>
    public void Reset()
    {
        Plates = PlateSet.CreateRandom(Grid, _seed, densePlateCount: _densePlateCount);
        State = SimulationState.CreateInitialOceanWorld(Grid, _oceanDepthMeters, _seed);
        _tectonics = new PlateTectonics(Grid, Plates.Plates.Length);
        _plumes = new MantlePlumes(_seed);
        SyncPlateIdIntoState();
        // 一定要排在 _tectonics.Reset 之前：標記的密度是從 State.CrustDensity 拷過去的，
        // 順序反了的話重板塊的密度異常永遠不會進到標記裡。
        // 也要排在 SyncPlateIdIntoState 之後：TryMakeContinental 要讀 State.PlateId。
        Plates.ApplyDensityAnomaliesTo(State);
        _tectonics.Reset(Grid, State, Plates);
        _yearsSinceClimateSolve = double.MaxValue;
        _yearsSinceBreachCheck = double.MaxValue;
        InitializeWater();
    }

    /// <summary>
    /// 由「平均海深」定出這顆星球的總水量，再反解出對應的海平面。
    ///
    /// **水量要由平均海深直接算，不能從起始水深加總回來**（2026-09-06 踩過）：
    /// 起始的基岩高度是由均衡公式算出來的，如果水深也只是「填滿高度0以下」，
    /// 那水量就完全由地形決定、傳進來的海深參數是**死的**——
    /// 症狀是把總水量從3,000公尺一路調到5,000公尺，海面佔比一個百分點都沒變。
    /// </summary>
    private void InitializeWater()
    {
        _totalWaterVolume = (double)_oceanDepthMeters
            * Grid.AverageCellAreaSquareMeters * Grid.CellCount;

        Parallel.For(0, State.CellCount, cell => _surfaceElevation[cell] = State.SurfaceElevationAt(cell));
        State.SeaLevelMeters = SolveSeaLevel();
        Parallel.For(0, State.CellCount, cell =>
        {
            State.WaterDepth[cell] = MathF.Max(0f, State.SeaLevelMeters - _surfaceElevation[cell]);
        });
    }

    /// <summary>
    /// 陸地佔全球表面的比例（0~1）。
    /// 這是驗證模擬有沒有跑出像地球的星球最直接的指標——地球是 0.29。
    /// </summary>
    public double LandFraction()
    {
        var land = 0;
        for (var cell = 0; cell < State.CellCount; cell++)
        {
            if (State.WaterDepth[cell] <= 0f) land++;
        }
        return (double)land / State.CellCount;
    }

    /// <summary>板塊歸屬同時存在 PlateSet（模擬用）與 State（顯示用），初始化時要對齊。</summary>
    private void SyncPlateIdIntoState() =>
        Array.Copy(Plates.PlateIdByCell, State.PlateId, Grid.CellCount);

    /// <summary>目前的板塊邊界統計，給診斷與測試用。</summary>
    /// <returns>聚合、張裂、轉形三種邊界各有幾格。</returns>
    public (int Convergent, int Divergent, int Transform) BoundaryCounts()
    {
        var convergent = 0;
        var divergent = 0;
        var transform = 0;
        foreach (var boundary in State.Boundary)
        {
            switch (boundary)
            {
                case BoundaryType.Convergent: convergent++; break;
                case BoundaryType.Divergent: divergent++; break;
                case BoundaryType.Transform: transform++; break;
            }
        }
        return (convergent, divergent, transform);
    }
}
