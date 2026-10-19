namespace Planet.Simulation;

/// <summary>
/// v2 的外營力（P7，2026-10-19；只在板塊座標系模式用，標記法照舊用 <see cref="Erosion"/>）：
/// 河流侵蝕、冰川刨蝕、海浪、風化，刨下來的物質沿著 <see cref="Hydrology"/> 的流向搬走，
/// 堆進湖裡或沿著大陸棚往外鋪。**沉積總量等於侵蝕總量**（<see cref="LastErodedVolumeKm3"/> ＝ <see cref="LastDepositedVolumeKm3"/>）。
///
/// 跟 v1 的 <see cref="Erosion"/> 不同的地方，每一條都是 P7 基準線量到的問題（種子 42 劇本、40 Myr）：
///
/// 1. **流向用 P5 的泛洪**（灌滿窪地之後往主洋的方向），不是最陡下降。最陡下降碰到窪地就停，
///    41% 的沉積物堆在陸地上的小坑裡；泛洪的流向會越過淺窪地，只有真的存水的盆地（湖）才留得住泥沙。
/// 2. **坡度以水面為基準**：流進海的那一格，落差算到海平面，不是算到幾千公尺深的海床。
/// 3. **沉積物進海之後沿著大陸棚往外鋪**（<see cref="ShelfDepthMeters"/>），不是全部倒在第一格海上——
///    那樣海岸那一格會被墊成陸地、下一步又被刨掉，來回震盪。
/// 4. **褶皺（<see cref="SimulationState.StructuralRelief"/>）也會被刨**：先刨鬆散的沉積物、再刨凸起的褶皺、最後才是基岩。
///    褶皺是疊在地表上的一層，刨 1 公尺就降 1 公尺；基岩要換算均衡反彈（見 <see cref="Erosion"/> 的類別說明）。
/// 5. 流量用 <see cref="Hydrology.Discharge"/>（降水的相對單位 × 格），係數直接對著陸地高度校準
///    （2026-10-19 使用者選定：40 Myr 時陸地平均高出海面約 0.8~1 公里、最高峰 9 公里以內）。
/// </summary>
public sealed class FluvialErosion
{
    /// <summary>河流侵蝕係數：E（公尺／百萬年）＝ K · Q^m · S，Q 是 <see cref="Hydrology.Discharge"/>，S 在 <see cref="SlopeReferenceLengthMeters"/> 上量。</summary>
    ///
    /// 校準（2026-10-20，使用者選定）：種子 42 劇本 40 Myr 時陸地平均高出海面 0.93 公里、最高峰 8.9 公里、陸地佔表面 31.6%、
    /// 剝蝕率約 190 公尺／百萬年（物質厚度）。3,000 時陸地 1.10 公里；10,000 時最高峰 10.4 公里、第二大陸的破碎度 4.3 超過上限。
    internal double StreamPowerCoefficient { get; set; } = 6000.0;

    /// <summary>流量的指數（stream power law 的 m）。</summary>
    private const double DischargeExponent = 0.5;

    /// <summary>量坡度用的參考長度（公尺），跟 <see cref="Erosion"/> 一樣，與解析度無關。</summary>
    private const double SlopeReferenceLengthMeters = 15_000.0;

    /// <summary>風化：到處都有的背景剝蝕（公尺地表／百萬年）。</summary>
    private const double BaseWeatheringMetersPerMyr = 5.0;

    /// <summary>冰川刨蝕相對於流水的倍率。</summary>
    private const double GlacialErosionMultiplier = 3.0;

    /// <summary>結冰的溫度門檻（攝氏），低於這個算冰川。</summary>
    private const float GlacialTemperatureC = -5f;

    /// <summary>浪基面：外面的海比這個淺（公尺）就不算受浪的邊，見 <see cref="SeaNeighborCount"/>。</summary>
    private const float WaveBaseDepthMeters = 200f;

    /// <summary>海浪侵蝕（公尺地表／百萬年／每一條面向深水的邊）。</summary>
    private const double WaveErosionMetersPerMyr = 30.0;

    /// <summary>
    /// 一步最多把地表降低多少公尺：200 公尺／5 萬年 ＝ 4 公釐／年，地球最快的造山帶（台灣、喜馬拉雅東端）的量級。
    /// v1 的 50 公尺在 P7 第一次校準時卡住了碰撞帶最高的 300 格裡的 59~172 格。
    /// </summary>
    private const double MaxSurfaceLoweringPerStepMeters = 200.0;

    /// <summary>河流一步最多刨掉往下游落差的多少：不會把一格刨到比下游還低（挖出坑、下一步坡度反過來，來回震盪）。</summary>
    private const double MaxStreamLoweringFractionOfDrop = 0.5;

    /// <summary>拿掉 1 公尺沉積物地表降多少（均衡反彈之後），見 <see cref="Isostasy.LoadDepressionMeters"/>。</summary>
    private const double SedimentSurfaceRatio = (Isostasy.MantleDensity - Isostasy.SedimentDensity) / (double)Isostasy.MantleDensity;

    /// <summary>湖滿了往下游、流進下一個湖，最多重複幾輪。</summary>
    private const int MaxLakeRounds = 8;

    /// <summary>
    /// 進海的沉積物先把海床墊到海平面下這個深度（公尺），滿了再往更深處流——堆出來的就是大陸棚。
    /// 地球的大陸棚外緣約在海面下 130 公尺。
    /// </summary>
    private const float ShelfDepthMeters = 130f;

    /// <summary>
    /// 進海的沉積物最多往外走多遠（公尺）：濁流可以把泥沙帶到上千公里外的深海平原。
    /// 第一版 600 公里：大陸之間的窄海裡 600 公里內只有幾格，裝不下的平均倒在那幾格上、墊出海面，
    /// 40 Myr 時有 8,993 格陸地是 1 公里以上的沉積物（P7，2026-10-19）。
    /// </summary>
    private const double MaxSeaTransportMeters = 2_000_000.0;

    /// <summary>大陸棚都墊滿了之後，第二輪最多墊到海平面下這個深度（公尺）——沉積物不會自己長出海面。</summary>
    private const float FilledSeaDepthMeters = 5f;

    private readonly SimulationGrid _grid;
    private readonly float[] _surface;
    private readonly float[] _waterSurface;
    private readonly double[] _load;
    private readonly double[] _basinLoad;
    private readonly int[] _lakeCellCount;
    private readonly double[] _lakeCapacity;
    private readonly int[] _visitStamp;
    private readonly List<int> _visited = [];
    private int _stamp;
    private readonly int _maxSeaHops;

    /// <summary>診斷用：最近一步每一格要求的地表降低量（公尺，夾上限之前）。</summary>
    internal float[] LastRequestedLowering { get; }

    /// <summary>最近一步刨下來的物質（立方公里，沉積物＋褶皺＋基岩）。</summary>
    public double LastErodedVolumeKm3 { get; private set; }

    /// <summary>最近一步堆下去的物質（立方公里），應該等於 <see cref="LastErodedVolumeKm3"/>。</summary>
    public double LastDepositedVolumeKm3 { get; private set; }

    /// <summary>最近一步堆進湖裡的（立方公里）。</summary>
    public double LastLakeDepositKm3 { get; private set; }

    /// <summary>最近一步刨掉的褶皺（立方公里）。</summary>
    public double LastReliefErodedKm3 { get; private set; }

    /// <summary>診斷用：這一步各種備援路徑堆下去的量（公尺×格）：[0] 第二輪墊到海面、[1] 平均鋪、[2] 沒有去處就地堆。</summary>
    internal double[] FallbackDeposits { get; } = new double[3];

    /// <summary>診斷用：這一步最大的一筆就地堆（路徑 1 ＝ 主迴圈沒有流向、2 ＝ 往下游送到底）、格子與量。</summary>
    internal (int Path, int Cell, double Amount) LargestDump { get; private set; }

    /// <summary>建立 v2 的外營力模型。</summary>
    /// <param name="grid">網格。</param>
    public FluvialErosion(SimulationGrid grid)
    {
        _grid = grid;
        var n = grid.CellCount;
        _surface = new float[n];
        _waterSurface = new float[n];
        _load = new double[n];
        _basinLoad = new double[n];
        _lakeCellCount = new int[n];
        _lakeCapacity = new double[n];
        _visitStamp = new int[n];
        LastRequestedLowering = new float[n];
        _maxSeaHops = Math.Max(1, (int)Math.Round(MaxSeaTransportMeters / grid.AverageCellSpacingMeters));
    }

    /// <summary>
    /// 推進一步：用上一步的水（海、湖、流向、流量）刨蝕並搬運。排在均衡注水之前（跟 <see cref="Erosion"/> 同一個慣例）。
    /// </summary>
    /// <param name="state">模擬狀態：沉積物、褶皺、地殼厚度會被更新。</param>
    /// <param name="hydrology">上一步解好的水系。</param>
    /// <param name="deltaYears">這一步代表多少年。</param>
    public void Step(SimulationState state, Hydrology hydrology, double deltaYears)
    {
        var deltaMyr = deltaYears / 1_000_000.0;
        var n = state.CellCount;
        var seaLevel = state.SeaLevelMeters;
        var lakeOf = hydrology.LakeOf;
        var basinOf = hydrology.BasinOf;
        var parent = hydrology.FlowParent;
        var order = hydrology.FlowOrder;
        var discharge = hydrology.Discharge;

        Parallel.For(0, n, c =>
        {
            _surface[c] = state.SurfaceElevationAt(c);
            _waterSurface[c] = _surface[c] + state.WaterDepth[c];
        });
        Array.Clear(_load);
        Array.Clear(_basinLoad);
        Array.Clear(_lakeCellCount);
        Array.Clear(_lakeCapacity);
        Array.Clear(LastRequestedLowering);
        LargestDump = default;
        for (var c = 0; c < n; c++) if (lakeOf[c] >= 0) _lakeCellCount[lakeOf[c]]++;

        double eroded = 0, deposited = 0, lake = 0, relief = 0;

        // 泛洪順序反過來走就是上游先（每一格的流向 parent 都在它前面被泛洪到）
        for (var i = hydrology.FlowOrderCount - 1; i >= 0; i--)
        {
            var c = order[i];
            if (IsSea(state, lakeOf, c) || lakeOf[c] >= 0) continue;

            var p = parent[c];
            var lowering = BaseWeatheringMetersPerMyr * deltaMyr
                + WaveErosionMetersPerMyr * SeaNeighborCount(state, lakeOf, c) * deltaMyr;
            if (p >= 0)
            {
                // 坡度以水面為基準：流進海或湖的那一格，落差算到水面
                var drop = _surface[c] - _waterSurface[p];
                if (drop > 0f)
                {
                    var stream = StreamPowerCoefficient
                        * Math.Pow(Math.Max(discharge[c], 0f), DischargeExponent)
                        * drop / SlopeReferenceLengthMeters;
                    if (state.Temperature[c] < GlacialTemperatureC) stream *= GlacialErosionMultiplier;
                    lowering += Math.Min(stream * deltaMyr, drop * MaxStreamLoweringFractionOfDrop);
                }
            }
            LastRequestedLowering[c] = (float)lowering;
            lowering = Math.Min(lowering, MaxSurfaceLoweringPerStepMeters);
            // 基準面：什麼營力都刨不到海平面以下（海浪只削出海面附近的海蝕平台）。少了這一條，海浪與風化把低平原
            // 一路刨到海面下，40 Myr 時 A 北、C 北的平原整片變成淺海、一座大島沉掉（P7，2026-10-20）
            lowering = Math.Min(lowering, Math.Max(0.0, _surface[c] - seaLevel));
            if (lowering > 0)
            {
                var (removed, fromRelief) = RemoveMaterial(state, c, lowering);
                _load[c] += removed;
                eroded += removed;
                relief += fromRelief;
            }
            if (_load[c] <= 0) continue;

            // 自己就在存水盆地裡（湖邊的坡）：泥沙進湖
            var basin = basinOf[c];
            if (basin >= 0 && hydrology.HoldsWater(basin) && _lakeCellCount[basin] > 0)
            {
                _basinLoad[basin] += _load[c];
                _load[c] = 0;
                continue;
            }
            if (p < 0)
            {
                // 沒有流向：上一步的主洋種子、海平面降了之後露出水面（流向是上一步泛洪的）。
                // 第一版就地堆，整條河的泥沙每一步倒在露出來的那一格，堆出 20~30 公里的沉積柱（P7，2026-10-19）
                deposited += DepositNearestSea(state, lakeOf, c, _load[c], 1);
                _load[c] = 0;
                continue;
            }
            if (IsSea(state, lakeOf, p))
            {
                deposited += DepositInSea(state, lakeOf, p, _load[c]);
                _load[c] = 0;
                continue;
            }
            if (lakeOf[p] >= 0)
            {
                _basinLoad[lakeOf[p]] += _load[c];
                _load[c] = 0;
                continue;
            }
            _load[p] += _load[c];
            _load[c] = 0;
        }

        // 進湖的泥沙依水深比例鋪在湖底（湖會慢慢被填淺），最多填到湖面；多的從溢出口接著往下游，
        // 可能流進下一個湖，所以重複到沒有湖還有待處理的泥沙為止
        for (var c = 0; c < n; c++)
        {
            var b = lakeOf[c];
            if (b >= 0) _lakeCapacity[b] += Math.Max(0f, hydrology.LakeLevelAt(c) - _surface[c]);
        }
        for (var round = 0; round < MaxLakeRounds; round++)
        {
            for (var c = 0; c < n; c++)
            {
                var b = lakeOf[c];
                if (b < 0 || _basinLoad[b] <= 0 || _lakeCapacity[b] <= 0) continue;
                var fill = Math.Min(1.0, _basinLoad[b] / _lakeCapacity[b]);
                var add = Math.Max(0f, hydrology.LakeLevelAt(c) - _surface[c]) * fill;
                state.SedimentThickness[c] += (float)add;
                _surface[c] += (float)add;
                deposited += add;
                lake += add;
            }
            var overflowed = false;
            for (var b = 0; b < n; b++)
            {
                if (_basinLoad[b] <= 0) continue;
                var excess = _basinLoad[b] - _lakeCapacity[b];
                _lakeCapacity[b] = Math.Max(0, _lakeCapacity[b] - _basinLoad[b]);
                _basinLoad[b] = 0;
                if (excess <= 0) continue;
                overflowed = true;
                deposited += RouteDownstream(state, hydrology, hydrology.BasinOutlet(b), excess);
            }
            if (!overflowed) break;
        }
        // 輪數用完還沒處理的（湖一個接一個都滿了，極少見）：就地平均鋪在湖上
        for (var c = 0; c < n; c++)
        {
            var b = lakeOf[c];
            if (b < 0 || _basinLoad[b] <= 0) continue;
            var add = _basinLoad[b] / _lakeCellCount[b];
            state.SedimentThickness[c] += (float)add;
            deposited += add;
            lake += add;
        }

        var cellKm2 = _grid.AverageCellAreaSquareMeters / 1e6;
        LastErodedVolumeKm3 = eroded / 1000.0 * cellKm2;
        LastDepositedVolumeKm3 = deposited / 1000.0 * cellKm2;
        LastLakeDepositKm3 = lake / 1000.0 * cellKm2;
        LastReliefErodedKm3 = relief / 1000.0 * cellKm2;
    }

    /// <summary>主洋的海（湖不算）。</summary>
    private static bool IsSea(SimulationState state, int[] lakeOf, int c) => state.WaterDepth[c] > 0f && lakeOf[c] < 0;

    /// <summary>
    /// 進海的沉積物：從河口一圈一圈往外（陸地也走過去、但只填海格），把每一格墊到 <see cref="ShelfDepthMeters"/>——堆出來的是往外長的大陸棚。
    /// 走到 <see cref="_maxSeaHops"/> 圈還有剩，第二輪把走過的格子墊到接近海面，再有剩才平均鋪上去。回傳堆下去的量（等於 <paramref name="amount"/>）。
    ///
    /// 第一版是「往最深的海鄰居一格一格走，走不下去就整包倒在那裡」：碰到封閉的海溝或走到上限就倒在一格，
    /// 40 Myr 時最高的 300 格陸地平均有 5.9 公里的沉積物（P7，2026-10-19）。
    /// </summary>
    private double DepositInSea(SimulationState state, int[] lakeOf, int start, double amount)
    {
        var shelf = state.SeaLevelMeters - ShelfDepthMeters;
        var remaining = amount;
        _stamp++;
        _visited.Clear();
        _visited.Add(start);
        _visitStamp[start] = _stamp;
        var ringStart = 0;
        for (var ring = 0; ring <= _maxSeaHops && ringStart < _visited.Count; ring++)
        {
            var ringEnd = _visited.Count;
            for (var i = ringStart; i < ringEnd; i++)
            {
                var cur = _visited[i];
                if (IsSea(state, lakeOf, cur))
                {
                    var room = shelf - _surface[cur];
                    if (room > 0f)
                    {
                        var put = Math.Min(room, remaining);
                        state.SedimentThickness[cur] += (float)put;
                        _surface[cur] += (float)put;
                        remaining -= put;
                        if (remaining <= 0) return amount;
                    }
                }
                if (ring == _maxSeaHops) continue;
                // 陸地也走（只是不填）：水深是上一步的，板塊動過之後河口可能接到一兩格孤立的小水窪，
                // 只走海格的話整包泥沙倒在那一格，堆出 20 公里的沉積柱（P7，2026-10-19）
                var baseIndex = cur * SimulationGrid.MaxNeighborsPerCell;
                for (var k = 0; k < SimulationGrid.MaxNeighborsPerCell; k++)
                {
                    var nb = _grid.Neighbors[baseIndex + k];
                    if (nb == SimulationGrid.NoNeighbor || _visitStamp[nb] == _stamp) continue;
                    _visitStamp[nb] = _stamp;
                    _visited.Add(nb);
                }
            }
            ringStart = ringEnd;
        }
        // 大陸棚都滿了：第二輪墊到接近海面
        _visited.RemoveAll(cell => !IsSea(state, lakeOf, cell));
        if (_visited.Count == 0) _visited.Add(start);
        var filled = state.SeaLevelMeters - FilledSeaDepthMeters;
        foreach (var cell in _visited)
        {
            var room = filled - _surface[cell];
            if (room <= 0f) continue;
            var put = Math.Min(room, remaining);
            FallbackDeposits[0] += put;
            state.SedimentThickness[cell] += (float)put;
            _surface[cell] += (float)put;
            remaining -= put;
            if (remaining <= 0) return amount;
        }
        // 連海面都到了（極少見）：平均鋪上去
        FallbackDeposits[1] += remaining;
        var each = (float)(remaining / _visited.Count);
        foreach (var cell in _visited)
        {
            state.SedimentThickness[cell] += each;
            _surface[cell] += each;
        }
        return amount;
    }

    /// <summary>
    /// 從 <paramref name="cell"/> 沿流向往下游送（不再刨蝕）：到海就 <see cref="DepositInSea"/>，到存水的湖就記進那個湖待處理。
    /// 回傳這一趟已經堆下去的量（進湖的那一份之後才堆，不算在這裡）。
    /// </summary>
    private double RouteDownstream(SimulationState state, Hydrology hydrology, int cell, double amount)
    {
        var lakeOf = hydrology.LakeOf;
        var basinOf = hydrology.BasinOf;
        var parent = hydrology.FlowParent;
        var last = cell;
        for (var guard = 0; cell >= 0 && guard < state.CellCount; guard++)
        {
            if (IsSea(state, lakeOf, cell)) return DepositInSea(state, lakeOf, cell, amount);
            var b = lakeOf[cell] >= 0 ? lakeOf[cell] : basinOf[cell];
            if (b >= 0 && hydrology.HoldsWater(b) && _lakeCellCount[b] > 0)
            {
                _basinLoad[b] += amount;
                return 0;
            }
            last = cell;
            cell = parent[cell];
        }
        if (last < 0) return 0;
        return DepositNearestSea(state, lakeOf, last, amount, 2);
    }

    /// <summary>
    /// 沒有流向可走的泥沙：找最近的海格交給 <see cref="DepositInSea"/>；附近都沒有海（極少見）才就地堆。
    /// </summary>
    private double DepositNearestSea(SimulationState state, int[] lakeOf, int cell, double amount, int path)
    {
        _stamp++;
        _visited.Clear();
        _visited.Add(cell);
        _visitStamp[cell] = _stamp;
        for (var i = 0; i < _visited.Count && i < MaxNearestSeaSearch; i++)
        {
            var cur = _visited[i];
            if (IsSea(state, lakeOf, cur)) return DepositInSea(state, lakeOf, cur, amount);
            var baseIndex = cur * SimulationGrid.MaxNeighborsPerCell;
            for (var k = 0; k < SimulationGrid.MaxNeighborsPerCell; k++)
            {
                var nb = _grid.Neighbors[baseIndex + k];
                if (nb == SimulationGrid.NoNeighbor || _visitStamp[nb] == _stamp) continue;
                _visitStamp[nb] = _stamp;
                _visited.Add(nb);
            }
        }
        FallbackDeposits[2] += amount;
        if (amount > LargestDump.Amount) LargestDump = (path, cell, amount);
        state.SedimentThickness[cell] += (float)amount;
        _surface[cell] += (float)amount;
        return amount;
    }

    /// <summary>找最近的海最多看幾格。</summary>
    private const int MaxNearestSeaSearch = 5000;

    /// <summary>
    /// 拿掉相當於地表降低 <paramref name="surfaceLoweringMeters"/> 公尺的物質：先沉積物（換算均衡反彈）、再凸起的褶皺（1:1，褶皺沒有均衡補償），
    /// 最後基岩（換算均衡反彈，見 <see cref="Erosion"/>）。回傳拿掉的物質厚度（公尺，之後是下游的沉積物）與其中褶皺的那一份。
    /// </summary>
    private static (double Removed, double FromRelief) RemoveMaterial(SimulationState state, int cell, double surfaceLoweringMeters)
    {
        var remaining = surfaceLoweringMeters;
        var removed = 0.0;

        var sediment = state.SedimentThickness[cell];
        if (sediment > 0f)
        {
            // 沉積物也是均衡補償的載重：拿掉 1 公尺只降 (ρ地函 − ρ沉積)/ρ地函 ≈ 0.24 公尺（見 Isostasy.LoadDepressionMeters）
            var take = Math.Min(sediment, remaining / SedimentSurfaceRatio);
            state.SedimentThickness[cell] = (float)(sediment - take);
            removed += take;
            remaining -= take * SedimentSurfaceRatio;
        }

        var fromRelief = 0.0;
        var relief = state.StructuralRelief[cell];
        if (remaining > 0 && relief > 0f)
        {
            fromRelief = Math.Min(relief, remaining);
            state.StructuralRelief[cell] = (float)(relief - fromRelief);
            removed += fromRelief;
            remaining -= fromRelief;
        }
        if (remaining <= 0) return (removed, fromRelief);

        var crustRemovedMeters = remaining / Isostasy.IsostaticSurfaceRatio(state.Crust[cell]);
        var crustKm = state.CrustThickness[cell];
        var takeKm = Math.Min(crustKm - Isostasy.MinCrustThicknessKm, crustRemovedMeters / 1000.0);
        if (takeKm <= 0) return (removed, fromRelief);
        state.CrustThickness[cell] = (float)(crustKm - takeKm);
        return (removed + takeKm * 1000.0, fromRelief);
    }

    /// <summary>
    /// 有幾個鄰居是夠深的海（受浪的邊數，見 <see cref="WaveBaseDepthMeters"/>）。
    ///
    /// 只算外面是深水的邊：寬的大陸棚、淺海會把浪的能量消耗掉，海岸後退會自己慢下來。第一版每一條面海的邊都算，
    /// 低平原被海浪一路往內陸削（削到海平面、變海、下一格又面海），40 Myr 時兩塊大陸北緯 50 度以上的大陸地殼有一半變成淺海（P7，2026-10-20）。
    /// </summary>
    private int SeaNeighborCount(SimulationState state, int[] lakeOf, int cell)
    {
        var count = 0;
        var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
        for (var k = 0; k < SimulationGrid.MaxNeighborsPerCell; k++)
        {
            var nb = _grid.Neighbors[baseIndex + k];
            if (nb != SimulationGrid.NoNeighbor && IsSea(state, lakeOf, nb) && state.WaterDepth[nb] > WaveBaseDepthMeters) count++;
        }
        return count;
    }
}
