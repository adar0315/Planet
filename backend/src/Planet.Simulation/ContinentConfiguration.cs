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
///
/// ## 兩大塊還要「完整」（2026-10-17 加入）
///
/// 只看面積比例會被騙：v1 第一次成立時（第 1,160 步）兩大塊是 51% ＋ 30%，畫出來卻是
/// 沿板塊邊界彎成弧形的細長山脈帶、串起一堆半淹的碎塊，像一張網。所以兩大塊各自還要量
/// **海岸破碎度**（<see cref="Raggedness"/>）。內部的湖洞也會算進海岸，不用另外量。
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

        var label = LabelLandMasses(grid, state, out var sizes);
        var cellAreaKm2 = grid.AverageCellAreaSquareMeters / 1_000_000.0;
        var significant = sizes.Where(s => s * cellAreaKm2 >= c.MinLandMassAreaSquareKm).ToList();
        significant.Sort((a, b) => b.CompareTo(a));

        // 最大兩塊的陸塊編號（同樣大小時取編號小的，結果才是決定性的）
        var byArea = Enumerable.Range(0, sizes.Count).OrderByDescending(i => sizes[i]).ThenBy(i => i).ToList();
        var (largestRagged, secondRagged) = Raggedness(grid, state, label,
            byArea.Count > 0 ? byArea[0] : -1, byArea.Count > 1 ? byArea[1] : -1,
            c.InlandWaterIsCoast ? null : MainOceanMask(grid, state));

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
        var landShareOfSurface = (double)land / state.CellCount;
        var isMet = landShareOfSurface >= c.MinLandShareOfSurface
            && twoLargest >= c.MinTwoLargestShare
            && largest >= c.MinEachOfTwoShare
            && second >= c.MinEachOfTwoShare
            && bigIslands >= c.MinBigIslandCount
            && largestRagged <= c.MaxRaggedness
            && secondRagged <= c.MaxRaggedness;

        return new ContinentConfigurationReport
        {
            LargestShare = largest,
            SecondShare = second,
            TwoLargestShare = twoLargest,
            BigIslandCount = bigIslands,
            LargestRaggedness = largestRagged,
            SecondRaggedness = secondRagged,
            LandMassCount = significant.Count,
            LandShareOfSurface = landShareOfSurface,
            OceanFraction = (double)waterCells / state.CellCount,
            IsMet = isMet,
        };
    }

    /// <summary>
    /// 海岸破碎度：海岸格數 ÷ 同面積圓盤的海岸格數（六角網格上半徑 r 的圓盤約 3r² 格、6r 格海岸，
    /// 所以圓盤的海岸 ≈ 2√3·√面積）。1 ＝ 圓盤；地球的大陸在這個解析度（約 14 公里）估計 2~3。
    /// 「海岸格」＝至少有一個水鄰居的陸地格，所以內部的湖洞與胡椒狀的海洋地殼也會算進來。
    /// 沒有那一塊（編號 −1）時回傳 0。
    /// </summary>
    private static (double First, double Second) Raggedness(
        SimulationGrid grid, SimulationState state, int[] label, int first, int second, bool[]? ocean)
    {
        int area1 = 0, coast1 = 0, area2 = 0, coast2 = 0;
        for (var cell = 0; cell < state.CellCount; cell++)
        {
            var id = label[cell];
            if (id < 0 || (id != first && id != second)) continue;

            var wet = false;
            var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell && !wet; s++)
            {
                var neighbor = grid.Neighbors[baseIndex + s];
                wet = neighbor != SimulationGrid.NoNeighbor && state.WaterDepth[neighbor] > 0f
                    && (ocean is null || ocean[neighbor]);
            }

            if (id == first) { area1++; if (wet) coast1++; }
            else { area2++; if (wet) coast2++; }
        }

        static double Ratio(int coast, int area) => area == 0 ? 0 : coast / (2 * Math.Sqrt(3) * Math.Sqrt(area));
        return (Ratio(coast1, area1), Ratio(coast2, area2));
    }

    /// <summary>主洋（最大的水域連通塊）的格子（見 <see cref="ContinentCriteria.InlandWaterIsCoast"/>）。</summary>
    private static bool[] MainOceanMask(SimulationGrid grid, SimulationState state)
    {
        var n = state.CellCount;
        var label = new int[n];
        Array.Fill(label, -1);
        var sizes = new List<int>();
        var stack = new Stack<int>();
        for (var start = 0; start < n; start++)
        {
            if (label[start] >= 0 || state.WaterDepth[start] <= 0f) continue;
            var id = sizes.Count;
            var size = 0;
            label[start] = id;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                size++;
                var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
                for (var k = 0; k < SimulationGrid.MaxNeighborsPerCell; k++)
                {
                    var nb = grid.Neighbors[baseIndex + k];
                    if (nb == SimulationGrid.NoNeighbor || label[nb] >= 0 || state.WaterDepth[nb] <= 0f) continue;
                    label[nb] = id;
                    stack.Push(nb);
                }
            }
            sizes.Add(size);
        }
        var main = sizes.Count == 0 ? -1 : sizes.IndexOf(sizes.Max());
        var mask = new bool[n];
        for (var c = 0; c < n; c++) mask[c] = label[c] == main && main >= 0;
        return mask;
    }

    /// <summary>陸地連通塊的大小（格數）。</summary>
    internal static List<int> LandMassSizes(SimulationGrid grid, SimulationState state)
    {
        LabelLandMasses(grid, state, out var sizes);
        return sizes;
    }

    /// <summary>
    /// 把陸地格標上連通塊編號（水格是 −1），並回傳每一塊的大小。
    /// 用明確的堆疊而不是遞迴：大陸可以有五萬多格。
    /// </summary>
    private static int[] LabelLandMasses(SimulationGrid grid, SimulationState state, out List<int> sizes)
    {
        var label = new int[state.CellCount];
        Array.Fill(label, -1);
        sizes = [];
        var stack = new Stack<int>();

        for (var start = 0; start < state.CellCount; start++)
        {
            if (label[start] >= 0 || state.WaterDepth[start] > 0f) continue;

            var id = sizes.Count;
            var size = 0;
            label[start] = id;
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
                    if (label[neighbor] >= 0 || state.WaterDepth[neighbor] > 0f) continue;
                    label[neighbor] = id;
                    stack.Push(neighbor);
                }
            }

            sizes.Add(size);
        }

        return label;
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
/// <param name="MaxRaggedness">最大兩塊各自的海岸破碎度上限（見 <see cref="ContinentConfigurationReport.LargestRaggedness"/>）。</param>
/// <param name="InlandWaterIsCoast">
/// 海岸破碎度要不要把大陸內部的水（湖、被圍住的內海）也算成海岸。v1 是（用來抓大陸中間夾著洋殼的胡椒）；
/// v2（板塊座標系）不算：依連通性注水之後大陸上有湖、褶皺的谷地裡也有小湖，湖是大陸的一部分，不該讓大陸看起來破碎
/// （2026-10-19 使用者指定；褶皺加大後第二塊的破碎度被谷地的小湖推到 4.1）。只有碰到主洋（最大的水域）的才算海岸。
/// </param>
/// <param name="MinLandShareOfSurface">
/// 陸地（排除碎島）至少佔星球表面多少。比例條件只看陸地「內部」的分配，陸地很少時一兩個小島也會符合
/// 「兩大塊＋大島」——P4 實測陸地只有 1~2% 時停止條件就成立了。2026-10-17 使用者定 20%（地球約 29%）。
/// </param>
public sealed record ContinentCriteria(
    double MinTwoLargestShare,
    double MinEachOfTwoShare,
    int MinBigIslandCount,
    double MinBigIslandShare,
    double MaxBigIslandShare,
    double MinLandMassAreaSquareKm,
    double MaxRaggedness,
    double MinLandShareOfSurface,
    bool InlandWaterIsCoast = true)
{
    /// <summary>
    /// 預設門檻（2026-10-16）。v1 在水量 1,150、第 1,600 步量到的是 66.9% / 18.2% / 3.4% / 3.4%——
    /// 合計與大島都過了，只差第二塊還沒到 20%。第一次看到實際結果之後很可能要調。
    ///
    /// 破碎度上限 4：地球的大陸在這個解析度估計 2~3，v1 第 1,160 步的兩大塊是 12.6 與 12.5
    /// （把內部湖洞全部填掉也只降到 9.2），v1 的澳洲大小的島是 4.4。
    /// </summary>
    public static ContinentCriteria Default { get; } = new(
        MinTwoLargestShare: 0.75,
        MinEachOfTwoShare: 0.20,
        MinBigIslandCount: 2,
        MinBigIslandShare: 0.01,
        MaxBigIslandShare: 0.10,
        MinLandMassAreaSquareKm: SmallIslandCleanup.MaxCleanedIslandAreaSquareKm,
        MaxRaggedness: 4.0,
        MinLandShareOfSurface: 0.20);
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

    /// <summary>
    /// 最大一塊的海岸破碎度：海岸格數 ÷ 同面積圓盤的海岸格數。1 ＝ 圓盤，愈大愈破碎
    /// （細長的弧形、內部湖洞、周圍參差的海岸都會讓它變大）。
    /// </summary>
    public required double LargestRaggedness { get; init; }

    /// <summary>第二大一塊的海岸破碎度。</summary>
    public required double SecondRaggedness { get; init; }

    /// <summary>陸地（排除碎島）佔星球表面的比例。</summary>
    public required double LandShareOfSurface { get; init; }

    /// <summary>排除碎島之後的陸塊總數。</summary>
    public required int LandMassCount { get; init; }

    /// <summary>海洋佔全球面積的比例。**只監看，不是停止條件的一部分。**</summary>
    public required double OceanFraction { get; init; }

    /// <summary>全部條件（陸地面積、比例、大島、破碎度）是否成立。</summary>
    public required bool IsMet { get; init; }
}
