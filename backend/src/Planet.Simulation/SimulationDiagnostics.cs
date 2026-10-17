using System.Runtime.InteropServices;

namespace Planet.Simulation;

/// <summary>
/// 「固定儀表」的分析（v2 規劃 P0）：每個階段的驗收都看同一組數字。
///
/// v1 每一次被騙都是只看了其中一兩項——陸地比例正常但畫面是碎斑、
/// 厚度分布正常但礁體躺在深海、機制回報成功但實際沒觸發。
/// 所以這組數字要**一起看**，而且要隨時拿得到，不是每次要查時才寫一個探針。
/// </summary>
public static class SimulationDiagnostics
{
    /// <summary>
    /// 高度分布：地表相對海平面的高度，每 binMeters 一格的格數。
    /// 地球是雙峰的（大陸台地／洋底），海平面附近的面積很少——單峰或在海平面附近很平坦，
    /// 海岸線就會隨一點點海平面變動大片翻面。
    /// </summary>
    /// <param name="state">模擬狀態。</param>
    /// <param name="binMeters">每一格的寬度（公尺）。</param>
    public static IReadOnlyList<HypsometryBin> Hypsometry(SimulationState state, double binMeters = 100)
    {
        var counts = new SortedDictionary<long, int>();
        for (var cell = 0; cell < state.CellCount; cell++)
        {
            var relative = state.SurfaceElevationAt(cell) - state.SeaLevelMeters;
            var bin = (long)Math.Floor(relative / binMeters);
            counts[bin] = counts.TryGetValue(bin, out var n) ? n + 1 : 1;
        }

        return counts.Select(kv => new HypsometryBin(kv.Key * binMeters, kv.Value)).ToList();
    }

    /// <summary>
    /// 陸塊大小分布。v1 量到的典型病徵是「6 塊大陸佔 92% 的面積、卻有上千個一兩格的碎島」，
    /// 只看陸地比例完全看不出來。
    /// </summary>
    public static IReadOnlyList<LandMassBucket> LandMassBuckets(SimulationGrid grid, SimulationState state)
    {
        var sizes = ContinentConfiguration.LandMassSizes(grid, state);
        var total = Math.Max(1L, sizes.Sum(s => (long)s));
        (int Min, int Max, string Label)[] buckets =
        [
            (1, 1, "1格"), (2, 2, "2格"), (3, 5, "3~5格"), (6, 10, "6~10格"),
            (11, 50, "11~50格"), (51, 500, "51~500格"), (501, int.MaxValue, "500格以上"),
        ];

        return buckets.Select(b =>
        {
            var inBucket = sizes.Where(s => s >= b.Min && s <= b.Max).ToList();
            var cells = inBucket.Sum(s => (long)s);
            return new LandMassBucket(b.Label, inBucket.Count, cells, (double)cells / total);
        }).ToList();
    }

    /// <summary>
    /// 整個狀態的雜湊（FNV-1a）。**跨行程重現的檢查用**：兩個行程跑同一個種子到同一步，
    /// 雜湊必須相同。不同就代表兩邊跑的不是同一顆星球——
    /// 2026-10-16 就是這樣抓到測試與服務的半徑差了 4.3 毫米。
    /// </summary>
    public static ulong StateHash(SimulationEngine engine)
    {
        var s = engine.State;
        var h = 14695981039346656037UL;
        h = Mix(h, s.BedrockElevation);
        h = Mix(h, s.SedimentThickness);
        h = Mix(h, s.OrganicThickness);
        h = Mix(h, s.VegetationThickness);
        h = Mix(h, s.IceThickness);
        h = Mix(h, s.WaterDepth);
        h = Mix(h, s.Moisture);
        h = Mix(h, s.AtmosphericMoisture);
        h = Mix(h, s.Precipitation);
        h = Mix(h, s.Temperature);
        h = Mix(h, s.PlateId);
        h = Mix(h, s.Crust);
        h = Mix(h, s.CrustAge);
        h = Mix(h, s.CrustDensity);
        h = Mix(h, s.CrustThickness);
        h = Mix(h, s.Boundary);
        h = Mix(h, s.VolcanicActivity);
        h = Mix(h, s.OceanicOnly);
        h = Mix(h, [s.SeaLevelMeters]);
        return h;
    }

    /// <summary>
    /// 各機制上一步實際做了多少。CLAUDE.md 3.5：「參數改了結果不動」要先確認這條路有沒有在跑，
    /// 而事後統計看不出來（閘門若有作用，被轉走的格子當下就不在統計群裡了），只有直接數觸發次數才看得到。
    /// </summary>
    public static MechanismCounts Mechanisms(SimulationEngine engine)
    {
        var t = engine.Tectonics;
        return new MechanismCounts
        {
            RidgeCrustKm = t.LastRidgeCrustKm,
            ArcCrustKm = t.LastArcCrustKm,
            AccretionCrustKm = t.LastAccretionCrustKm,
            SubductedCrustKm = t.LastSubductedCrustKm,
            OceanSubductionCount = t.LastOceanSubductionCount,
            ContinentCollisionCount = t.LastContinentCollisionCount,
            CollisionThickeningCount = t.LastCollisionThickeningCount,
            MisfitFillCount = t.LastMisfitFillCount,
            NoDonorCount = t.LastNoDonorCount,
            MarkerCount = t.MarkerCount,
            PlumeCrustKm = engine.Plumes.LastAddedCrustKm,
            FissureCrustKm = engine.Fissures.LastAddedCrustKm,
            ErodedCellCount = engine.ErosionDiagnostics.LastErodedCellCount,
            ErodedCrustVolumeKm3 = engine.ErosionDiagnostics.LastRemovedCrustVolumeKm3,
            BreachCount = engine.Breach.LastBreachCount,
            BreachTooHighCount = engine.Breach.LastTooHighCount,
            IceSupplyLimit = engine.IceSupplyLimit,
            CrustTransport = t.Transport,
            FrameNewCrustCells = t.LastFrameNewCrustCells,
            FrameTransferredCells = t.LastFrameTransferredCells,
            FrameConsumedPoints = t.LastFrameConsumedPoints,
            FrameConsumedContinentalPoints = t.LastFrameConsumedContinentalPoints,
        };
    }

    private static ulong Mix<T>(ulong h, T[] values) where T : unmanaged
    {
        foreach (var b in MemoryMarshal.AsBytes<T>(values))
        {
            h ^= b;
            h *= 1099511628211UL;
        }
        return h;
    }
}

/// <summary>高度分布的一格。</summary>
/// <param name="FromMeters">這一格的下界（相對海平面，公尺）。</param>
/// <param name="Cells">落在這一格的格數。</param>
public sealed record HypsometryBin(double FromMeters, int Cells);

/// <summary>陸塊大小分布的一組。</summary>
/// <param name="Label">大小範圍。</param>
/// <param name="Masses">有幾塊。</param>
/// <param name="Cells">這些陸塊合計幾格。</param>
/// <param name="ShareOfLand">佔全部陸地的比例。</param>
public sealed record LandMassBucket(string Label, int Masses, long Cells, double ShareOfLand);

/// <summary>
/// 各機制**上一步**的觸發量（見 <see cref="SimulationDiagnostics.Mechanisms"/>）。
/// 海洋出口每 20 萬年才檢查一次，所以 <see cref="BreachCount"/> 是「上一次檢查」的結果。
/// </summary>
public sealed record MechanismCounts
{
    /// <summary>中洋脊新增的地殼（公里·格）。</summary>
    public required double RidgeCrustKm { get; init; }

    /// <summary>島弧增厚新增的地殼（公里·格）。</summary>
    public required double ArcCrustKm { get; init; }

    /// <summary>增生新增的地殼（公里·格）。</summary>
    public required double AccretionCrustKm { get; init; }

    /// <summary>隱沒消耗的地殼（公里·格）。</summary>
    public required double SubductedCrustKm { get; init; }

    /// <summary>聚合格中洋殼隱沒的格數。</summary>
    public required int OceanSubductionCount { get; init; }

    /// <summary>聚合格中大陸對撞的格數。</summary>
    public required int ContinentCollisionCount { get; init; }

    /// <summary>對撞增厚的格數。</summary>
    public required int CollisionThickeningCount { get; init; }

    /// <summary>取樣錯配、沿用舊厚度的空格數。</summary>
    public required int MisfitFillCount { get; init; }

    /// <summary>其中連鄰居標記都借不到的格數。</summary>
    public required int NoDonorCount { get; init; }

    /// <summary>存活的標記數。</summary>
    public required int MarkerCount { get; init; }

    /// <summary>熱柱新增的地殼（公里·格）。</summary>
    public required double PlumeCrustKm { get; init; }

    /// <summary>裂縫火山新增的地殼（公里·格）。</summary>
    public required double FissureCrustKm { get; init; }

    /// <summary>被侵蝕的格數。</summary>
    public required int ErodedCellCount { get; init; }

    /// <summary>侵蝕移除的地殼體積（立方公里）。</summary>
    public required double ErodedCrustVolumeKm3 { get; init; }

    /// <summary>上一次檢查鑿開了幾個被圍死的海。</summary>
    public required int BreachCount { get; init; }

    /// <summary>上一次檢查有幾個海因為屏障太高而沒鑿開。</summary>
    public required int BreachTooHighCount { get; init; }

    /// <summary>冰蓋被水量上限打的折數（1 代表沒打折）。</summary>
    public required float IceSupplyLimit { get; init; }

    /// <summary>地殼搬運方式。下面四個 Frame* 只有板塊座標系才有值，標記法時是 0。</summary>
    public required CrustTransport CrustTransport { get; init; }

    /// <summary>板塊座標系：上一步長出新海洋地殼的格數（張裂）。</summary>
    public required int FrameNewCrustCells { get; init; }

    /// <summary>板塊座標系：上一步從相鄰板塊轉交過來的格數。</summary>
    public required int FrameTransferredCells { get; init; }

    /// <summary>板塊座標系：上一步被蓋過而消耗掉的點數（隱沒）。</summary>
    public required int FrameConsumedPoints { get; init; }

    /// <summary>板塊座標系：其中是大陸地殼的點數（P4 加碰撞規則之前大陸也會被吃掉）。</summary>
    public required int FrameConsumedContinentalPoints { get; init; }
}
