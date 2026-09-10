namespace Planet.Simulation;

/// <summary>
/// 外營力：流水侵蝕、冰川刨蝕、海浪侵蝕、風化，以及把刨下來的物質搬到下游堆積。
///
/// ## ⚠️ 這是唯一能把大陸地殼「移除」的機制
///
/// 在這之前，地殼只進不出——板塊增生與火山一直加，卻沒有任何東西拿走。
/// 實測跑到2億年時**85%的星球變成大陸地殼**、平均地殼厚達45公里、
/// 海平面被守恆的水量推高到+5,161公尺（2026-09-06，用0個重板塊的對照組確認過
/// 跟板塊設定無關）。所有參數校準在這個失控存在時都是對著壞掉的系統在調。
///
/// ## 等均衡是關鍵：磨掉1公尺地表要拿走5.5公尺地殼
///
/// 地殼浮在地函上，拿掉一部分之後剩下的會浮起來補回大半，這叫**均衡反彈**。
/// 由 Airy 均衡 e = h·Δρ/ρ地函，大陸地殼的比例是 (3300−2700)/3300 = 0.182：
/// 移除1公里厚度只讓地表降182公尺，反過來說**地表降1公尺要移除5.5公尺的地殼**。
///
/// 這一點決定了侵蝕能不能平衡造陸：地球的大陸平均剝蝕率約每百萬年50公尺（地表），
/// 換算成地殼是每百萬年275公尺——2億年就是55公里，跟前面量到的45公里增厚同一個量級。
/// 少了這個換算會低估5.5倍，侵蝕看起來「有在跑」卻完全擋不住地殼增厚。
///
/// ## 流水侵蝕用 stream power law
///
/// E = K · A^m · S^n，A是集水面積、S是坡度。這是地形演化模型的標準式子，
/// 物理意義是「水多、坡陡的地方刨得快」——所以大河的下游峽谷與陡峻的山區侵蝕最猛，
/// 而平坦的內陸高原幾乎不動。集水面積要靠**由高到低依序累加**才算得出來
/// （見 <see cref="Step"/> 裡的排序），不能只看鄰居。
/// </summary>
public sealed class Erosion
{
    /// <summary>
    /// 流水侵蝕係數。E = K·Q^m·S^n 裡的 K。
    ///
    /// **校準目標是地球的大陸平均剝蝕率：每百萬年約50公尺地表**（相當於每年0.05公釐）。
    /// K 吸收了岩性、植被、降雨強度等一堆這裡沒有模擬的因素，地形演化文獻裡它本來就是校準參數。
    ///
    /// 第一版給 3e-5 而且 Q 用的是「上游格數 × 逕流」，實際算出來只有每百萬年 2×10⁻⁶ 公尺——
    /// **比需要的少了七個數量級**，跑200百萬年完全擋不住地殼增厚（2026-09-06）。
    /// 現在 Q 改成真正的流量（立方公尺／年）、坡度改成固定參考長度上的落差，兩者都與解析度無關。
    /// </summary>
    private const double StreamPowerCoefficient = 0.06;

    /// <summary>流量的指數（stream power law 的 m）。文獻常用 0.4~0.6。</summary>
    private const double DischargeExponent = 0.5;

    /// <summary>
    /// 量坡度用的參考長度（公尺）。
    ///
    /// **坡度一定要在固定的物理長度上量，不能直接用「跟鄰居的落差 ÷ 格距」**：
    /// 同一片地形用愈細的網格取樣，量到的局部坡度就愈陡（n=150的格距是n=24的1/6，
    /// 同樣的落差算出來的坡度就大6倍），侵蝕強度會直接跟著解析度變。
    /// 取15,000公尺，也就是正式解析度 n=150 的格距——在那個解析度下這個正規化剛好是1。
    /// </summary>
    private const double SlopeReferenceLengthMeters = 15_000.0;

    /// <summary>
    /// 風化：跟坡度和水量都無關的背景剝蝕（公尺地表／百萬年）。
    /// 就算是完全平坦的地方，岩石照樣會因為溫差、水、生物作用而崩解。
    /// 取5：遠小於河流的侵蝕，但保證高原內部不會完全靜止。
    /// </summary>
    private const double BaseWeatheringMetersPerMyr = 5.0;

    /// <summary>
    /// 冰川刨蝕相對於流水的倍率。冰川是效率極高的侵蝕營力（挪威峽灣、五大湖都是它刨出來的），
    /// 一般估計比同等條件的河流強2~5倍。
    /// </summary>
    private const double GlacialErosionMultiplier = 3.0;

    /// <summary>結冰的溫度門檻（攝氏）。低於此視為有冰川覆蓋。</summary>
    private const float GlacialTemperatureC = -5f;

    /// <summary>
    /// 海浪侵蝕（公尺地表／百萬年／**每一條面海的邊**）。
    ///
    /// 這是海崖後退、海蝕平台的來源，跟高度無關，所以會把海岸削平。
    /// 大陸海岸多半只有一條邊面海，所以維持原本的 30；四面環水的孤立小島有5~6條邊，
    /// 被削的速度是好幾倍——那才是對的物理（見 <c>SurfaceLoweringMeters</c> 的說明）。
    /// </summary>
    private const double WaveErosionMetersPerMyr = 30.0;

    /// <summary>
    /// 一步最多能把地表降低多少公尺。純粹是數值穩定的護欄：
    /// 沒有上限的話，陡坡加大集水面積會在一步之內把一座山整個挖穿、變成一個洞，
    /// 下一步周圍的坡度又變得更陡，於是震盪發散。
    /// </summary>
    private const double MaxSurfaceLoweringPerStepMeters = 50.0;

    /// <summary>
    /// 搬運中的沉積物有多少比例會在**陸地上**沿途堆下來，其餘一路帶到海裡。
    /// 取0.3：沖積扇、氾濫平原確實會留下一部分，但大河終究是把大部分泥沙送進海。
    /// </summary>
    private const float LandDepositionFraction = 0.3f;

    private readonly SimulationGrid _grid;

    /// <summary>每格的下游鄰居（最陡下降方向）。-1 代表是窪地或海面下，水流不出去。</summary>
    private readonly int[] _downstream;

    /// <summary>由高到低排序過的格子索引。集水面積與泥沙搬運都要照這個順序累加。</summary>
    private readonly int[] _order;

    /// <summary>每格的流量（立方公尺／年）＝上游所有格子的逕流體積總和。是物理量，與解析度無關。</summary>
    private readonly double[] _discharge;

    /// <summary>每格搬運中的沉積物（公尺厚度當量）。</summary>
    private readonly double[] _load;

    private readonly float[] _surface;

    /// <summary>排序用的鍵（負高度）。見 <see cref="BuildFlowNetwork"/> 裡不用比較委派的理由。</summary>
    private readonly float[] _sortKey;

    /// <summary>診斷用：最近一步移除了多少地殼體積（立方公里）。用來確認侵蝕的量級對不對。</summary>
    public double LastRemovedCrustVolumeKm3 { get; private set; }

    /// <summary>診斷用：最近一步有幾格真的被侵蝕到。</summary>
    public int LastErodedCellCount { get; private set; }

    /// <summary>建立侵蝕模型。</summary>
    /// <param name="grid">網格。</param>
    public Erosion(SimulationGrid grid)
    {
        _grid = grid;
        _downstream = new int[grid.CellCount];
        _order = new int[grid.CellCount];
        _discharge = new double[grid.CellCount];
        _load = new double[grid.CellCount];
        _surface = new float[grid.CellCount];
        _sortKey = new float[grid.CellCount];
    }

    /// <summary>
    /// 推進一步的外營力。
    ///
    /// 順序有物理意義：先決定水往哪流、算出集水面積，才談得上哪裡侵蝕得快；
    /// 刨下來的物質再照同一條路線往下游搬、沿途與入海時堆積。
    /// </summary>
    /// <param name="state">模擬狀態，地殼厚度與沉積物厚度會被更新。</param>
    /// <param name="deltaYears">這一步代表多少年。</param>
    public void Step(SimulationState state, double deltaYears)
    {
        var deltaMyr = deltaYears / 1_000_000.0;

        for (var cell = 0; cell < state.CellCount; cell++) _surface[cell] = state.SurfaceElevationAt(cell);

        BuildFlowNetwork(state);
        AccumulateDischarge(state);

        var before = 0.0;
        for (var cell = 0; cell < state.CellCount; cell++) before += state.CrustThickness[cell];

        ErodeAndTransport(state, deltaMyr);

        var after = 0.0;
        var eroded = 0;
        for (var cell = 0; cell < state.CellCount; cell++)
        {
            after += state.CrustThickness[cell];
            if (state.WaterDepth[cell] <= 0f) eroded++;
        }
        LastRemovedCrustVolumeKm3 = (before - after) * _grid.AverageCellAreaSquareMeters / 1e6;
        LastErodedCellCount = eroded;
    }

    /// <summary>
    /// 決定每格的水往哪個鄰居流（最陡下降），並把所有格子由高到低排序。
    ///
    /// 排序是為了讓集水面積與泥沙搬運可以**一趟掃完**：由高往低走的時候，
    /// 一格的所有上游必定已經處理過了。這是地形演化模型的標準做法，
    /// 比反覆迭代到收斂快得多。
    /// </summary>
    private void BuildFlowNetwork(SimulationState state)
    {
        Parallel.For(0, state.CellCount, cell =>
        {
            var best = -1;
            var steepest = 0f;
            var here = _surface[cell];

            var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                var neighbor = _grid.Neighbors[baseIndex + s];
                if (neighbor == SimulationGrid.NoNeighbor) continue;

                var drop = here - _surface[neighbor];
                if (drop <= steepest) continue;
                steepest = drop;
                best = neighbor;
            }

            _downstream[cell] = best;
        });

        // **用鍵值陣列排序，不要用比較委派**（2026-09-06 實測）：
        // `Array.Sort(_order, (a, b) => ...)` 每次比較都是一次委派呼叫，22.5萬格要比約400萬次，
        // 而這是每步都跑的。實測 n=150 的推進速度因此從16步/秒掉到2.3步/秒。
        // 兩個陣列的多載走的是特化的排序路徑，沒有委派成本。
        // 排序鍵取負的高度，就等於由高到低（水一定往下流，累加必須從最高處開始）。
        for (var cell = 0; cell < state.CellCount; cell++)
        {
            _order[cell] = cell;
            _sortKey[cell] = -_surface[cell];
        }
        Array.Sort(_sortKey, _order, 0, state.CellCount);
    }

    /// <summary>
    /// 由高到低累加流量：每格把自己的逕流加上上游來的，一起交給下游。
    ///
    /// 逕流是公釐／年，要乘上格子面積換成立方公尺／年才是**物理流量**——
    /// 直接累加公釐數的話，同一條河在不同解析度下會算出差好幾倍的「流量」。
    /// </summary>
    private void AccumulateDischarge(SimulationState state)
    {
        var cellArea = _grid.AverageCellAreaSquareMeters;
        for (var cell = 0; cell < state.CellCount; cell++)
        {
            _discharge[cell] = state.Moisture[cell] / 1000.0 * cellArea;
        }

        foreach (var cell in _order)
        {
            var downstream = _downstream[cell];
            if (downstream >= 0) _discharge[downstream] += _discharge[cell];
        }
    }

    /// <summary>
    /// 依 stream power law 刨蝕，並把刨下來的物質往下游搬、沿途與入海時堆積。
    ///
    /// **一定要照 <see cref="_order"/> 由高到低走**：泥沙只能往下游帶，
    /// 順序錯了會把上游還沒算的量算進來。
    /// </summary>
    private void ErodeAndTransport(SimulationState state, double deltaMyr)
    {
        Array.Clear(_load);

        foreach (var cell in _order)
        {
            var downstream = _downstream[cell];
            var submerged = state.WaterDepth[cell] > 0f;

            if (!submerged)
            {
                var lowering = SurfaceLoweringMeters(state, cell, downstream, deltaMyr);
                if (lowering > 0)
                {
                    _load[cell] += RemoveMaterial(state, cell, lowering);
                }
            }

            if (_load[cell] <= 0) continue;

            // 到海裡（或流不出去的窪地）就全部堆下來，否則只留一部分、其餘帶往下游
            if (submerged || downstream < 0)
            {
                state.SedimentThickness[cell] += (float)_load[cell];
                _load[cell] = 0;
                continue;
            }

            var deposited = _load[cell] * LandDepositionFraction;
            state.SedimentThickness[cell] += (float)deposited;
            _load[downstream] += _load[cell] - deposited;
            _load[cell] = 0;
        }
    }

    /// <summary>這一步這一格的地表要降低多少公尺（四種營力相加，再夾在數值穩定的上限內）。</summary>
    private double SurfaceLoweringMeters(SimulationState state, int cell, int downstream, double deltaMyr)
    {
        // 風化：到處都有的背景剝蝕
        var lowering = BaseWeatheringMetersPerMyr * deltaMyr;

        // 海浪：按**暴露的邊數**計費，不是有沒有靠海的二元判斷。
        //
        // 波浪攻擊的強度正比於面海的邊長。原本寫成 IsCoastal 的二元判斷，於是
        // 四面環水的一格小島跟大陸海岸的一格吃到完全相同的侵蝕量——那不合物理，
        // 而且正是「滿地一格小島」的來源之一：孤立小島應該被削得比海岸快好幾倍。
        //
        // 一個水鄰居的大陸海岸維持原本的速率（多數海岸格就是這樣），
        // 六面環水的小島變成六倍。真實世界的對應是**海底平頂山（guyot）**：
        // 火山島停止噴發之後被浪削平、沉到水下，天皇海山鏈整排都是這樣來的。
        // 還在噴的島（夏威夷、冰島）因為火山持續補充地殼所以留得住。
        lowering += WaveErosionMetersPerMyr * WaterNeighborCount(state, cell) * deltaMyr;

        // 流水／冰川：要有坡度才刨得動
        if (downstream >= 0)
        {
            var drop = _surface[cell] - _surface[downstream];
            if (drop > 0)
            {
                // 坡度在固定的參考長度上量，不用格距——否則侵蝕強度會跟著解析度跑
                var slope = drop / SlopeReferenceLengthMeters;
                var stream = StreamPowerCoefficient
                    * Math.Pow(Math.Max(_discharge[cell], 0), DischargeExponent)
                    * slope;

                // 夠冷就是冰川，刨得比河流兇
                if (state.Temperature[cell] < GlacialTemperatureC) stream *= GlacialErosionMultiplier;

                lowering += stream * deltaMyr;
            }
        }

        return Math.Min(lowering, MaxSurfaceLoweringPerStepMeters);
    }

    /// <summary>
    /// 從這一格拿掉相當於「地表降低 <paramref name="surfaceLoweringMeters"/> 公尺」的物質，
    /// 回傳實際移除的厚度（公尺，之後會變成下游的沉積物）。
    ///
    /// **先刨鬆散的沉積物，刨完才動基岩**——沉積物就疊在上面，本來就先被沖走。
    /// 動到基岩時要換算成地殼厚度：均衡反彈會把大部分補回來，
    /// 所以地表每降1公尺要移除約 1/0.182 ≈ 5.5 公尺的地殼（見類別說明）。
    /// </summary>
    private static double RemoveMaterial(SimulationState state, int cell, double surfaceLoweringMeters)
    {
        var removed = 0.0;
        var remaining = surfaceLoweringMeters;

        var sediment = state.SedimentThickness[cell];
        if (sediment > 0f)
        {
            var takeSediment = Math.Min(sediment, remaining);
            state.SedimentThickness[cell] = (float)(sediment - takeSediment);
            removed += takeSediment;
            remaining -= takeSediment;
        }

        if (remaining <= 0) return removed;

        // 基岩：地表降 remaining 公尺 → 移除 remaining/IsostaticRatio 的地殼厚度
        var crustRemovedMeters = remaining / Isostasy.IsostaticSurfaceRatio(state.Crust[cell]);
        var crustKm = state.CrustThickness[cell];
        var takeKm = Math.Min(crustKm - Isostasy.MinCrustThicknessKm, crustRemovedMeters / 1000.0);
        if (takeKm <= 0) return removed;

        state.CrustThickness[cell] = (float)(crustKm - takeKm);

        // 回傳的是「可以拿去下游堆積的物質厚度」，也就是實際被搬走的岩石體積，
        // 不是地表降低量——那兩個差了均衡反彈的倍率。
        return removed + takeKm * 1000.0;
    }

    /// <summary>
    /// 有幾個鄰居在水面下——也就是這一格有幾條邊在受浪。
    ///
    /// 大陸海岸通常是1~2，海灣與半島的尖端3~4，四面環水的孤立小島是5~6（五角形只有5個鄰居）。
    /// 內陸是0，所以不必另外判斷是不是海岸。
    /// </summary>
    private int WaterNeighborCount(SimulationState state, int cell)
    {
        var count = 0;
        var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
        for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
        {
            var neighbor = _grid.Neighbors[baseIndex + s];
            if (neighbor == SimulationGrid.NoNeighbor) continue;
            if (state.WaterDepth[neighbor] > 0f) count++;
        }
        return count;
    }
}
