namespace Planet.Simulation;

/// <summary>
/// 氣候與水循環：溫度、風、蒸發、水氣輸送、降水。
///
/// ## ⚠️ 這一段是「解穩態」不是「往前推進」
///
/// 這是整個模型裡時間尺度最極端的地方。大氣把水氣從海面帶到內陸只要**幾天**，
/// 而模擬的一步是**5萬年**——以地質的時間尺度來看，氣候是瞬間達到平衡的。
/// 所以這裡不是把水氣「往前推一步」，而是每一步都重新**解出當下地形對應的穩態氣候**
/// （<see cref="_transportIterations"/> 次迭代直到水氣分布收斂）。
///
/// 誤把它當成時間積分的話，水氣會以每步一格的速度慢慢爬，跑了幾百萬年還沒到內陸，
/// 內陸永遠是沙漠——而且完全不會有錯誤訊息。
///
/// ## 三胞環流：為什麼風的分布長這樣
///
/// 赤道受熱上升、極區冷卻下沉，中間由三組環流胞（Hadley／Ferrel／Polar）接力，
/// 加上自轉的科氏力偏轉，於是地表風呈現**緯度帶狀**：
/// 信風（東風）在15度、西風在45度、極地東風在75度，而0/30/60度是風速接近零的輻合輻散帶。
/// 這組分布直接產生地球上最明顯的幾個氣候特徵，都可以拿來驗證模型對不對：
///
/// - **赤道多雨帶**（輻合上升 → 熱帶雨林）
/// - **南北緯30度的沙漠帶**（下沉增溫 → 撒哈拉、阿拉伯、澳洲內陸）
/// - **雨影**（迎風坡多雨、背風坡乾燥）
///
/// 這裡的風只算**地表風**、而且不隨地形改變。真正的大氣動力學要解流體方程，
/// 對這個模擬來說是殺雞用牛刀——帶狀風加上地形抬升就足以產生上面那些特徵。
/// </summary>
public sealed class Climate
{
    /// <summary>
    /// 水氣最遠要能被帶多遠（公里）。
    ///
    /// **參數是物理距離，不是迭代次數**，這裡踩過一次坑（2026-09-06）：
    /// 每次迭代水氣大約前進一格，所以固定的迭代次數在不同解析度下代表**完全不同的距離**——
    /// 寫死60次在 n=24（格距93公里）是5,580公里、在 n=150（格距14.9公里）只剩894公里。
    /// 而這顆星球的大陸寬達數千公里，結果是正式解析度下**一半的陸地降水掛零**
    /// （實測陸地降水中位數0.00），粗網格的測試卻全部通過。
    ///
    /// 取2,000公里：與地球上水氣能深入內陸的尺度相當（再遠就是真正的內陸乾燥區了）。
    /// </summary>
    private const double TransportDistanceKm = 2000.0;

    /// <summary>迭代次數的上下限，避免極端解析度下算太久或距離不足。</summary>
    private const int MinTransportIterations = 20;

    /// <summary>迭代次數上限，見 <see cref="MinTransportIterations"/>。</summary>
    private const int MaxTransportIterations = 200;

    /// <summary>由 <see cref="TransportDistanceKm"/> 除以格子中心間距換算出來的實際迭代次數。</summary>
    private readonly int _transportIterations;

    /// <summary>海平面處、緯度0的基準溫度（攝氏）。</summary>
    private const float EquatorTemperatureC = 30f;

    /// <summary>赤道到極區的溫度落差（攝氏）。地球約是30°C到-25°C，落差55度。</summary>
    private const float PoleTemperatureDropC = 55f;

    /// <summary>
    /// 氣溫遞減率（攝氏／公里）。對流層的標準值是6.5，這是山頂會下雪、高原比較冷的原因，
    /// 也是階段4冰川作用發不發生的判準來源。
    /// </summary>
    private const float LapseRateCPerKm = 6.5f;

    /// <summary>
    /// 大陸度：陸地的溫度變化比海洋劇烈多少。
    /// 海水的熱容量大得多，所以海洋調節氣溫、大陸極端（西伯利亞比同緯度的海洋冷得多）。
    /// 取0.15＝陸地把「相對全球平均的偏差」放大15%。
    /// </summary>
    private const float ContinentalityFactor = 0.15f;

    /// <summary>
    /// 溫度每變化這麼多度，大氣的持水能力就變成 e 倍（攝氏）。
    ///
    /// 這是 Clausius–Clapeyron 關係的簡化：飽和水氣壓隨溫度指數上升。
    /// 取12：溫度每升高12度持水量約變2.7倍，與實際的「每升高1度約多7%」大致相符
    /// （1.07^12 ≈ 2.25，同一個量級）。**這是熱帶潮濕、極區乾燥的根本原因**，
    /// 極區降水少不是因為沒有水，是因為冷空氣裝不了多少水氣。
    /// </summary>
    private const float MoistureCapacityEFoldingC = 12f;

    /// <summary>15°C 的空氣能裝多少水氣（公釐可降水量）。地球的全球平均約25公釐。</summary>
    private const float MoistureCapacityAt15C = 25f;

    /// <summary>每次迭代，水面能把多少比例的「未飽和空缺」補滿。1.0＝海面上空永遠飽和。</summary>
    private const float EvaporationEfficiency = 0.25f;

    /// <summary>
    /// 地形抬升每公里，強制降下多少比例的水氣。
    /// 取0.6：一道2公里高的山脈可以攔下大部分水氣，背風側因此明顯乾燥——這就是雨影。
    /// </summary>
    private const float OrographicFractionPerKm = 0.6f;

    /// <summary>每次迭代，飽和以外還會自然降下多少比例的水氣（對流雨、鋒面雨）。</summary>
    private const float BaselineRainFraction = 0.06f;

    /// <summary>每次迭代水氣往下風處移動的比例。1.0＝整團移到下風格。</summary>
    private const float AdvectionFraction = 0.9f;

    /// <summary>降水轉成逕流的比例；其餘蒸散回大氣。地球陸地的平均逕流係數約0.35。</summary>
    internal const float RunoffFraction = 0.35f;

    private readonly SimulationGrid _grid;

    /// <summary>
    /// 每格往各個鄰居送出多少比例的水氣（長度 = 格數 × 最大鄰居數，總和為1或0）。
    ///
    /// **不能只挑「最對齊的那一個」鄰居**，這裡踩過一次坑（2026-09-06）：
    /// 那等於把風向量化到鄰居的方向，而帶狀風的緯向分量遠大於經向分量，
    /// 於是經向分量每次都輸、整個被丟掉——水氣沿著緯線繞圈永遠出不去。
    /// 症狀是橫跨整個緯度圈的大陸內部**完全沒有降水**（實測1,540格陸地裡0格有逕流），
    /// 而且海面上的降水看起來完全正常，只看全球統計抓不到。
    /// </summary>
    private readonly float[] _outflowWeight;

    /// <summary>
    /// 垂直運動：正值＝上升（成雲致雨），負值＝下沉（增溫變乾）。
    ///
    /// **這是副熱帶沙漠帶的成因，不能省。** 只有水平風的話，南北緯30度只是溫暖的海面、
    /// 反而多雨（實測赤道4.09、副熱帶5.12，完全顛倒）。撒哈拉、阿拉伯、澳洲內陸這一整圈
    /// 沙漠帶真正的成因是 Hadley 環流的下沉支——空氣在30度附近下沉、絕熱增溫、相對濕度下降。
    /// </summary>
    private readonly float[] _verticalMotion;

    /// <summary>
    /// 每格從各個鄰居**收到**多少比例的水氣，也就是 <see cref="_outflowWeight"/> 的反向索引。
    /// 有了它，輸送可以寫成每格只寫自己的收集迴圈、完全平行化。
    /// </summary>
    private readonly float[] _inflowWeight;

    /// <summary>每格留在原地不被吹走的水氣比例。</summary>
    private readonly float[] _retainedFraction;

    private readonly float[] _nextMoisture;
    private readonly float[] _capacity;

    /// <summary>地形抬升會逼出多少比例的水氣。跟地形一起固定，每次 Solve 算一次就好。</summary>
    private readonly float[] _orographicFraction;

    /// <summary>陸地上這一次迭代降下、下一次迭代要蒸散回大氣的水量（公釐）。</summary>
    private readonly float[] _recycled;

    /// <summary>建立氣候模型，並預先算好帶狀風的下風方向。</summary>
    /// <param name="grid">網格。</param>
    public Climate(SimulationGrid grid)
    {
        _grid = grid;
        _outflowWeight = new float[grid.CellCount * SimulationGrid.MaxNeighborsPerCell];
        _inflowWeight = new float[grid.CellCount * SimulationGrid.MaxNeighborsPerCell];
        _retainedFraction = new float[grid.CellCount];
        _verticalMotion = new float[grid.CellCount];
        _nextMoisture = new float[grid.CellCount];
        _capacity = new float[grid.CellCount];
        _orographicFraction = new float[grid.CellCount];
        _recycled = new float[grid.CellCount];

        // 格子中心間距：由平均面積當正六邊形反推邊長，再乘√3
        var spacingKm = Math.Sqrt(grid.AverageCellAreaSquareMeters / (3.0 * Math.Sqrt(3.0) / 2.0))
            * Math.Sqrt(3.0) / 1000.0;
        _transportIterations = Math.Clamp(
            (int)Math.Round(TransportDistanceKm / spacingKm),
            MinTransportIterations,
            MaxTransportIterations);

        BuildWindField();
        BuildInflowWeights();
    }

    /// <summary>
    /// 由出流權重反算出「每格從哪些鄰居收到多少水氣」，讓輸送可以平行化（見 <see cref="Advect"/>）。
    /// </summary>
    private void BuildInflowWeights()
    {
        Parallel.For(0, _grid.CellCount, cell =>
        {
            var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
            var sentAway = 0f;

            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                sentAway += _outflowWeight[baseIndex + s];

                var neighbor = _grid.Neighbors[baseIndex + s];
                if (neighbor == SimulationGrid.NoNeighbor) continue;

                // 找出這一格在鄰居的鄰居表裡排第幾個，才能取到「鄰居往這裡送」的權重
                var neighborBase = neighbor * SimulationGrid.MaxNeighborsPerCell;
                for (var t = 0; t < SimulationGrid.MaxNeighborsPerCell; t++)
                {
                    if (_grid.Neighbors[neighborBase + t] != cell) continue;
                    _inflowWeight[baseIndex + s] = _outflowWeight[neighborBase + t] * AdvectionFraction;
                    break;
                }
            }

            _retainedFraction[cell] = 1f - sentAway * AdvectionFraction;
        });
    }

    /// <summary>
    /// 依目前的地形解出穩態的氣候：溫度、降水、陸地含水量。
    /// </summary>
    /// <param name="state">模擬狀態，溫度／大氣水氣／降水／含水量會被更新。</param>
    public void Solve(SimulationState state)
    {
        UpdateTemperature(state);

        Array.Clear(state.AtmosphericMoisture);
        Array.Clear(state.Precipitation);
        Array.Clear(_recycled);

        // 持水能力與地形抬升的降水比例**在迭代之間都不會變**（地形與溫度在這次求解裡是固定的），
        // 所以先算好。原本每次迭代都重算 DownwindElevation，那是七個鄰居各自把四層厚度相加，
        // 每步因此多做約3.8億次陣列讀取——n=150 實測從72步/秒掉到2步/秒，幾乎全部花在這裡。
        Parallel.For(0, state.CellCount, cell =>
        {
            _capacity[cell] = MoistureCapacityAt15C
                * MathF.Exp((state.Temperature[cell] - 15f) / MoistureCapacityEFoldingC);

            var climbKm = (DownwindElevation(state, cell) - AirFloorAt(state, cell)) / 1000f;
            _orographicFraction[cell] = climbKm > 0f
                ? MathF.Min(climbKm * OrographicFractionPerKm, 1f)
                : 0f;
        });

        for (var iteration = 0; iteration < _transportIterations; iteration++)
        {
            EvaporateAndRain(state);
            Advect(state);
        }

        // 降水量是累積 _transportIterations 次的結果，換算成「每年」只是一個相對的尺度，
        // 對階段4的侵蝕來說重要的是**相對強弱**（哪裡多雨、哪裡是雨影），不是絕對值。
        var perIteration = 1f / _transportIterations;
        Parallel.For(0, state.CellCount, cell =>
        {
            state.Precipitation[cell] *= perIteration;
            state.Moisture[cell] = state.WaterDepth[cell] > 0f
                ? 0f // 水面下沒有「陸地逕流」可言
                : state.Precipitation[cell] * RunoffFraction;
        });
    }

    /// <summary>
    /// 只更新溫度、不解降水（v2 P6：板塊座標系模式每一步呼叫）。溫度只看緯度、相對海平面的高度與海陸，很便宜；
    /// 原本跟著降水每 200 萬年才更新一次，冰蓋在解氣候的那一步一口氣追上 200 萬年累積的溫度變化，海平面跟著跳一截
    /// （種子 42 劇本第 42 步 −26 公尺，平常每步 0.8 公尺）。
    /// </summary>
    /// <param name="state">模擬狀態，溫度會被更新。</param>
    public void UpdateTemperatureOnly(SimulationState state) => UpdateTemperature(state);

    /// <summary>
    /// 降水裡有多少是雪（0~1），依年均溫：<see cref="AllSnowBelowC"/> 以下全是雪、<see cref="AllRainAboveC"/> 以上全是雨，中間線性。
    /// 依連通性注水的融雪逕流（<see cref="Hydrology"/>）與冰蓋的降雪供給（<see cref="Cryosphere"/>）共用。
    /// </summary>
    /// <param name="temperatureC">年均溫（攝氏）。</param>
    internal static float SnowFraction(float temperatureC) =>
        Math.Clamp((AllRainAboveC - temperatureC) / (AllRainAboveC - AllSnowBelowC), 0f, 1f);

    /// <summary>年均溫低於這個（攝氏）降水全是雪。</summary>
    internal const float AllSnowBelowC = -5f;

    /// <summary>年均溫高於這個（攝氏）降水全是雨。</summary>
    internal const float AllRainAboveC = 5f;

    /// <summary>
    /// 海洋熱輸送（v2 P6，2026-10-19；只在板塊座標系模式開，標記法照舊用 <see cref="EquatorTemperatureC"/> 的緯度公式）。
    ///
    /// 原本溫度只看緯度（30 − 55 sin²緯度），沒有任何熱量從赤道搬到高緯度——45° 是 2.5°C、地球同緯度約 10°C。
    /// 地球上海流與大氣各搬大約一半的熱，這是中高緯度能住人的原因。
    ///
    /// 做法是一維的能量平衡模型（<see cref="SolveZonalTemperature"/>）：全球分成 2° 一條的緯度帶，
    /// 每條帶往輻射平衡溫度（<see cref="RadiativeEquatorC"/> − <see cref="RadiativePoleDropC"/>·sin²緯度）鬆弛，
    /// 同時跟相鄰的帶擴散熱量，擴散係數跟那條帶的海洋比例成正比（海流在搬熱）。
    /// 海面的溫度就是那條帶的溫度；陸地拿到「大氣那一半」的輸送，加上離海岸越遠越少的「海洋那一半」
    /// （<see cref="MaritimeReachKm"/>）——沿海溫和、內陸極端。
    /// </summary>
    public bool OceanHeatTransport { get; set; }

    /// <summary>沒有熱輸送時赤道的輻射平衡溫度（攝氏）。跟擴散一起校準：七成是海時赤道約 26°C、45° 約 8°C、60° 約 −2°C、極區約 −10°C。</summary>
    private const float RadiativeEquatorC = 34f;

    /// <summary>輻射平衡溫度從赤道到極區降多少（攝氏），乘上 sin²緯度。</summary>
    private const float RadiativePoleDropC = 60f;

    /// <summary>熱量擴散的強度（以弧度² 計、相對於往輻射平衡鬆弛的強度），再乘上海洋比例。校準見 <see cref="RadiativeEquatorC"/>。</summary>
    private const double ZonalDiffusion = 0.15;

    /// <summary>陸地拿得到的熱輸送裡，大氣搬的那一份（到處都有），其餘是海洋搬的（只到得了沿海）。</summary>
    private const float AtmosphericTransportShare = 0.5f;

    /// <summary>海洋那一份熱輸送往內陸遞減的距離尺度（公里，指數遞減）。</summary>
    private const double MaritimeReachKm = 800;

    /// <summary>緯度帶的數目（每條 2°）。</summary>
    private const int ZonalBands = 90;

    /// <summary>能量平衡模型每次更新的 Gauss–Seidel 迭代次數（從上一次的解出發）。</summary>
    private const int ZonalIterations = 3000;

    private readonly double[] _zonalTemperature = new double[ZonalBands];
    private bool _zonalInitialized;
    private int[] _coastHops = [];

    private static double RadiativeAt(double latitudeDegrees)
    {
        var sinLat = Math.Sin(latitudeDegrees * Math.PI / 180.0);
        return RadiativeEquatorC - RadiativePoleDropC * sinLat * sinLat;
    }

    /// <summary>
    /// 各緯度帶的穩態溫度：0 ＝ λ·f/cosφ · d/dφ(cosφ dT/dφ) ＋（輻射平衡 − T），f 是相鄰兩帶海洋比例的平均。
    /// Gauss–Seidel 解，從上一次的解出發。
    /// </summary>
    private void SolveZonalTemperature(SimulationState state)
    {
        var ocean = new double[ZonalBands];
        var count = new double[ZonalBands];
        for (var c = 0; c < state.CellCount; c++)
        {
            var band = Math.Clamp((int)((_grid.Latitude[c] + 90.0) / 180.0 * ZonalBands), 0, ZonalBands - 1);
            count[band]++;
            if (state.WaterDepth[c] > 0f) ocean[band]++;
        }
        for (var i = 0; i < ZonalBands; i++) ocean[i] = count[i] > 0 ? ocean[i] / count[i] : 0;

        var dphi = Math.PI / ZonalBands;
        var phi = new double[ZonalBands];
        var radiative = new double[ZonalBands];
        for (var i = 0; i < ZonalBands; i++)
        {
            phi[i] = -Math.PI / 2 + (i + 0.5) * dphi;
            radiative[i] = RadiativeAt(phi[i] * 180.0 / Math.PI);
        }
        if (!_zonalInitialized)
        {
            Array.Copy(radiative, _zonalTemperature, ZonalBands);
            _zonalInitialized = true;
        }
        for (var iteration = 0; iteration < ZonalIterations; iteration++)
        {
            for (var i = 0; i < ZonalBands; i++)
            {
                var north = i < ZonalBands - 1 ? ZonalDiffusion * 0.5 * (ocean[i] + ocean[i + 1]) * Math.Cos(phi[i] + dphi / 2) : 0;
                var south = i > 0 ? ZonalDiffusion * 0.5 * (ocean[i] + ocean[i - 1]) * Math.Cos(phi[i] - dphi / 2) : 0;
                var scale = 1.0 / (Math.Cos(phi[i]) * dphi * dphi);
                var neighbors = north * (i < ZonalBands - 1 ? _zonalTemperature[i + 1] : 0) + south * (i > 0 ? _zonalTemperature[i - 1] : 0);
                _zonalTemperature[i] = (radiative[i] + scale * neighbors) / (1 + scale * (north + south));
            }
        }
    }

    /// <summary>某個緯度的帶溫度（相鄰兩帶中心之間線性內插）。</summary>
    private double ZonalAt(double latitudeDegrees)
    {
        var x = (latitudeDegrees + 90.0) / 180.0 * ZonalBands - 0.5;
        var i = Math.Clamp((int)Math.Floor(x), 0, ZonalBands - 2);
        var t = Math.Clamp(x - i, 0, 1);
        return _zonalTemperature[i] * (1 - t) + _zonalTemperature[i + 1] * t;
    }

    /// <summary>每一格離海岸幾格（水面是 0）。</summary>
    private void ComputeCoastHops(SimulationState state)
    {
        var n = state.CellCount;
        if (_coastHops.Length != n) _coastHops = new int[n];
        Array.Fill(_coastHops, int.MaxValue);
        var queue = new Queue<int>();
        for (var c = 0; c < n; c++)
            if (state.WaterDepth[c] > 0f) { _coastHops[c] = 0; queue.Enqueue(c); }
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
            for (var k = 0; k < SimulationGrid.MaxNeighborsPerCell; k++)
            {
                var nb = _grid.Neighbors[baseIndex + k];
                if (nb == SimulationGrid.NoNeighbor || _coastHops[nb] != int.MaxValue) continue;
                _coastHops[nb] = _coastHops[c] + 1;
                queue.Enqueue(nb);
            }
        }
    }

    /// <summary>
    /// 溫度＝緯度基準（有海洋熱輸送時見 <see cref="OceanHeatTransport"/>）−（高度×遞減率）＋大陸度修正。
    /// </summary>
    private void UpdateTemperature(SimulationState state)
    {
        if (OceanHeatTransport)
        {
            SolveZonalTemperature(state);
            ComputeCoastHops(state);
        }
        var spacingKm = _grid.AverageCellSpacingMeters / 1000.0;
        Parallel.For(0, state.CellCount, cell =>
        {
            var sinLat = Math.Sin(_grid.Latitude[cell] * Math.PI / 180.0);
            var baseC = EquatorTemperatureC - PoleTemperatureDropC * (float)(sinLat * sinLat);
            if (OceanHeatTransport)
            {
                var radiative = RadiativeAt(_grid.Latitude[cell]);
                var zonal = ZonalAt(_grid.Latitude[cell]);
                var share = state.WaterDepth[cell] > 0f
                    ? 1.0
                    : AtmosphericTransportShare + (1 - AtmosphericTransportShare) * Math.Exp(-_coastHops[cell] * spacingKm / MaritimeReachKm);
                baseC = (float)(radiative + (zonal - radiative) * share);
            }
            baseC += GlobalTemperatureOffsetC;

            // 高度一定要**相對海平面**算，不能用絕對高度（2026-09-06 踩過）：
            // 這個模型的海平面是由守恆的水量反解出來的，會一路漲到 +3,000 公尺以上，
            // 用絕對高度的話，一塊海拔0公尺的海岸平原會被當成3,000公尺的高山、多降19度，
            // 整顆星球被系統性凍住——實測冰蓋覆蓋了48%的表面（地球冰期最盛也只有約25%），
            // 而且冰把水鎖走→海平面下降→露出更多絕對高度很高的陸地→更冷，是正回饋。
            //
            // 只有露出水面的地方才有高度遞減：水面下是海水溫度，不適用氣溫遞減率。
            var elevationKm = MathF.Max(0f, state.SurfaceElevationAt(cell) - state.SeaLevelMeters) / 1000f;
            var temperature = baseC - elevationKm * LapseRateCPerKm;

            // 大陸度：陸地把「相對全球平均」的偏差放大（海洋熱容量大，會把溫度拉回平均）
            if (state.WaterDepth[cell] <= 0f)
            {
                var globalMean = (OceanHeatTransport
                    ? RadiativeEquatorC - RadiativePoleDropC / 3f
                    : EquatorTemperatureC - PoleTemperatureDropC / 3f) + GlobalTemperatureOffsetC;
                temperature += (temperature - globalMean) * ContinentalityFactor;
            }

            state.Temperature[cell] = temperature;
        });
    }

    /// <summary>
    /// 一次迭代裡的蒸發與降水。
    ///
    /// 降水有兩個來源，兩個都是真實的機制：
    /// **地形抬升**（空氣被迫爬坡、絕熱冷卻、水氣凝結——迎風坡多雨、背風坡是雨影）
    /// 與**飽和**（暖濕空氣移到較冷的地方裝不下，多的降下來）。
    /// </summary>
    private void EvaporateAndRain(SimulationState state)
    {
        Parallel.For(0, state.CellCount, cell =>
        {
            // 1. 水面蒸發：把空氣往飽和補
            if (state.WaterDepth[cell] > 0f)
            {
                var deficit = _capacity[cell] - state.AtmosphericMoisture[cell];
                if (deficit > 0f) state.AtmosphericMoisture[cell] += deficit * EvaporationEfficiency;
            }
            else
            {
                // 陸地的蒸散回流：降在陸地上的水只有 RunoffFraction 那部分往下游流，
                // **其餘會蒸散回大氣、被風帶往更內陸再下一次雨**。
                //
                // 這一段不能省（2026-09-06 踩過）：原本只把逕流那部分記下來、其餘直接憑空消失，
                // 於是水氣一進入陸地就被消耗殆盡，**一半的陸地降水掛零**（實測陸地降水中位數0.00），
                // 階段4的流水侵蝕會完全沒有輸入。真實世界的內陸之所以還有雨，
                // 正是靠這個回收機制（亞馬遜的降水有相當比例來自森林自己的蒸散）。
                state.AtmosphericMoisture[cell] += _recycled[cell];
                _recycled[cell] = 0f;
            }

            var moisture = state.AtmosphericMoisture[cell];
            if (moisture <= 0f) return;

            // 2. 地形抬升：空氣被迫爬坡、絕熱冷卻、水氣凝結（比例在 Solve 裡先算好了）
            var rain = moisture * _orographicFraction[cell];

            // 3. 基礎降水（對流雨、鋒面雨），受三胞環流的垂直運動調節：
            //    上升區（赤道、極鋒）加成，下沉區（副熱帶、極區）抑制——後者就是沙漠帶
            var convective = BaselineRainPerIteration * (1f + _verticalMotion[cell]);
            if (convective > 0f) rain += (moisture - rain) * convective;

            // 4. 超過持水能力的部分一定降下來
            var remaining = moisture - rain;
            var excess = remaining - _capacity[cell];
            if (excess > 0f) rain += excess;

            state.AtmosphericMoisture[cell] = moisture - rain;
            state.Precipitation[cell] += rain;

            // 陸地上的雨有一部分會蒸散回大氣，下一次迭代再放回去（見上面第1步的說明）
            if (state.WaterDepth[cell] <= 0f) _recycled[cell] = rain * (1f - RunoffFraction);
        });
    }

    /// <summary>下風方向的地表高度（依出流權重加權），用來判斷空氣要不要爬坡。</summary>
    private float DownwindElevation(SimulationState state, int cell)
    {
        var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
        var elevation = 0f;
        var totalWeight = 0f;
        for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
        {
            var weight = _outflowWeight[baseIndex + s];
            if (weight <= 0f) continue;
            elevation += AirFloorAt(state, _grid.Neighbors[baseIndex + s]) * weight;
            totalWeight += weight;
        }
        return totalWeight > 0f ? elevation / totalWeight : AirFloorAt(state, cell);
    }

    /// <summary>
    /// 空氣底下的高度：地表，有水的話是水面（<see cref="OrographyAboveWater"/>）。
    /// </summary>
    private float AirFloorAt(SimulationState state, int cell) => OrographyAboveWater
        ? state.SurfaceElevationAt(cell) + state.WaterDepth[cell]
        : state.SurfaceElevationAt(cell);

    /// <summary>
    /// 地形抬升從水面算起（v2 P7，2026-10-19；只在板塊座標系模式開，標記法照舊用水底的地表）。
    ///
    /// 空氣在水面上，海面就是海平面。原本用水底的地表：海上的空氣「從 −4 公里的海床爬上大陸坡」，
    /// 靠岸的海格就把 56% 的水氣逼成雨（種子 42 劇本第 1 步），上岸的空氣幾乎是乾的——
    /// 陸地降水只有全球平均的 0~1%。之前量到的「陸地 11%」是海平面下降露出來的大陸棚、還留著當海時的降水。
    /// </summary>
    public bool OrographyAboveWater { get; set; }

    /// <summary>
    /// 全球溫度偏移（攝氏，v2 P8；標記法是 0）：整顆星球一起暖或冷，像溫室氣體多一點或少一點。
    /// 大陸的位置固定（兩塊大陸都延伸到北緯 70~75 度），冰蓋要壓到陸地的 10% 以下只能靠整體暖一點，見規劃文件 P8 結果。
    /// </summary>
    public float GlobalTemperatureOffsetC { get; set; }

    /// <summary>
    /// 不是 0 時（v2 P7），基礎降水的 <see cref="BaselineRainFraction"/> 是「每走這麼多公里」降下的比例，
    /// 換算成每次迭代（一格）的比例；0 ＝ 標記法的舊行為，每次迭代固定 <see cref="BaselineRainFraction"/>。
    /// </summary>
    internal double BaselineRainReferenceKm { get; set; }

    /// <summary>每次迭代的基礎降水比例，見 <see cref="BaselineRainReferenceKm"/>。</summary>
    private float BaselineRainPerIteration => BaselineRainReferenceKm > 0
        ? (float)(1 - Math.Pow(1 - BaselineRainFraction, _grid.AverageCellSpacingMeters / 1000.0 / BaselineRainReferenceKm))
        : BaselineRainFraction;

    /// <summary>
    /// 把水氣沿著風送給下風的鄰居們（上風法）。
    ///
    /// 寫成**收集**（每格把鄰居送進來的加總起來）而不是**散播**（每格把自己的送出去）：
    /// 散播會有多格寫同一個鄰居的競爭、只能單執行緒跑，而這段每一步要跑60次迭代、
    /// 是整個氣候模型最熱的迴圈（n=150 時單執行緒版本讓測試從34秒變成4分半）。
    /// 收集版本每格只寫自己，可以完全平行。
    /// </summary>
    private void Advect(SimulationState state)
    {
        Parallel.For(0, state.CellCount, cell =>
        {
            var incoming = state.AtmosphericMoisture[cell] * _retainedFraction[cell];

            var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                var weight = _inflowWeight[baseIndex + s];
                if (weight <= 0f) continue;
                incoming += state.AtmosphericMoisture[_grid.Neighbors[baseIndex + s]] * weight;
            }

            _nextMoisture[cell] = incoming;
        });

        Array.Copy(_nextMoisture, state.AtmosphericMoisture, state.CellCount);
    }

    /// <summary>
    /// 預先算好每格的下風鄰居。
    ///
    /// 風向由三胞環流的帶狀分布決定（見類別說明）。同一個 cos 項同時決定東西與南北分量，
    /// 所以信風自然是「東北風」而不是純東風——北半球低緯的風確實是從東北吹向西南。
    /// </summary>
    private void BuildWindField()
    {
        Parallel.For(0, _grid.CellCount, cell =>
        {
            var latitude = _grid.Latitude[cell];

            // 相位讓信風在15度、西風在45度、極地東風在75度達到最強，
            // 而0/30/60度（赤道輻合帶、副熱帶高壓、極鋒）風速接近零
            var band = Math.Cos(Math.PI * (Math.Abs(latitude) - 15.0) / 30.0);
            var eastward = -band;                                  // 負值＝東風（由東往西吹）
            var northward = -Math.Sign(latitude) * band * 0.5;     // 低緯往赤道、中緯往極區

            // 垂直運動：0度上升、30度下沉、60度上升、90度下沉（Hadley/Ferrel/Polar 三胞的接縫）
            _verticalMotion[cell] = (float)Math.Cos(Math.PI * Math.Abs(latitude) / 30.0);

            var x = _grid.UnitVector[cell * 3];
            var y = _grid.UnitVector[cell * 3 + 1];
            var z = _grid.UnitVector[cell * 3 + 2];

            // 地表的東方＝自轉軸(0,0,1)與位置向量的外積，正規化後就是切平面上的單位東向量
            var eastX = -y;
            var eastY = x;
            var eastLength = Math.Sqrt(eastX * eastX + eastY * eastY);
            if (eastLength < 1e-12) return; // 正好在極點上，東方沒有定義，權重留 0
            eastX /= eastLength;
            eastY /= eastLength;

            // 北方 = r × east（右手系，已經是單位向量）。
            // 三個分量的正負號都很容易寫反——寫反的話經向的風就整個顛倒，
            // 而畫面上仍然是一組看起來很合理的帶狀分布，非常難察覺。
            var northX = -z * eastY;
            var northY = z * eastX;
            var northZ = x * eastY - y * eastX;

            var windX = eastward * eastX + northward * northX;
            var windY = eastward * eastY + northward * northY;
            var windZ = northward * northZ;
            var windLength = Math.Sqrt(windX * windX + windY * windY + windZ * windZ);
            if (windLength < 1e-9) return; // 輻合輻散帶，地表風接近靜止，權重留 0

            // 出流權重＝各鄰居方向與風向的對齊程度（只取正的），最後正規化成總和1。
            // 分給多個鄰居而不是只挑最對齊的那一個，經向分量才不會被量化掉（見欄位說明）。
            var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
            var totalWeight = 0.0;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                var neighbor = _grid.Neighbors[baseIndex + s];
                if (neighbor == SimulationGrid.NoNeighbor) continue;

                var dx = _grid.UnitVector[neighbor * 3] - x;
                var dy = _grid.UnitVector[neighbor * 3 + 1] - y;
                var dz = _grid.UnitVector[neighbor * 3 + 2] - z;
                var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (length < 1e-12) continue;

                var alignment = (dx * windX + dy * windY + dz * windZ) / (length * windLength);
                if (alignment <= 0) continue;

                _outflowWeight[baseIndex + s] = (float)alignment;
                totalWeight += alignment;
            }

            if (totalWeight <= 0) return;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                _outflowWeight[baseIndex + s] /= (float)totalWeight;
            }
        });
    }
}
