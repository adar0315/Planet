namespace Planet.Simulation;

/// <summary>湖（封閉盆地）的種類，見 <see cref="Hydrology"/>。</summary>
public enum LakeKind : byte
{
    /// <summary>入水多於蒸發，灌到溢出口、多的水溢出去（淡水湖）。</summary>
    Fresh,

    /// <summary>入水與湖面蒸發在溢出口以下就平衡了，沒有出口（鹹湖、內流湖）。</summary>
    Saline,

    /// <summary>入水少到連最低的一格都灌不滿（乾盆地）。</summary>
    Dry,
}

/// <summary>一個封閉盆地這一步的水量收支（診斷與顯示用），見 <see cref="Hydrology"/>。</summary>
/// <param name="Kind">種類。</param>
/// <param name="Cells">盆地的格數（灌滿到溢出口時會淹到的範圍）。</param>
/// <param name="LakeCells">實際有水的格數。</param>
/// <param name="SpillMeters">溢出口的高度。</param>
/// <param name="LevelMeters">湖面高度（乾盆地是最低點的高度）。</param>
/// <param name="VolumeCubicMeters">湖水體積。</param>
/// <param name="Inflow">入水（集水區的逕流加上上游溢出來的水，降水的相對單位 × 格）。</param>
/// <param name="Outflow">從溢出口流出去的水（同一個單位）。</param>
public readonly record struct LakeBasin(
    LakeKind Kind, int Cells, int LakeCells, float SpillMeters, float LevelMeters, double VolumeCubicMeters, double Inflow, double Outflow);

/// <summary>
/// 依連通性注水（v2 P5，2026-10-18，只在板塊座標系模式使用）：只有跟大洋相通的低窪處才是海，
/// 被陸地圍住的盆地依集水區的逕流與湖面蒸發，決定是淡水湖、鹹湖還是乾盆地。
///
/// ## 為什麼要依連通性
///
/// v1 只有一個全球海平面，每一格的水深就是「海平面 − 地表」，被陸地圍死、低於海平面的盆地也被灌成海——
/// 水是憑空出現在那裡的。地球上那種盆地是死海、裏海、卡塔拉窪地：有水沒水、鹹的淡的，取決於那裡的氣候。
///
/// ## 做法
///
/// 1. **泛洪**：從上一步的主洋（地表低於上一步海平面的最大連通塊）做一次 priority-flood，
///    <see cref="FloodLevel"/> ＝ 這一格通往主洋的路上、最高的那一格最低可以壓到多高。
///    海平面是 L 的時候，<c>FloodLevel &lt; L</c> 的格子整條路都在水面下，就是海；這個判斷對任何 L 都成立，
///    所以海平面的二分搜尋可以直接用它（見 SimulationEngine 的海平面求解）。
/// 2. **盆地**：<c>FloodLevel &gt; 地表</c>、而且溢出口不在海面下的連通塊。同一塊裡的泛洪高度都一樣，就是溢出口的高度
///    （相鄰兩格都在窪地裡的話，泛洪高度必定相等）。
/// 3. **集水**：泛洪記下每一格是從哪一格擴過來的（<c>_parent</c>，也就是灌滿之後的流向），
///    每一格的水流到沿這條路往下游遇到的第一個盆地；上游盆地溢出來的水再流進下游的盆地。
/// 4. **水量收支**：盆地的格子由低到高一格一格淹，每淹一格，那一格從「逕流 P×r」變成「湖面 P − E」（r 含高山融雪，見 RunoffFractionAt），
///    淹到入水剛好被蒸發抵銷為止。淹到溢出口還有剩就溢出去。
///
/// 盆地內部若有更小的子盆地，這裡當成同一個水位（只看整個盆地由低到高的格子），子盆地之間的分水嶺不另外處理。
///
/// ## 單位
///
/// 模擬的降水是相對尺度（典型 0~30，見 <see cref="Climate"/>），湖面蒸發因此也用相對尺度：
/// 15°C 時等於全球平均降水 × <see cref="EvaporationAt15CPerMeanPrecipitation"/>，隨溫度指數變化。
/// 地球上溫帶湖面每年蒸發約 800~1,000 公釐、全球平均降水約 1,000 公釐，炎熱的沙漠湖可達 2,000 公釐以上。
/// </summary>
public sealed class Hydrology
{
    private readonly SimulationGrid _grid;
    private readonly float[] _flood;
    private readonly int[] _parent;
    private readonly int[] _order;
    private readonly int[] _basinOf;
    private readonly int[] _drain;
    private readonly float[] _lakeLevel;
    private readonly int[] _lakeOf;
    private readonly int[] _label;
    private readonly List<LakeBasin> _basins = [];
    private int _orderCount;
    private readonly double[] _surfacePrefix;
    private int _orderVersion;
    private int _prefixFor = -1;
    private int _basinsFor = -1;
    private readonly List<int> _firstCell = [];
    private bool[] _holdsWater = [];
    private double[] _outflow = [];
    private readonly float[] _discharge;
    private readonly List<float> _spill = [];
    private readonly List<int> _downstream = [];
    private readonly List<double> _runoff = [];
    private readonly List<int> _cellStartList = [];
    private readonly List<int> _lakeCellsWritten = [];
    private int[] _cellStart = [0];
    private int[] _sortedCells = [];
    private double[] _loss = [];

    /// <summary>為網格配置暫存緩衝。</summary>
    /// <param name="grid">網格。</param>
    public Hydrology(SimulationGrid grid)
    {
        _grid = grid;
        var n = grid.CellCount;
        _flood = new float[n];
        _parent = new int[n];
        _order = new int[n];
        _basinOf = new int[n];
        _drain = new int[n];
        _lakeLevel = new float[n];
        _lakeOf = new int[n];
        Array.Fill(_lakeOf, -1);
        _label = new int[n];
        _surfacePrefix = new double[n + 1];
        _discharge = new float[n];
    }

    /// <summary>每一格通往主洋的路上、最高的那一格最低可以壓到多高（公尺）。見類別說明。</summary>
    public float[] FloodLevel => _flood;

    /// <summary>上一次 <see cref="SolveLakes"/> 的盆地（依泛洪先後排序，下游在前）。</summary>
    public IReadOnlyList<LakeBasin> Basins => _basins;

    /// <summary>每一格屬於哪個盆地的湖水（<see cref="Basins"/> 的索引），沒有湖水是 −1。</summary>
    public int[] LakeOf => _lakeOf;

    /// <summary>每一格的流向（泛洪記下的 parent，灌滿之後往主洋的那一格；-1 ＝ 主洋的種子）。</summary>
    internal int[] FlowParent => _parent;

    /// <summary>泛洪的處理順序（前 <see cref="FlowOrderCount"/> 個有效）：反過來走就是上游先。</summary>
    internal int[] FlowOrder => _order;

    /// <summary><see cref="FlowOrder"/> 裡有效的格數。</summary>
    internal int FlowOrderCount => _orderCount;

    /// <summary>每一格屬於哪個窪地盆地（-1 ＝ 不在窪地裡）。</summary>
    internal int[] BasinOf => _basinOf;

    /// <summary>這個盆地有沒有存水（湖）。</summary>
    /// <param name="basin">盆地編號（<see cref="BasinOf"/>）。</param>
    internal bool HoldsWater(int basin) => basin >= 0 && basin < _firstCell.Count && _holdsWater[basin];

    /// <summary>盆地滿了之後水從哪一格流出去（溢出口外面那一格；-1 ＝ 沒有）。</summary>
    /// <param name="basin">盆地編號（<see cref="BasinOf"/>）。</param>
    internal int BasinOutlet(int basin) => _parent[_firstCell[basin]];

    /// <summary>盆地的數目（<see cref="BasinOf"/> 的值域）。</summary>
    internal int BasinCount => _firstCell.Count;

    /// <summary>這個盆地平均往下游放多少水（降水的相對單位 × 格）；鹹湖、乾盆地是 0。</summary>
    /// <param name="basin">盆地編號。</param>
    internal double BasinOutflow(int basin) => basin >= 0 && basin < _firstCell.Count ? _outflow[basin] : 0;

    /// <summary>湖面高度（公尺），只對 <see cref="LakeOf"/> ≥ 0 的格子有意義。</summary>
    /// <param name="cell">格子。</param>
    internal float LakeLevelAt(int cell) => _lakeLevel[cell];

    /// <summary>上一次泛洪：主洋的種子有幾格（地表低於上一步海平面的最大連通塊）。</summary>
    public int LastSeedCells { get; private set; }

    /// <summary>上一次 <see cref="SolveLakes"/>：所有湖水的體積（立方公尺）。</summary>
    public double LastLakeVolume { get; private set; }

    /// <summary>上一次 <see cref="SolveLakes"/>：從盆地溢出、最後流進海裡的水（降水的相對單位 × 格）。</summary>
    public double LastOverflowToOcean { get; private set; }

    /// <summary>
    /// 湖面蒸發在 15°C 時是全球平均降水的幾倍（預設）。地球上溫帶湖面每年蒸發約 800~1,000 公釐、全球平均降水約 1,000 公釐。
    /// 2026-10-18 在種子 42 的劇本上比較過：1.0 時 40 Myr 只有淡水湖 6、鹹湖 6、乾盆地 60；0.6 是 46／22／19；0.4 是 69／12／14。使用者選 0.8。
    /// </summary>
    public const float DefaultEvaporationAt15CPerMeanPrecipitation = 0.8f;

    /// <summary>湖面蒸發在 15°C 時是全球平均降水的幾倍（預設 <see cref="DefaultEvaporationAt15CPerMeanPrecipitation"/>，校準用可以改）。</summary>
    public float EvaporationAt15CPerMeanPrecipitation { get; set; } = DefaultEvaporationAt15CPerMeanPrecipitation;

    /// <summary>湖面蒸發隨溫度變化：每變化這麼多度變成 e 倍（攝氏）。</summary>
    private const float EvaporationEFoldingC = 15f;

    /// <summary>湖面蒸發相對 15°C 的倍數下限與上限（極地與炎熱沙漠）。</summary>
    private const float MinEvaporationFactor = 0.2f;

    /// <inheritdoc cref="MinEvaporationFactor"/>
    private const float MaxEvaporationFactor = 2.5f;

    /// <summary>
    /// 比這個小的盆地（平方公里）不存水：地形雜訊造成的一兩格小坑，水直接往下游流。
    /// 寫成面積（CLAUDE.md 第 4 條），n=150 約 5 格。
    /// </summary>
    private const double MinBasinAreaSquareKm = 1_000.0;

    /// <summary>
    /// 比這個淺的窪地（溢出口減最低點，公尺）不存水，當成已經被河流切穿或被沉積填平：水繼續往下游流、匯成河。
    /// 2026-10-18 使用者覺得鹹湖太多：大陸內部起始地殼厚度的小起伏留下很多淺窪地，每個都成了集水區很小的封閉盆地，
    /// 乾一點的地方就是鹹湖（蒸發 0.8 時 40 Myr 鹹湖 31 個）。真實地形上這種窪地在地質時間裡早就被切穿出口，
    /// 留下來的湖多半在深的構造盆地（裂谷、前陸盆地）。
    /// </summary>
    private const float MinBasinDepthMeters = 150f;

    /// <summary>
    /// 每一格的河流流量（降水的相對單位 × 格，也就是上游所有格子的逕流加總，含湖溢出來的水）。
    /// 海與湖裡是 0。上一次 <see cref="ComputeDischarge"/> 的結果。
    /// </summary>
    public float[] Discharge => _discharge;

    /// <summary>
    /// 存檔：外營力（<see cref="FluvialErosion"/>）每一步用的是**上一步**的水系（流向、處理順序、盆地、湖、流量），
    /// 讀檔之後第一步要拿到同一份，續跑才會位元相同。不能讀檔時重算：解氣候的那一步降水在注水之後才更新，重算會得到不同的湖。
    /// </summary>
    /// <param name="writer">目標。</param>
    internal void WriteTo(BinaryWriter writer)
    {
        writer.Write(_orderCount);
        for (var i = 0; i < _orderCount; i++) writer.Write(_order[i]);
        SimulationSnapshot.WriteArray(writer, _parent);
        SimulationSnapshot.WriteArray(writer, _basinOf);
        SimulationSnapshot.WriteArray(writer, _lakeOf);
        SimulationSnapshot.WriteArray(writer, _lakeLevel);
        SimulationSnapshot.WriteArray(writer, _discharge);
        writer.Write(_firstCell.Count);
        // 湖的出流也要存（水文地貌規劃 H4）：四季的水文讀檔之後要知道每個湖平均放多少水下去
        for (var b = 0; b < _firstCell.Count; b++) { writer.Write(_firstCell[b]); writer.Write(_holdsWater[b]); writer.Write(_outflow[b]); }
    }

    /// <summary>讀檔，見 <see cref="WriteTo"/>。</summary>
    /// <param name="reader">來源。</param>
    internal void ReadFrom(BinaryReader reader)
    {
        _orderCount = reader.ReadInt32();
        for (var i = 0; i < _orderCount; i++) _order[i] = reader.ReadInt32();
        SimulationSnapshot.ReadArray(reader, _parent);
        SimulationSnapshot.ReadArray(reader, _basinOf);
        SimulationSnapshot.ReadArray(reader, _lakeOf);
        SimulationSnapshot.ReadArray(reader, _lakeLevel);
        SimulationSnapshot.ReadArray(reader, _discharge);
        var basins = reader.ReadInt32();
        _firstCell.Clear();
        if (_holdsWater.Length < basins) { _holdsWater = new bool[basins]; _outflow = new double[basins]; }
        for (var b = 0; b < basins; b++) { _firstCell.Add(reader.ReadInt32()); _holdsWater[b] = reader.ReadBoolean(); _outflow[b] = reader.ReadDouble(); }
        _lakeCellsWritten.Clear();
        for (var c = 0; c < _lakeOf.Length; c++) if (_lakeOf[c] >= 0) _lakeCellsWritten.Add(c);
        _orderVersion++;
    }

    /// <summary>
    /// 沿流向（泛洪記下的 <c>_parent</c>，灌滿之後往主洋的方向）把每一格的逕流往下游累加成河流流量。
    /// 流進存水盆地的水停在湖裡（已經算進湖的水量收支），湖滿了溢出去的水從溢出口接著往下流。
    /// 要在最後一次 <see cref="SolveLakes"/> 之後呼叫。
    /// </summary>
    /// <param name="state">模擬狀態（降水、溫度）。</param>
    /// <param name="seaLevel">海平面（最後一次 SolveLakes 用的那個）。</param>
    public void ComputeDischarge(SimulationState state, float seaLevel)
    {
        ArgumentNullException.ThrowIfNull(state);
        var n = _grid.CellCount;
        for (var c = 0; c < n; c++)
            _discharge[c] = _flood[c] < seaLevel || _lakeOf[c] >= 0 ? 0f : state.Precipitation[c] * RunoffFractionAt(state, c);
        for (var b = 0; b < _firstCell.Count; b++)
        {
            if (!_holdsWater[b]) continue;
            var outlet = _parent[_firstCell[b]];
            if (outlet >= 0) _discharge[outlet] += (float)_outflow[b];
        }
        // 依泛洪的反順序（上游先），每一格把流量交給下游那一格
        for (var i = _orderCount - 1; i >= 0; i--)
        {
            var c = _order[i];
            if (_flood[c] < seaLevel) continue; // 到海了
            var b = _basinOf[c];
            if (b >= 0 && _holdsWater[b]) continue; // 流進湖裡
            var p = _parent[c];
            if (p >= 0) _discharge[p] += _discharge[c];
        }
        // 流進海與湖的水已經到了，那些格子本身不算河
        for (var c = 0; c < n; c++)
            if (_flood[c] < seaLevel || _lakeOf[c] >= 0) _discharge[c] = 0f;
    }

    /// <summary>
    /// 從上一步的主洋泛洪，算出 <see cref="FloodLevel"/>。地形改了就要重算（每一步）。
    /// </summary>
    /// <param name="surface">每格的地表高度。</param>
    /// <param name="previousSeaLevel">上一步的海平面（用來找主洋的種子）。</param>
    public void Flood(float[] surface, float previousSeaLevel)
    {
        ArgumentNullException.ThrowIfNull(surface);
        var n = _grid.CellCount;

        // 種子：地表低於上一步海平面的最大連通塊。沒有任何水的話，用全球最低的一格
        Array.Fill(_label, -1);
        var stack = new Stack<int>();
        int bestLabel = -1, bestSize = 0, labels = 0;
        for (var start = 0; start < n; start++)
        {
            if (_label[start] >= 0 || surface[start] >= previousSeaLevel) continue;
            var size = 0;
            _label[start] = labels;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                size++;
                var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
                for (var k = 0; k < SimulationGrid.MaxNeighborsPerCell; k++)
                {
                    var nb = _grid.Neighbors[baseIndex + k];
                    if (nb == SimulationGrid.NoNeighbor || _label[nb] >= 0 || surface[nb] >= previousSeaLevel) continue;
                    _label[nb] = labels;
                    stack.Push(nb);
                }
            }
            if (size > bestSize) { bestSize = size; bestLabel = labels; }
            labels++;
        }

        Array.Fill(_flood, float.MaxValue);
        Array.Fill(_parent, -1);
        var queue = new PriorityQueue<int, float>();
        var queued = new bool[n];
        _orderVersion++;
        _orderCount = 0;
        if (bestLabel >= 0)
        {
            // 主洋的種子：泛洪高度就是自己的地表（都低於上一步海平面，比其他任何格子的泛洪高度都低），
            // 直接排序放在處理順序的最前面；只有靠岸（旁邊有不是種子的格子）的才放進佇列往外擴。
            // 原本 15 萬格種子全部進優先佇列，泛洪每步 90 ms
            var seeds = new int[bestSize];
            var keys = new float[bestSize];
            var m = 0;
            for (var c = 0; c < n; c++)
            {
                if (_label[c] != bestLabel) continue;
                seeds[m] = c;
                keys[m] = surface[c];
                m++;
                _flood[c] = surface[c];
                queued[c] = true;
            }
            SortByKey(keys, seeds, m);
            foreach (var c in seeds)
            {
                _order[_orderCount++] = c;
                var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
                for (var k = 0; k < SimulationGrid.MaxNeighborsPerCell; k++)
                {
                    var nb = _grid.Neighbors[baseIndex + k];
                    if (nb == SimulationGrid.NoNeighbor || _label[nb] == bestLabel) continue;
                    queue.Enqueue(c, surface[c]);
                    break;
                }
            }
        }
        else
        {
            var lowest = 0;
            for (var c = 1; c < n; c++) if (surface[c] < surface[lowest]) lowest = c;
            _flood[lowest] = surface[lowest];
            queued[lowest] = true;
            _order[_orderCount++] = lowest;
            queue.Enqueue(lowest, surface[lowest]);
        }
        LastSeedCells = bestSize;

        // priority-flood：依泛洪高度由低到高處理，每一格第一次被碰到時的高度就是最低的（之後碰到它的鄰居只會更高）
        while (queue.TryDequeue(out var c, out var level))
        {
            if (_parent[c] >= 0) _order[_orderCount++] = c; // 種子已經放進處理順序了
            var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
            for (var k = 0; k < SimulationGrid.MaxNeighborsPerCell; k++)
            {
                var nb = _grid.Neighbors[baseIndex + k];
                if (nb == SimulationGrid.NoNeighbor || queued[nb]) continue;
                queued[nb] = true;
                _flood[nb] = MathF.Max(level, surface[nb]);
                _parent[nb] = c;
                queue.Enqueue(nb, _flood[nb]);
            }
        }
    }

    /// <summary>
    /// 依 keys 由小到大排 items（基數排序，穩定：同樣的高度照原本的順序，也就是格子編號），前 count 個。
    /// 15 萬格種子用 Array.Sort 要 15 ms 以上，這是泛洪裡最貴的一段。
    /// </summary>
    private void SortByKey(float[] keys, int[] items, int count)
    {
        if (_sortBits.Length < count)
        {
            _sortBits = new uint[count];
            _sortBitsTmp = new uint[count];
            _sortItemsTmp = new int[count];
        }
        for (var i = 0; i < count; i++)
        {
            // float 的位元轉成可以直接比大小的無號整數：正數翻符號位、負數全翻
            var bits = BitConverter.SingleToUInt32Bits(keys[i]);
            _sortBits[i] = (bits & 0x8000_0000u) != 0 ? ~bits : bits | 0x8000_0000u;
        }
        var counts = new int[1 << 16];
        foreach (var shift in new[] { 0, 16 })
        {
            Array.Clear(counts);
            for (var i = 0; i < count; i++) counts[(int)((_sortBits[i] >> shift) & 0xFFFF)]++;
            var sum = 0;
            for (var d = 0; d < counts.Length; d++) { var c = counts[d]; counts[d] = sum; sum += c; }
            for (var i = 0; i < count; i++)
            {
                var slot = counts[(int)((_sortBits[i] >> shift) & 0xFFFF)]++;
                _sortBitsTmp[slot] = _sortBits[i];
                _sortItemsTmp[slot] = items[i];
            }
            (_sortBits, _sortBitsTmp) = (_sortBitsTmp, _sortBits);
            Array.Copy(_sortItemsTmp, items, count);
        }
    }

    private uint[] _sortBits = [];
    private uint[] _sortBitsTmp = [];
    private int[] _sortItemsTmp = [];

    /// <summary>
    /// 海水體積剛好是 oceanVolume 的海平面（公尺）。要先 <see cref="Flood"/>。
    ///
    /// 不用二分搜尋：泛洪的處理順序就是泛洪高度由低到高，海平面是 L 時海就是「泛洪高度 &lt; L」的前 k 格，
    /// 體積 ＝ k·L − 這 k 格地表高度的總和，對 L 是分段線性的。前綴和算一次，之後每次求解只要在 k 上二分。
    /// 原本照 v1 每次掃全球 60 輪二分，湖與海平面交替幾輪下來注水每步要 150 ms。
    /// </summary>
    /// <param name="surface">每格的地表高度（跟泛洪用的同一份）。</param>
    /// <param name="oceanVolume">海水體積（立方公尺）。</param>
    public float SolveSeaLevel(float[] surface, double oceanVolume)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (_prefixFor != _orderVersion)
        {
            double sum = 0;
            for (var i = 0; i < _orderCount; i++)
            {
                sum += surface[_order[i]];
                _surfacePrefix[i + 1] = sum;
            }
            _prefixFor = _orderVersion;
        }
        var depthSum = oceanVolume / _grid.AverageCellAreaSquareMeters; // Σ(L − s) 要等於這個
        if (!(depthSum >= 0)) throw new InvalidOperationException($"海水體積不合理（{oceanVolume} 立方公尺）。");
        // f(k) ＝ 前 k 格淹到第 k 格（從 0 數）的泛洪高度時的體積，對 k 單調不減；找最小的 k 使 f(k) ≥ depthSum，
        // 海平面就落在第 k−1 格與第 k 格的泛洪高度之間，L ＝ (depthSum + 前 k 格地表總和) ÷ k
        double F(int k) => k >= _orderCount ? double.MaxValue : (double)_flood[_order[k]] * k - _surfacePrefix[k];
        int lo = 1, hi = _orderCount;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (F(mid) >= depthSum) hi = mid;
            else lo = mid + 1;
        }
        var seaLevel = (depthSum + _surfacePrefix[lo]) / lo;
        // 海平面剛越過一個盆地的溢出口時，整個盆地一下子灌進來，體積是跳的；水量剛好落在跳躍裡的話，
        // 海平面停在溢出口（跟二分搜尋的結果一樣），差的那一點水不在海裡（極少發生，見 LastSeaLevelShortfall）
        var sill = lo > 0 ? _flood[_order[lo - 1]] : float.MinValue;
        LastSeaLevelShortfall = 0;
        if (seaLevel < sill)
        {
            LastSeaLevelShortfall = depthSum - ((double)sill * (lo - 1) - _surfacePrefix[lo - 1]);
            seaLevel = sill;
        }
        return (float)seaLevel;
    }

    /// <summary>
    /// 上一次 <see cref="SolveSeaLevel"/>：水量剛好落在「海平面越過盆地溢出口」的體積跳躍裡時，
    /// 海平面停在溢出口，這是沒放進海裡的水（每格平均深度 × 格數，公尺·格）。平常是 0。
    /// </summary>
    public double LastSeaLevelShortfall { get; private set; }

    /// <summary>
    /// 海平面是 seaLevel 時，各封閉盆地的湖水（見類別說明），回傳湖水總體積（立方公尺）。
    /// 要先 <see cref="Flood"/>。只算不寫：湖面高度記在內部，<see cref="LakeDepthAt"/> 讀得到。
    /// </summary>
    /// <param name="state">模擬狀態（降水、溫度）。</param>
    /// <param name="surface">每格的地表高度（跟泛洪用的同一份）。</param>
    /// <param name="seaLevel">海平面。溢出口在海面下的盆地已經是海的一部分，不算。</param>
    public double SolveLakes(SimulationState state, float[] surface, float seaLevel)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(surface);
        if (_basinsFor != _orderVersion) BuildBasins(state, surface);

        _basins.Clear();
        foreach (var c in _lakeCellsWritten) _lakeOf[c] = -1;
        _lakeCellsWritten.Clear();

        var count = _spill.Count;
        var minCells = (int)Math.Ceiling(MinBasinAreaSquareKm * 1_000_000.0 / _grid.AverageCellAreaSquareMeters);
        var cellArea = _grid.AverageCellAreaSquareMeters;
        var inflow = new double[count];
        for (var b = 0; b < count; b++) inflow[b] = _runoff[b];
        if (_holdsWater.Length < count) { _holdsWater = new bool[count]; _outflow = new double[count]; }
        var results = new LakeBasin[count];
        double totalVolume = 0, toOcean = 0;

        // 上游先算（編號大的在上游），溢出來的水加到下游。溢出口在海面下的盆地已經是海，流進去的水等於流進海裡
        for (var b = count - 1; b >= 0; b--)
        {
            var spill = _spill[b];
            var from = _cellStart[b];
            var cells = _cellStart[b + 1] - from;
            if (spill < seaLevel)
            {
                _holdsWater[b] = false;
                _outflow[b] = inflow[b];
                toOcean += inflow[b];
                results[b] = new LakeBasin(LakeKind.Fresh, cells, 0, spill, seaLevel, 0, inflow[b], inflow[b]);
                continue;
            }
            double outflow;
            float level;
            int lakeCells;
            double volume = 0;
            LakeKind kind;

            var depth = spill - surface[_sortedCells[from]];
            if (cells < minCells || depth < MinBasinDepthMeters)
            {
                // 小坑與淺窪地不存水，水直接往下游流（見 MinBasinDepthMeters）
                outflow = inflow[b];
                level = spill;
                lakeCells = 0;
                kind = LakeKind.Fresh;
            }
            else
            {
                // 一格一格由低到高淹：淹進來的那一格從逕流 P×r 變成湖面 P − E，淨損失 E − P(1−r)（建結構時算好了）
                var remaining = inflow[b];
                lakeCells = 0;
                // 要嚴格有剩才淹得進去（或那一格本身就是淨入水）：沒有任何降水時蒸發也是 0，用「≤」的話每一格淨損失 0 ≤ 剩餘 0，
                // 整個盆地會被憑空灌滿（起始到第一次解氣候之前就是這樣）
                while (lakeCells < cells && (_loss[from + lakeCells] < remaining || _loss[from + lakeCells] < 0))
                {
                    remaining -= _loss[from + lakeCells];
                    lakeCells++;
                }
                if (lakeCells == cells)
                {
                    level = spill;
                    outflow = remaining;
                    kind = LakeKind.Fresh;
                }
                else
                {
                    level = surface[_sortedCells[from + lakeCells]];
                    outflow = 0;
                    kind = lakeCells == 0 ? LakeKind.Dry : LakeKind.Saline;
                }
                for (var i = 0; i < lakeCells; i++)
                {
                    var c = _sortedCells[from + i];
                    var waterDepth = level - surface[c];
                    if (waterDepth <= 0f) continue;
                    _lakeOf[c] = b;
                    _lakeLevel[c] = level;
                    _lakeCellsWritten.Add(c);
                    volume += waterDepth;
                }
                volume *= cellArea;
            }

            _holdsWater[b] = !(cells < minCells || depth < MinBasinDepthMeters);
            _outflow[b] = outflow;
            var downstream = _downstream[b];
            if (downstream >= 0) inflow[downstream] += outflow;
            else toOcean += outflow;
            totalVolume += volume;
            results[b] = new LakeBasin(kind, cells, lakeCells, spill, level, volume, inflow[b], outflow);
        }
        _basins.AddRange(results);
        LastLakeVolume = totalVolume;
        LastOverflowToOcean = toOcean;
        return totalVolume;
    }

    /// <summary>
    /// 建盆地的結構（每次泛洪之後一次，跟海平面無關；海平面只決定哪些盆地已經被海淹掉，見 <see cref="SolveLakes"/>）：
    /// 窪地（泛洪高度 &gt; 地表）的連通塊、各自由低到高排好的格子、每一格淹進湖裡的淨損失、溢出去流到哪個盆地、集水區的逕流。
    /// 原本每次 <see cref="SolveLakes"/> 都重建，湖與海平面交替幾輪下來注水每步要 120 ms。
    /// </summary>
    private void BuildBasins(SimulationState state, float[] surface)
    {
        var n = _grid.CellCount;
        Array.Fill(_basinOf, -1);
        _spill.Clear();
        _downstream.Clear();
        _runoff.Clear();
        _cellStartList.Clear();
        var sorted = new List<int>();
        var first = _firstCell;
        first.Clear();
        var stack = new Stack<int>();

        // 窪地的連通塊，依泛洪先後編號：下游（溢出口低的）在前
        for (var i = 0; i < _orderCount; i++)
        {
            var start = _order[i];
            if (_basinOf[start] >= 0 || !(_flood[start] > surface[start])) continue;
            var id = _spill.Count;
            var begin = sorted.Count;
            _basinOf[start] = id;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                sorted.Add(c);
                var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
                for (var k = 0; k < SimulationGrid.MaxNeighborsPerCell; k++)
                {
                    var nb = _grid.Neighbors[baseIndex + k];
                    if (nb == SimulationGrid.NoNeighbor || _basinOf[nb] >= 0 || !(_flood[nb] > surface[nb])) continue;
                    _basinOf[nb] = id;
                    stack.Push(nb);
                }
            }
            sorted.Sort(begin, sorted.Count - begin, Comparer<int>.Create((x, y) =>
                surface[x] != surface[y] ? surface[x].CompareTo(surface[y]) : x.CompareTo(y)));
            _cellStartList.Add(begin);
            _spill.Add(_flood[start]);
            first.Add(start);
        }
        _cellStartList.Add(sorted.Count);
        _cellStart = _cellStartList.ToArray();
        _sortedCells = sorted.ToArray();

        // 每一格的水流到下游第一個窪地（−1 ＝ 主洋的種子）：依泛洪先後處理，上游那一格的下游一定已經算過
        for (var i = 0; i < _orderCount; i++)
        {
            var c = _order[i];
            _drain[c] = _basinOf[c] >= 0 ? _basinOf[c] : _parent[c] < 0 ? -1 : _drain[_parent[c]];
        }
        for (var b = 0; b < first.Count; b++)
        {
            var parent = _parent[first[b]];
            _downstream.Add(parent < 0 ? -1 : _drain[parent]);
            _runoff.Add(0);
        }

        // 集水區的逕流 P×r（降水是相對單位，見類別說明）
        double meanPrecipitation = 0;
        for (var c = 0; c < n; c++) meanPrecipitation += state.Precipitation[c];
        meanPrecipitation /= n;
        for (var c = 0; c < n; c++)
            if (_drain[c] >= 0) _runoff[_drain[c]] += state.Precipitation[c] * RunoffFractionAt(state, c);

        // 淹進湖裡的淨損失：湖面蒸發減掉原本會蒸散掉的那部分降水
        _loss = new double[_sortedCells.Length];
        for (var i = 0; i < _sortedCells.Length; i++)
        {
            var c = _sortedCells[i];
            _loss[i] = EvaporationAt(state, c, meanPrecipitation) - state.Precipitation[c] * (1f - RunoffFractionAt(state, c));
        }
        _basinsFor = _orderVersion;
    }

    /// <summary>這一格的湖水深度（公尺），沒有湖水是 0。用上一次 <see cref="SolveLakes"/> 的結果。</summary>
    /// <param name="cell">格子。</param>
    /// <param name="surface">地表高度。</param>
    public float LakeDepthAt(int cell, float surface) => _lakeOf[cell] >= 0 ? MathF.Max(0f, _lakeLevel[cell] - surface) : 0f;

    /// <summary>
    /// 這一格的降水有多少變成逕流（2026-10-18 使用者指定：淡水要算高山融雪的水）。
    /// 雨的部分照 <see cref="Climate.RunoffFraction"/>（其餘蒸散掉）；雪的部分存到融雪季才流下來，蒸散損失很少，
    /// 用 <see cref="SnowmeltRunoffFraction"/>。降雪的比例見 <see cref="Climate.SnowFraction"/>。冰蓋往平衡厚度鬆弛、沒有記融化量，冰河本身的消融不另外算。
    /// </summary>
    internal static float RunoffFractionAtCell(SimulationState state, int cell) => RunoffFractionAt(state, cell);

    /// <inheritdoc cref="RunoffFractionAtCell"/>
    private static float RunoffFractionAt(SimulationState state, int cell)
    {
        var snow = Climate.SnowFraction(state.Temperature[cell]);
        return Climate.RunoffFraction + (SnowmeltRunoffFraction - Climate.RunoffFraction) * snow;
    }

    /// <summary>融雪的逕流係數。以融雪為主的高山集水區（阿爾卑斯、天山）年逕流係數約 0.6~0.9。</summary>
    internal const float SnowmeltRunoffFraction = 0.8f;

    /// <summary>湖面蒸發（降水的相對單位）：15°C 時是全球平均降水的 <see cref="EvaporationAt15CPerMeanPrecipitation"/> 倍，隨溫度指數變化。</summary>
    private float EvaporationAt(SimulationState state, int cell, double meanPrecipitation)
    {
        var factor = Math.Clamp(MathF.Exp((state.Temperature[cell] - 15f) / EvaporationEFoldingC), MinEvaporationFactor, MaxEvaporationFactor);
        return (float)(meanPrecipitation * EvaporationAt15CPerMeanPrecipitation * factor);
    }
}
