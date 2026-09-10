namespace Planet.Simulation;

/// <summary>
/// 被陸地封死的海會被鑿開一個出口。
///
/// ## 這是地質作用，不是事後修圖
///
/// 板塊運動會把海圍起來——島弧接上大陸、兩塊陸地夾攏、隆起的地形切斷海道。
/// 但那種屏障在地質時間尺度上**不穩定**：兩側都是海、屏障又只有幾百公尺高的時候，
/// 波浪侵蝕、斷層陷落、溢流下切都會把它切穿。地球上有現成的例子——
/// 直布羅陀在桑克良洪水時被切開，地中海在數個月到數年內重新灌滿；
/// 博斯普魯斯海峽把黑海接回地中海也是同一類事件。
///
/// 所以這件事要放在模擬迴圈裡跟著板塊一起跑，而不是最後對成品修圖：
/// 修圖只會被下一步的板塊運動再度封死，而且改格子的話會被下一次
/// <c>PlateTectonics.Rasterize</c> 從標記覆蓋回去（CLAUDE.md 第2條）。
///
/// ## 它做的是「連通」，不是「排水」
///
/// <c>SimulationEngine.ApplyIsostasyAndFillWater</c> 用的是**單一全球海平面**：
/// 每一格的水深就是 <c>max(0, 海平面 − 地表高度)</c>，完全不看連不連得到大洋。
/// 所以被封死的內海並不是「困在高處的水」，它的水位跟外海本來就一樣高。
///
/// 鑿開出口之後**水位不會下降**，改變的是拓樸：那片海從此併入世界大洋、有了出口，
/// 洋流才走得通。要看到「水真的流走」得先讓注水改成依連通性逐盆地求解，那是另一件事。
///
/// ## 找出口用單次 priority-flood，不是每個海各搜一次
///
/// 從主洋往外做一次 Dijkstra 式的泛洪，鍵值是「路徑上最高的那一格有多高」
/// （minimax，不是距離總和）。跑完之後每一格都知道**通往主洋最低要翻過多高**，
/// 於是所有內海的最佳突破口一次就全部解出來——37 個海各搜一次的話要掃 37 遍。
/// </summary>
public sealed class SeaOutletBreach
{
    /// <summary>
    /// 多大的水域才算「海」、值得鑿出口（平方公里）。
    ///
    /// **一定要寫成面積而不是格數**（CLAUDE.md 第4條）：n=150 一格約 192 平方公里、
    /// n=24 一格約 7,485 平方公里，寫死格數在兩種解析度上代表完全不同的東西。
    ///
    /// 取 2,000：約等於 n=150 的 10 格。比這更小的水窪是**湖**不是海——
    /// 地球上也到處都是內流湖（裏海、死海、鹹海），把它們全部打通反而不像真的星球。
    /// 實測 n=150 跑到 6,250 萬年時，1,256 個封閉水域裡有 1,072 個只有 1~2 格，
    /// 真正夠大的只有 37 個。
    /// </summary>
    private const double MinSeaAreaSquareKm = 2000.0;

    /// <summary>
    /// 屏障最高可以到海平面以上幾公尺，還會被切穿。
    ///
    /// 太低的話大部分內海都打不通，太高的話會憑空在三公里高的造山帶上切出一道峽谷。
    /// 取 800：低矮的地峽、年輕的島弧、新隆起的門檻都在這個範圍內，
    /// 而成熟的褶皺山脈（動輒2~4公里）切不動——那種內海在現實裡的結局是變成鹹湖，
    /// 不是被鑿穿。<see cref="LastColHeights"/> 會把實際遇到的鞍部高度印出來，
    /// 調這個值之前先看那份分布。
    /// </summary>
    private const float MaxBreachColMeters = 800f;

    /// <summary>
    /// 鑿開之後，通道的地表要低於海平面幾公尺。
    ///
    /// 不能只切到「剛好等於海平面」：均衡與侵蝕每一步都在動，剛好齊平的通道
    /// 下一步就可能又浮回水面上，於是每隔幾步就重新鑿一次同一個地方。
    /// 取 120（約等於地球冰期的海平面振幅），一次切到夠深，通道才留得住。
    /// </summary>
    private const float BreachDepthBelowSeaLevelMeters = 120f;

    private int[] _waterLabel = [];
    private int[] _parent = [];
    private float[] _barrier = [];
    private float[] _surface = [];

    /// <summary>診斷用：上一次鑿開了幾個內海。</summary>
    public int LastBreachCount { get; private set; }

    /// <summary>診斷用：被鑿開的內海總共有幾格。</summary>
    public int LastBreachedSeaCells { get; private set; }

    /// <summary>診斷用：夠大但屏障太高、這次沒鑿開的內海有幾個。</summary>
    public int LastTooHighCount { get; private set; }

    /// <summary>
    /// 診斷用：因為屏障太高而沒鑿開的內海裡，**最大的那個有幾格**。
    ///
    /// 這個數字比「有幾個沒鑿開」重要得多：沒鑿開的若都是小海，門檻設得剛好；
    /// 但如果有一個跟主洋同量級的海一直卡在門檻外，那整顆星球就是兩個互不相通的大洋，
    /// 而總數上只會顯示「1 個」，完全看不出嚴重性。
    /// </summary>
    public int LastTooHighLargestCells { get; private set; }

    /// <summary>診斷用：那個最大的未鑿通內海，屏障高出海平面幾公尺。</summary>
    public float LastTooHighLargestColMeters { get; private set; }

    /// <summary>
    /// 診斷用：想削卻被地殼厚度下限夾住、沒削到目標深度的格數。
    ///
    /// 這個計數器存在是因為那種失敗**完全看不出來**：通道照樣被標記成鑿過，
    /// 只是實際上還高於水面。持續不為零就代表屏障是薄地殼（削不動），
    /// 要嘛降低 <see cref="MaxBreachColMeters"/>、要嘛換一種鑿法。
    /// </summary>
    public int LastCarveShortfallCells { get; private set; }

    /// <summary>診斷用：這次處理到的內海，各自的鞍部高出海平面多少公尺（由低到高）。</summary>
    public IReadOnlyList<float> LastColHeights { get; private set; } = [];

    /// <summary>
    /// 找出被陸地封死的海，把最低的那道屏障鑿穿。
    ///
    /// **改的是地殼厚度不是高度**：高度每一步都由 <see cref="Isostasy.BedrockElevation"/>
    /// 從厚度重算，直接改高度會在下一步被蓋掉。而且要排在
    /// <c>WriteGridBackToMarkers</c> 之前，改動才會寫回標記、不被下一次取樣覆蓋。
    /// </summary>
    /// <param name="grid">網格。</param>
    /// <param name="state">模擬狀態。用上一步的水深判斷海陸，跟造礁／侵蝕同一個慣例。</param>
    public void Step(SimulationGrid grid, SimulationState state)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(state);

        LastBreachCount = 0;
        LastBreachedSeaCells = 0;
        LastTooHighCount = 0;
        LastTooHighLargestCells = 0;
        LastTooHighLargestColMeters = 0f;
        LastCarveShortfallCells = 0;

        var cellCount = state.CellCount;
        EnsureCapacity(cellCount);

        for (var cell = 0; cell < cellCount; cell++)
        {
            _surface[cell] = state.SurfaceElevationAt(cell);
        }

        var componentSizes = LabelWater(grid, state);
        if (componentSizes.Count <= 1) return; // 全部相通，沒事可做

        var mainOcean = LargestComponent(componentSizes);
        var minSeaCells = (int)Math.Ceiling(
            MinSeaAreaSquareKm * 1_000_000.0 / grid.AverageCellAreaSquareMeters);

        // 只有夠大的內海值得處理，先確認有沒有，免得白跑一趟泛洪
        var hasCandidate = false;
        for (var component = 0; component < componentSizes.Count; component++)
        {
            if (component == mainOcean || componentSizes[component] < minSeaCells) continue;
            hasCandidate = true;
            break;
        }

        if (!hasCandidate) return;

        FloodFromMainOcean(grid, mainOcean, cellCount);

        // 每個內海取「通往主洋最低要翻過多高」最小的那一格當突破口
        var bestCell = new int[componentSizes.Count];
        var bestBarrier = new float[componentSizes.Count];
        Array.Fill(bestCell, -1);
        Array.Fill(bestBarrier, float.MaxValue);

        for (var cell = 0; cell < cellCount; cell++)
        {
            var component = _waterLabel[cell];
            if (component < 0 || component == mainOcean) continue;
            if (componentSizes[component] < minSeaCells) continue;
            if (_barrier[cell] >= bestBarrier[component]) continue;

            bestBarrier[component] = _barrier[cell];
            bestCell[component] = cell;
        }

        var cols = new List<float>();
        for (var component = 0; component < componentSizes.Count; component++)
        {
            if (bestCell[component] < 0) continue;

            var colAboveSeaLevel = bestBarrier[component] - state.SeaLevelMeters;
            cols.Add(colAboveSeaLevel);

            if (colAboveSeaLevel > MaxBreachColMeters)
            {
                LastTooHighCount++;
                if (componentSizes[component] > LastTooHighLargestCells)
                {
                    LastTooHighLargestCells = componentSizes[component];
                    LastTooHighLargestColMeters = colAboveSeaLevel;
                }
                continue;
            }

            Carve(grid, state, bestCell[component]);
            LastBreachCount++;
            LastBreachedSeaCells += componentSizes[component];
        }

        cols.Sort();
        LastColHeights = cols;
    }

    private void EnsureCapacity(int cellCount)
    {
        if (_waterLabel.Length == cellCount) return;
        _waterLabel = new int[cellCount];
        _parent = new int[cellCount];
        _barrier = new float[cellCount];
        _surface = new float[cellCount];
    }

    /// <summary>
    /// 把水域標成連通塊，回傳每塊的格數。陸地是 −1。
    ///
    /// **水陸的判準要跟泛洪用的是同一個量**：這裡用的是這一步重算的地表高度
    /// （<c>_surface</c> 對比海平面），不是 <c>state.WaterDepth</c>。
    /// 用後者會不一致——水深是上一步注水時算的，而造礁／侵蝕／冰蓋在這一步已經改過地表高度，
    /// 於是「上一步還是陸地、這一步已經沉到海面下」的格子會被當成屏障。
    /// 症狀是鞍部高度出現**負值**（整條路徑都在海平面以下卻說它被封死），
    /// 2026-09-10 第一版就是這樣，實測出現 −354 公尺的幻影屏障。
    /// </summary>
    private List<int> LabelWater(SimulationGrid grid, SimulationState state)
    {
        Array.Fill(_waterLabel, -1);
        var sizes = new List<int>();
        var stack = new Stack<int>();
        var seaLevel = state.SeaLevelMeters;

        for (var start = 0; start < state.CellCount; start++)
        {
            if (_waterLabel[start] >= 0 || _surface[start] >= seaLevel) continue;

            var component = sizes.Count;
            var size = 0;
            stack.Push(start);
            _waterLabel[start] = component;

            while (stack.Count > 0)
            {
                var cell = stack.Pop();
                size++;

                var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
                for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
                {
                    var neighbor = grid.Neighbors[baseIndex + s];
                    if (neighbor == SimulationGrid.NoNeighbor) continue;
                    if (_waterLabel[neighbor] >= 0 || _surface[neighbor] >= seaLevel) continue;

                    _waterLabel[neighbor] = component;
                    stack.Push(neighbor);
                }
            }

            sizes.Add(size);
        }

        return sizes;
    }

    private static int LargestComponent(List<int> sizes)
    {
        var best = 0;
        for (var i = 1; i < sizes.Count; i++)
        {
            if (sizes[i] > sizes[best]) best = i;
        }
        return best;
    }

    /// <summary>
    /// 從主洋往外做 minimax 泛洪：<c>_barrier[cell]</c> ＝ 從這一格走到主洋，
    /// 路上**最高的那一格**最低可以壓到多高。<c>_parent</c> 記下那條路徑。
    /// </summary>
    private void FloodFromMainOcean(SimulationGrid grid, int mainOcean, int cellCount)
    {
        Array.Fill(_barrier, float.MaxValue);
        Array.Fill(_parent, -1);

        var queue = new PriorityQueue<int, float>();
        for (var cell = 0; cell < cellCount; cell++)
        {
            if (_waterLabel[cell] != mainOcean) continue;
            _barrier[cell] = float.MinValue;
            queue.Enqueue(cell, float.MinValue);
        }

        while (queue.TryDequeue(out var cell, out var key))
        {
            // 過期的項目（同一格可能被放進去好幾次，留下最好的那次）
            if (key > _barrier[cell]) continue;

            var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                var neighbor = grid.Neighbors[baseIndex + s];
                if (neighbor == SimulationGrid.NoNeighbor) continue;

                var candidate = MathF.Max(key, _surface[neighbor]);
                if (candidate >= _barrier[neighbor]) continue;

                _barrier[neighbor] = candidate;
                _parent[neighbor] = cell;
                queue.Enqueue(neighbor, candidate);
            }
        }
    }

    /// <summary>
    /// 從突破口沿著 <c>_parent</c> 走回主洋，把沿途（以及緊鄰的一圈）削到海面以下。
    ///
    /// ## 為什麼要連鄰居一起削
    ///
    /// 只削一格寬的話通道**撐不過幾步**：板塊以約5公分／年移動，一步5萬年就是2.5公里，
    /// 而 n=150 的格距是8.6公里——標記大約3~4步就穿過一格，上游沒被削過的地殼
    /// 隨即把通道填回去。實測（每100萬年檢查一次、只削一格寬）的症狀正是這樣：
    /// 機制每次都回報「鑿開了39~52個」，連通性卻毫無改善，主洋一度只剩水域的56.6%。
    ///
    /// 削成約三格寬（約26公里）之後通道能撐十步以上，而且那也比較接近真實的海峽尺度
    /// （直布羅陀約14公里、麻六甲最窄處約38公里）。
    ///
    /// 這也是為什麼檢查頻率要高：真實的海峽是**洋流持續刷深**才維持暢通的，
    /// 不是岩石被切開一次就永遠留著。
    /// </summary>
    private void Carve(SimulationGrid grid, SimulationState state, int from)
    {
        var target = state.SeaLevelMeters - BreachDepthBelowSeaLevelMeters;

        for (var cell = from; cell >= 0; cell = _parent[cell])
        {
            CarveOne(state, cell, target);

            var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                var neighbor = grid.Neighbors[baseIndex + s];
                if (neighbor == SimulationGrid.NoNeighbor) continue;
                CarveOne(state, neighbor, target);
            }
        }
    }

    /// <summary>把一格削到目標高度以下。削不到就照實記下來，不假裝成功。</summary>
    private void CarveOne(SimulationState state, int cell, float target)
    {
        if (_surface[cell] <= target) return;

        var drop = _surface[cell] - target;

        // 高度對厚度的斜率：厚度多一公里，基岩抬高 MetersPerKmOfCrust，
        // 再加上密度項（地殼比參考密度輕的話會多浮一點）。
        var referenceDensity = state.Crust[cell] == CrustType.Continental
            ? Isostasy.ContinentalCrustDensity
            : Isostasy.OceanicCrustDensity;
        var metersPerKm = Isostasy.MetersPerKmOfCrust
            + 1000f * (referenceDensity - state.CrustDensity[cell]) / Isostasy.MantleDensity;

        if (metersPerKm <= 1f) return; // 斜率太小，削厚度沒有用（不該發生，保險）

        var wanted = drop / metersPerKm;
        var available = state.CrustThickness[cell] - Isostasy.MinCrustThicknessKm;
        var thinnedKm = MathF.Min(wanted, MathF.Max(0f, available));

        state.CrustThickness[cell] -= thinnedKm;

        // **削不到目標就不要謊報**（2026-09-10 的 bug）：原本無條件把 _surface 設成 target，
        // 於是厚度下限一夾制，機制就以為自己鑿通了、後面所有判斷都建立在沒發生的事情上。
        // 薄地殼（洋殼只有7公里、下限3公里）最多只能降 4 × 182 = 728 公尺，
        // 而實測鞍部最高到 712 公尺、需要降 832 公尺——剛好不夠。
        _surface[cell] -= thinnedKm * metersPerKm;
        if (thinnedKm < wanted) LastCarveShortfallCells++;
    }
}
