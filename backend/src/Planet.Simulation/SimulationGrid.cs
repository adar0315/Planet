using Planet.Domain;
using Planet.Generation;

namespace Planet.Simulation;

/// <summary>
/// 地質模擬用的**粗網格**：拓樸結構跟正式的2,000萬格星球一樣（Goldberg 多面體），
/// 但細分頻率低很多（見 <see cref="DefaultFrequency"/>），格數控制在數十萬。
///
/// 為什麼不直接在2,000萬格上模擬：逐格算水流、侵蝕、板塊，每步都是幾十GB的讀寫，
/// 即時模擬完全不可能。板塊尺度的作用本來也不需要1公里的解析度。
/// 詳見 docs/geological-simulation-plan.md「決策3」與「階段0實測結果」。
///
/// 這個型別只負責**不會變的東西**（位置、鄰接關係、面積），模擬過程中會變的狀態在
/// <see cref="SimulationState"/>。兩者分開是因為前者建一次就固定，後者每步都在改。
/// </summary>
public sealed class SimulationGrid
{
    /// <summary>
    /// 預設細分頻率，格數 = 10n²+2 = 225,002。
    ///
    /// 階段0實測（8邏輯核心）：單個 stencil pass 平行化後約 1.6ms，假設一步8個pass約12.6ms、
    /// 79步/秒，而觀看模擬只需要2~10步/秒，餘裕約10~40倍。n=200（40萬格）也跑得動但餘裕少一半，
    /// n=100（10萬格）解析度偏低。詳見 docs/geological-simulation-plan.md。
    /// </summary>
    public const int DefaultFrequency = 150;

    /// <summary>每格最多幾個鄰居。六角形6個、五角形5個（第6個位置放 <see cref="NoNeighbor"/>）。</summary>
    public const int MaxNeighborsPerCell = 6;

    /// <summary>鄰接表中代表「沒有這個鄰居」的值（只會出現在12個五角形的第6個位置）。</summary>
    public const int NoNeighbor = -1;

    /// <summary>細分頻率 n。</summary>
    public int Frequency { get; }

    /// <summary>格數 = 10n²+2。</summary>
    public int CellCount { get; }

    /// <summary>星球半徑（公尺）。</summary>
    public double PlanetRadiusMeters { get; }

    /// <summary>每格中心的緯度（度）。</summary>
    public double[] Latitude { get; }

    /// <summary>每格中心的經度（度）。</summary>
    public double[] Longitude { get; }

    /// <summary>每格中心在單位球面上的座標（x,y,z 連續存放，長度 = CellCount*3）。做最近格查詢時用弦長比較，比經緯度距離公式快也不會有極區問題。</summary>
    public double[] UnitVector { get; }

    /// <summary>
    /// 攤平的鄰接表，長度 = CellCount * <see cref="MaxNeighborsPerCell"/>。
    /// 第 i 格的第 s 個鄰居 = Neighbors[i * MaxNeighborsPerCell + s]，值為 <see cref="NoNeighbor"/> 代表沒有。
    ///
    /// **刻意攤平成一維陣列而不是每步呼叫 <see cref="ITileNeighborResolver"/>**：那支是為了「查一格」
    /// 設計的，每次呼叫都會配置 Dictionary 與 List；模擬每步要走訪幾十萬次鄰居，那樣會被配置成本淹沒。
    /// 建表本身只做一次（22.5萬格實測約0.5秒）。
    /// </summary>
    public int[] Neighbors { get; }

    /// <summary>每格是不是五角形（整顆星球剛好12個，位於原正二十面體的頂點，含南北極）。</summary>
    public bool[] IsPentagon { get; }

    /// <summary>
    /// 每格的平均面積（平方公尺）——**用球面總面積平均分配，不是逐格精算**。
    ///
    /// 測地線細分後各格面積並不完全相同（面中心與頂點附近有差異，五角形又比六角形小），
    /// 真正逐格算需要construct對偶多邊形的頂點。目前的作用（顯示、粗略的體積換算）用平均值就夠；
    /// 等到要做嚴格的質量守恆（沉積物搬運總量必須守恆）時再回來補逐格面積，
    /// 屆時要注意五角形會偏小，用平均值會讓那12格的物質量算多。
    /// </summary>
    public double AverageCellAreaSquareMeters { get; }

    /// <summary>
    /// 相鄰格子的平均中心間距（公尺）：由平均面積當正六邊形反推邊長，再乘 √3。
    ///
    /// **凡是「幾格」的參數都要換算成物理距離再用這個換回來**——模擬跑在 n=150（間距約15公里），
    /// 測試多半跑在 n=24（約93公里），差6倍。寫死格數的參數在兩邊代表完全不同的距離，
    /// 症狀是粗網格的測試全過、正式解析度卻壞掉（見 CLAUDE.md）。
    /// </summary>
    public double AverageCellSpacingMeters =>
        Math.Sqrt(AverageCellAreaSquareMeters / (3.0 * Math.Sqrt(3.0) / 2.0)) * Math.Sqrt(3.0);

    private SimulationGrid(
        int frequency,
        double planetRadiusMeters,
        double[] latitude,
        double[] longitude,
        double[] unitVector,
        int[] neighbors,
        bool[] isPentagon)
    {
        Frequency = frequency;
        CellCount = latitude.Length;
        PlanetRadiusMeters = planetRadiusMeters;
        Latitude = latitude;
        Longitude = longitude;
        UnitVector = unitVector;
        Neighbors = neighbors;
        IsPentagon = isPentagon;
        AverageCellAreaSquareMeters = 4.0 * Math.PI * planetRadiusMeters * planetRadiusMeters / CellCount;
    }

    /// <summary>
    /// 建立粗網格。拓樸直接沿用 <see cref="GoldbergPlanetGenerator"/>——那支的
    /// (FaceIndex,Row,Col) 配發規則跟 <see cref="TileNeighborResolver"/> 的正規化規則是**刻意對齊**的
    /// （都取面索引最小的表示），自己另外寫一份格點生成必然會跟鄰居查詢對不起來。
    ///
    /// **星球半徑要另外傳進來，不能用生成器算的那個。** 生成器的 <c>PlanetRadiusMeters</c> 是
    /// 「六角形邊長固定1公里」反推的，在粗頻率下會得到一顆很小的星球（n=150 只有196公里半徑）。
    /// 模擬要代表的是**同一顆**星球（半徑約1,852公里），所以半徑由呼叫端從
    /// <c>PlanetMetadata</c> 取得後傳入。緯經度本身跟半徑無關（是從單位球面點算的），可以直接用。
    /// </summary>
    /// <param name="planetRadiusMeters">星球實際半徑（公尺），權威來源是 <c>PlanetMetadata</c> 資料表。</param>
    /// <param name="frequency">細分頻率，預設 <see cref="DefaultFrequency"/>。</param>
    public static SimulationGrid Build(double planetRadiusMeters, int frequency = DefaultFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frequency, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(planetRadiusMeters);

        // 生成器順便會跑地形noise取樣，這裡用不到（模擬的地形是自己長出來的，不是noise給的），
        // 但為了讓拓樸與座標配發規則只有一份，還是走同一支而不是另外寫一個精簡版。
        // 一次性成本，n=150 實測約0.3秒。
        var generator = new GoldbergPlanetGenerator(frequency);
        var tiles = generator.GenerateTiles().ToArray();

        var count = tiles.Length;
        var latitude = new double[count];
        var longitude = new double[count];
        var unitVector = new double[count * 3];
        var isPentagon = new bool[count];
        var indexByCoord = new Dictionary<(int Face, int Row, int Col), int>(count);

        for (var i = 0; i < count; i++)
        {
            var tile = tiles[i];
            latitude[i] = tile.Latitude;
            longitude[i] = tile.Longitude;
            isPentagon[i] = tile.Shape == TileShape.Pentagon;
            indexByCoord[(tile.FaceIndex, tile.Row, tile.Col)] = i;

            var latRad = tile.Latitude * Math.PI / 180.0;
            var lonRad = tile.Longitude * Math.PI / 180.0;
            var cosLat = Math.Cos(latRad);
            unitVector[i * 3] = cosLat * Math.Cos(lonRad);
            unitVector[i * 3 + 1] = cosLat * Math.Sin(lonRad);
            unitVector[i * 3 + 2] = Math.Sin(latRad);
        }

        var resolver = new TileNeighborResolver(frequency);
        var neighbors = new int[count * MaxNeighborsPerCell];
        for (var i = 0; i < count; i++)
        {
            var coords = resolver.GetNeighborCoordinates(tiles[i]);
            for (var s = 0; s < MaxNeighborsPerCell; s++)
            {
                neighbors[i * MaxNeighborsPerCell + s] =
                    s < coords.Count && indexByCoord.TryGetValue(coords[s], out var neighborIndex)
                        ? neighborIndex
                        : NoNeighbor;
            }
        }

        return new SimulationGrid(frequency, planetRadiusMeters, latitude, longitude, unitVector, neighbors, isPentagon);
    }

    // ---- 最近格查詢 ----
    // 用經緯度桶加速。板塊平移（把物質搬到新位置）與貼圖取樣都需要「這個方向上最近的是哪一格」，
    // 所以放在網格上共用一份，不要各自實作一套 bucket（同一套邏輯存兩份必然漂移）。
    private int _latBuckets;
    private int _lonBuckets;
    private int[]? _bucketStart;   // CSR 格式：第 b 個桶的內容是 _bucketCells[_bucketStart[b].._bucketStart[b+1]]
    private int[]? _bucketCells;

    /// <summary>
    /// 找出單位向量方向上最接近的格子。
    ///
    /// **極區要特別處理**：靠近極點時同樣的經度範圍代表的實際距離會縮小 cos(緯度) 倍，
    /// 固定搜尋 ±N 個經度桶在極區會涵蓋太小的實際範圍而漏掉真正最近的格子，
    /// 所以經度方向的搜尋半徑要除以 cos(緯度)（極點附近直接掃整圈）。
    /// 有測試拿這支的結果跟暴力法逐格比對，抽樣涵蓋兩極與換日線。
    /// </summary>
    /// <param name="x">單位向量X。</param>
    /// <param name="y">單位向量Y。</param>
    /// <param name="z">單位向量Z。</param>
    public int FindNearestCell(double x, double y, double z)
    {
        EnsureBuckets();

        var latitude = Math.Asin(Math.Clamp(z, -1.0, 1.0)) * 180.0 / Math.PI;
        var longitude = Math.Atan2(y, x) * 180.0 / Math.PI;
        var cosLat = Math.Cos(latitude * Math.PI / 180.0);

        var lonSpan = cosLat > 0.02 ? (int)Math.Ceiling(2.0 / cosLat) : _lonBuckets;
        lonSpan = Math.Min(lonSpan, _lonBuckets);

        var (bLat, bLon) = BucketOf(latitude, longitude, _latBuckets, _lonBuckets);

        var best = 0;
        var bestDot = double.NegativeInfinity;

        for (var dLat = -2; dLat <= 2; dLat++)
        {
            var latIndex = bLat + dLat;
            if (latIndex < 0 || latIndex >= _latBuckets) continue;

            for (var dLon = -lonSpan; dLon <= lonSpan; dLon++)
            {
                var lonIndex = ((bLon + dLon) % _lonBuckets + _lonBuckets) % _lonBuckets;
                var bucket = latIndex * _lonBuckets + lonIndex;
                for (var k = _bucketStart![bucket]; k < _bucketStart[bucket + 1]; k++)
                {
                    var cell = _bucketCells![k];
                    var dot = x * UnitVector[cell * 3] + y * UnitVector[cell * 3 + 1] + z * UnitVector[cell * 3 + 2];
                    if (dot > bestDot)
                    {
                        bestDot = dot;
                        best = cell;
                    }
                }
            }
        }

        return best;
    }

    private void EnsureBuckets()
    {
        if (_bucketStart is not null) return;
        lock (this)
        {
            if (_bucketStart is not null) return;

            // 桶大小抓成跟格距同一個量級（緯度方向每桶約 180/n 度 ＝ 一格的間距）
            var latBuckets = Math.Max(8, Frequency);
            var lonBuckets = Math.Max(16, Frequency * 2);
            var bucketCount = latBuckets * lonBuckets;

            // CSR：先數每桶幾個、算前綴和、再填。比 List<int>[] 省記憶體也對快取友善。
            var counts = new int[bucketCount + 1];
            var bucketOf = new int[CellCount];
            for (var cell = 0; cell < CellCount; cell++)
            {
                var (bLat, bLon) = BucketOf(Latitude[cell], Longitude[cell], latBuckets, lonBuckets);
                var bucket = bLat * lonBuckets + bLon;
                bucketOf[cell] = bucket;
                counts[bucket + 1]++;
            }
            for (var b = 0; b < bucketCount; b++) counts[b + 1] += counts[b];

            var cells = new int[CellCount];
            var cursor = (int[])counts.Clone();
            for (var cell = 0; cell < CellCount; cell++) cells[cursor[bucketOf[cell]]++] = cell;

            _latBuckets = latBuckets;
            _lonBuckets = lonBuckets;
            _bucketCells = cells;
            _bucketStart = counts; // 最後才指派：其他執行緒靠這個判斷有沒有建好
        }
    }

    private static (int Lat, int Lon) BucketOf(double latitude, double longitude, int latBuckets, int lonBuckets)
    {
        var latIndex = (int)((90.0 - latitude) / 180.0 * latBuckets);
        var lonIndex = (int)((longitude + 180.0) / 360.0 * lonBuckets);
        return (Math.Clamp(latIndex, 0, latBuckets - 1), Math.Clamp(lonIndex, 0, lonBuckets - 1));
    }

    /// <summary>取得第 <paramref name="cellIndex"/> 格的鄰居索引（六角形6個、五角形5個，不含 <see cref="NoNeighbor"/>）。</summary>
    /// <param name="cellIndex">格子索引。</param>
    /// <param name="buffer">結果寫入這裡，長度至少要 <see cref="MaxNeighborsPerCell"/>。</param>
    /// <returns>實際鄰居數量。</returns>
    public int GetNeighbors(int cellIndex, Span<int> buffer)
    {
        var written = 0;
        var baseIndex = cellIndex * MaxNeighborsPerCell;
        for (var s = 0; s < MaxNeighborsPerCell; s++)
        {
            var neighbor = Neighbors[baseIndex + s];
            if (neighbor != NoNeighbor) buffer[written++] = neighbor;
        }
        return written;
    }
}
