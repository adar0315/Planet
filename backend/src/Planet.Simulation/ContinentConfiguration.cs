namespace Planet.Simulation;

/// <summary>
/// 「陸地集中成兩大塊，外加至少兩個大島」的判斷——v2 規劃的停止條件（2026-10-16 使用者拍板）。
///
/// ## 為什麼停止條件是陸地的形狀，不是年數
///
/// 使用者要的是一個特定的畫面：兩塊主要大陸，加上像澳洲、格陵蘭那樣的大島。
/// 板塊構造是循環的（威爾遜旋迴），那個配置會出現、維持一陣子、然後散掉，
/// 所以要在它出現的時候停下來，而不是跑固定的年數。
///
/// ## 門檻比照地球
///
/// 地球的陸地：亞非歐約 57%、美洲約 28%、澳洲 5.2%、格陵蘭 1.5%。
/// 預設門檻見 <see cref="ContinentCriteria.Default"/>。
///
/// ## 先排除碎島
///
/// 「一塊陸地」是陸地格的連通塊。marker-in-cell 的取樣會在海岸附近留下上千個一兩格的碎島
/// （見 <see cref="SmallIslandCleanup"/>），面積上微不足道但數量壓倒性——不排除的話
/// 它們不會影響「最大兩塊佔多少」，卻會讓「陸地總面積」的分母包含一堆雜訊。
/// 所以比例的分母只算**夠大的**陸塊。
/// </summary>
public static class ContinentConfiguration
{
    /// <summary>分析目前的陸地配置。</summary>
    /// <param name="grid">網格。</param>
    /// <param name="state">模擬狀態，用 <see cref="SimulationState.WaterDepth"/> 判斷海陸。</param>
    /// <param name="criteria">判斷門檻，預設 <see cref="ContinentCriteria.Default"/>。</param>
    public static ContinentConfigurationReport Analyze(
        SimulationGrid grid, SimulationState state, ContinentCriteria? criteria = null)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(state);
        var c = criteria ?? ContinentCriteria.Default;

        var sizes = LandMassSizes(grid, state);
        var cellAreaKm2 = grid.AverageCellAreaSquareMeters / 1_000_000.0;
        var significant = sizes.Where(s => s * cellAreaKm2 >= c.MinLandMassAreaSquareKm).ToList();
        significant.Sort((a, b) => b.CompareTo(a));

        var land = significant.Sum(s => (long)s);
        double Share(int index) => land > 0 && index < significant.Count ? (double)significant[index] / land : 0;

        var largest = Share(0);
        var second = Share(1);
        var bigIslands = significant.Skip(2).Count(s =>
        {
            var share = (double)s / land;
            return share >= c.MinBigIslandShare && share <= c.MaxBigIslandShare;
        });

        var waterCells = 0;
        for (var cell = 0; cell < state.CellCount; cell++)
        {
            if (state.WaterDepth[cell] > 0f) waterCells++;
        }

        var twoLargest = largest + second;
        var isMet = twoLargest >= c.MinTwoLargestShare
            && largest >= c.MinEachOfTwoShare
            && second >= c.MinEachOfTwoShare
            && bigIslands >= c.MinBigIslandCount;

        return new ContinentConfigurationReport
        {
            LargestShare = largest,
            SecondShare = second,
            TwoLargestShare = twoLargest,
            BigIslandCount = bigIslands,
            LandMassCount = significant.Count,
            OceanFraction = (double)waterCells / state.CellCount,
            IsMet = isMet,
        };
    }

    /// <summary>陸地連通塊的大小（格數）。用明確的堆疊而不是遞迴：大陸可以有五萬多格。</summary>
    internal static List<int> LandMassSizes(SimulationGrid grid, SimulationState state)
    {
        var visited = new bool[state.CellCount];
        var sizes = new List<int>();
        var stack = new Stack<int>();

        for (var start = 0; start < state.CellCount; start++)
        {
            if (visited[start] || state.WaterDepth[start] > 0f) continue;

            var size = 0;
            visited[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var cell = stack.Pop();
                size++;
                var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
                for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
                {
                    var neighbor = grid.Neighbors[baseIndex + s];
                    if (neighbor == SimulationGrid.NoNeighbor) continue;
                    if (visited[neighbor] || state.WaterDepth[neighbor] > 0f) continue;
                    visited[neighbor] = true;
                    stack.Push(neighbor);
                }
            }

            sizes.Add(size);
        }

        return sizes;
    }
}

/// <summary>
/// 「兩大塊＋大島」的門檻。比例都是**佔陸地面積**的比例（排除碎島之後）。
/// </summary>
/// <param name="MinTwoLargestShare">最大的兩塊合計至少佔多少。地球 85%。</param>
/// <param name="MinEachOfTwoShare">那兩塊**各自**至少佔多少——否則一塊超大陸加一塊小陸地也會過。地球 57% / 28%。</param>
/// <param name="MinBigIslandCount">大島至少幾個（使用者指定至少 2）。地球約 4 個（澳洲、格陵蘭、南極…）。</param>
/// <param name="MinBigIslandShare">「大島」的下限。格陵蘭 1.5%。</param>
/// <param name="MaxBigIslandShare">「大島」的上限，超過就算一塊大陸了。澳洲 5.2%。</param>
/// <param name="MinLandMassAreaSquareKm">小於這個面積的陸塊當成碎島，不算進任何比例（跟碎島清除同一個門檻）。</param>
public sealed record ContinentCriteria(
    double MinTwoLargestShare,
    double MinEachOfTwoShare,
    int MinBigIslandCount,
    double MinBigIslandShare,
    double MaxBigIslandShare,
    double MinLandMassAreaSquareKm)
{
    /// <summary>
    /// 預設門檻（2026-10-16）。v1 在水量 1,150、第 1,600 步量到的是 66.9% / 18.2% / 3.4% / 3.4%——
    /// 合計與大島都過了，只差第二塊還沒到 20%。第一次看到實際結果之後很可能要調。
    /// </summary>
    public static ContinentCriteria Default { get; } = new(
        MinTwoLargestShare: 0.75,
        MinEachOfTwoShare: 0.20,
        MinBigIslandCount: 2,
        MinBigIslandShare: 0.01,
        MaxBigIslandShare: 0.10,
        MinLandMassAreaSquareKm: SmallIslandCleanup.MaxCleanedIslandAreaSquareKm);
}

/// <summary><see cref="ContinentConfiguration.Analyze"/> 的結果。</summary>
public sealed record ContinentConfigurationReport
{
    /// <summary>最大一塊陸地佔陸地面積的比例。</summary>
    public required double LargestShare { get; init; }

    /// <summary>第二大一塊佔陸地面積的比例。</summary>
    public required double SecondShare { get; init; }

    /// <summary>最大兩塊合計。</summary>
    public required double TwoLargestShare { get; init; }

    /// <summary>大島的數量（不含最大的兩塊）。</summary>
    public required int BigIslandCount { get; init; }

    /// <summary>排除碎島之後的陸塊總數。</summary>
    public required int LandMassCount { get; init; }

    /// <summary>海洋佔全球面積的比例。**只監看，不是停止條件的一部分。**</summary>
    public required double OceanFraction { get; init; }

    /// <summary>四個條件是否全部成立。</summary>
    public required bool IsMet { get; init; }
}
