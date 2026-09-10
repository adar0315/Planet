namespace Planet.Simulation;

/// <summary>
/// 海洋的連通性分析：哪些水域彼此相通、哪些內海被陸地封死、水域裡有幾條獨立環路。
///
/// ## 為什麼「有沒有環」比「連不連通」重要
///
/// 洋流要能循環，水域不能只是**連通**，還必須**有環**——樹狀的水系只能來回振盪，
/// 沒有辦法形成環流。地球上最重要的兩條環流都靠環：南極繞極流繞著整顆星球一圈，
/// 溫鹽環流則串起大西洋、印度洋與太平洋。
///
/// 球面上的環數不必真的去找環，可以直接數出來（Alexander 對偶）：
/// 把球面切成水域與陸地，若水域連通、陸地有 k 個連通塊（島），
/// 則水域是「球面挖掉 k 個洞」，**獨立環路數 = k − 1**。
/// 所以島愈多、環愈多；只有「陸地連成一整塊」（k = 1）時水域才完全沒有環。
///
/// 但拓樸上的環不等於**有用**的環：寬度只有一格的海峽在拓樸上照樣是環，
/// 洋流卻過不去。所以另外量最窄處，見 <see cref="OceanTopologyReport.NarrowestLinkCells"/>。
/// </summary>
public static class OceanTopology
{
    /// <summary>
    /// 分析目前的水陸配置。
    /// </summary>
    /// <param name="grid">網格。</param>
    /// <param name="state">模擬狀態，用 <see cref="SimulationState.WaterDepth"/> 判斷是不是水。</param>
    /// <returns>連通性報告。</returns>
    public static OceanTopologyReport Analyze(SimulationGrid grid, SimulationState state)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(state);

        var waterLabel = LabelComponents(grid, state, water: true, out var waterSizes);
        _ = LabelComponents(grid, state, water: false, out var landSizes);

        var waterCells = 0;
        for (var cell = 0; cell < state.CellCount; cell++)
        {
            if (IsWater(state, cell)) waterCells++;
        }

        // 主洋＝最大的水域連通塊。其餘的水都是被陸地圍住的內海／湖。
        var mainOcean = -1;
        var mainOceanSize = 0;
        for (var component = 0; component < waterSizes.Count; component++)
        {
            if (waterSizes[component] <= mainOceanSize) continue;
            mainOceanSize = waterSizes[component];
            mainOcean = component;
        }

        var enclosedCells = 0;
        var enclosedSeas = new List<int>();
        for (var component = 0; component < waterSizes.Count; component++)
        {
            if (component == mainOcean) continue;
            enclosedCells += waterSizes[component];
            enclosedSeas.Add(waterSizes[component]);
        }

        enclosedSeas.Sort();
        enclosedSeas.Reverse();

        return new OceanTopologyReport
        {
            CellCount = state.CellCount,
            WaterCells = waterCells,
            LandCells = state.CellCount - waterCells,
            WaterComponentCount = waterSizes.Count,
            LandComponentCount = landSizes.Count,
            MainOceanCells = mainOceanSize,
            EnclosedWaterCells = enclosedCells,
            EnclosedSeaSizes = enclosedSeas,
            LargestLandMasses = Top(landSizes, 10),
            IndependentLoops = Math.Max(0, landSizes.Count - 1),
            NarrowestLinkCells = NarrowestLink(grid, state, waterLabel, mainOcean),
        };
    }

    /// <summary>這一格是不是水。</summary>
    private static bool IsWater(SimulationState state, int cell) => state.WaterDepth[cell] > 0f;

    /// <summary>
    /// 把水域（或陸地）標記成連通塊，回傳每格的塊編號（不屬於該類別的格子是 −1）。
    ///
    /// 用明確的堆疊而不是遞迴：22.5 萬格的連通塊可以很長，遞迴會爆堆疊。
    /// </summary>
    private static int[] LabelComponents(
        SimulationGrid grid, SimulationState state, bool water, out List<int> sizes)
    {
        var label = new int[state.CellCount];
        Array.Fill(label, -1);
        sizes = [];

        var stack = new Stack<int>();
        for (var start = 0; start < state.CellCount; start++)
        {
            if (label[start] >= 0 || IsWater(state, start) != water) continue;

            var component = sizes.Count;
            var size = 0;
            stack.Push(start);
            label[start] = component;

            while (stack.Count > 0)
            {
                var cell = stack.Pop();
                size++;

                var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
                for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
                {
                    var neighbor = grid.Neighbors[baseIndex + s];
                    if (neighbor == SimulationGrid.NoNeighbor) continue;
                    if (label[neighbor] >= 0 || IsWater(state, neighbor) != water) continue;

                    label[neighbor] = component;
                    stack.Push(neighbor);
                }
            }

            sizes.Add(size);
        }

        return label;
    }

    /// <summary>
    /// 主洋裡最窄的「咽喉」有幾格寬。
    ///
    /// 定義：對主洋的每一格，量它到最近陸地的格數距離（多源 BFS 從海岸往外擴）。
    /// 這個距離在海峽中央會取到局部極大值，而那個極大值就是海峽的半寬。
    /// 這裡回傳**所有局部極大值裡最小的那個**的兩倍，也就是最窄的水道寬度。
    ///
    /// 這是估計值不是精確的圖論最小割——精確版要對每一條環路求最小割，
    /// 成本高很多，而這裡只需要知道「有沒有窄到洋流過不去」。
    /// </summary>
    private static int NarrowestLink(
        SimulationGrid grid, SimulationState state, int[] waterLabel, int mainOcean)
    {
        if (mainOcean < 0) return 0;

        // 多源 BFS：所有陸地格距離 0，往水裡擴散
        var distance = new int[state.CellCount];
        Array.Fill(distance, int.MaxValue);
        var queue = new Queue<int>();

        for (var cell = 0; cell < state.CellCount; cell++)
        {
            if (IsWater(state, cell)) continue;
            distance[cell] = 0;
            queue.Enqueue(cell);
        }

        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                var neighbor = grid.Neighbors[baseIndex + s];
                if (neighbor == SimulationGrid.NoNeighbor) continue;
                if (distance[neighbor] != int.MaxValue) continue;

                distance[neighbor] = distance[cell] + 1;
                queue.Enqueue(neighbor);
            }
        }

        // 主洋裡「離岸距離」的局部極大值＝各條水道的半寬，取最小的那條
        var narrowest = int.MaxValue;
        for (var cell = 0; cell < state.CellCount; cell++)
        {
            if (waterLabel[cell] != mainOcean) continue;

            var isLocalMax = true;
            var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                var neighbor = grid.Neighbors[baseIndex + s];
                if (neighbor == SimulationGrid.NoNeighbor) continue;
                if (distance[neighbor] <= distance[cell]) continue;
                isLocalMax = false;
                break;
            }

            if (isLocalMax) narrowest = Math.Min(narrowest, distance[cell]);
        }

        return narrowest == int.MaxValue ? 0 : narrowest * 2;
    }

    /// <summary>最大的前幾塊，由大到小。</summary>
    private static IReadOnlyList<int> Top(List<int> sizes, int count)
    {
        var sorted = new List<int>(sizes);
        sorted.Sort();
        sorted.Reverse();
        return sorted.Count <= count ? sorted : sorted.GetRange(0, count);
    }
}

/// <summary>
/// <see cref="OceanTopology.Analyze"/> 的結果。
/// </summary>
public sealed record OceanTopologyReport
{
    /// <summary>總格數。</summary>
    public required int CellCount { get; init; }

    /// <summary>水域格數。</summary>
    public required int WaterCells { get; init; }

    /// <summary>陸地格數。</summary>
    public required int LandCells { get; init; }

    /// <summary>水域被分成幾個互不相通的連通塊（1 代表全部相通）。</summary>
    public required int WaterComponentCount { get; init; }

    /// <summary>陸地被分成幾塊。獨立環路數就是由這個decide的，見 <see cref="IndependentLoops"/>。</summary>
    public required int LandComponentCount { get; init; }

    /// <summary>主洋（最大的水域連通塊）有幾格。</summary>
    public required int MainOceanCells { get; init; }

    /// <summary>不屬於主洋的水域格數，也就是**被陸地封死的內海**總格數。</summary>
    public required int EnclosedWaterCells { get; init; }

    /// <summary>每個被封死的內海各有幾格，由大到小。</summary>
    public required IReadOnlyList<int> EnclosedSeaSizes { get; init; }

    /// <summary>最大的幾塊陸地各有幾格，由大到小。</summary>
    public required IReadOnlyList<int> LargestLandMasses { get; init; }

    /// <summary>
    /// 水域裡有幾條獨立環路（＝陸地連通塊數 − 1，見 <see cref="OceanTopology"/> 的說明）。
    /// 0 代表水域是樹狀的、完全沒有辦法形成環流。
    /// </summary>
    public required int IndependentLoops { get; init; }

    /// <summary>
    /// 主洋最窄的水道大約幾格寬。拓樸上的環若窄到只有一兩格，洋流實際上過不去。
    /// </summary>
    public required int NarrowestLinkCells { get; init; }
}
