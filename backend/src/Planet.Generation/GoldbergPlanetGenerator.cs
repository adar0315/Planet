using Planet.Domain;

namespace Planet.Generation;

/// <summary>
/// 以正二十面體做測地線細分（Class I geodesic subdivision）產生 Goldberg 多面體：
/// 12個五角形（原二十面體頂點，含南北極各一個）＋ 10n²-10 個六角形。
///
/// 星球規模取捨：地球大小＋1公里邊長會產生約1.96億格，資料量過大，
/// 因此改為固定總格數（頻率 n，見 SubdivisionFrequency）並反推星球半徑，
/// 讓六角形邊長維持約1公里（詳見 PlanetRadiusMeters 的計算方式）。
/// </summary>
public class GoldbergPlanetGenerator : IPlanetGenerator
{
    /// <summary>細分頻率。總格數 = 10n²+2（其中12個為五角形）。1415 → 約2,002萬格。
    /// public是因為重新生成星球的功能（Planet.Service專案）需要用這個值配合新的地形參數重建生成器。</summary>
    public const int SubdivisionFrequency = 1415;

    /// <summary>
    /// 目標六角形邊長（公尺）。星球半徑就是由「這個邊長 + 細分頻率」反推出來的。
    /// public 是因為前端算Tile在場景中的大小時需要同一個值——原本前端是自己寫死1000，
    /// 跟這裡各存一份，改了其中一邊另一邊不會知道。
    /// </summary>
    public const double TargetHexagonEdgeLengthMeters = 1_000;

    /// <summary>
    /// 正六邊形「中心到相鄰中心的距離」＝邊長×√3（把六邊形拆成6個以邊長為外接半徑的正三角形即可推出）。
    /// EstimateUnitSphereCenterDistance量到的是相鄰格點「中心距離」，所以反推半徑時要先把目標邊長換算成
    /// 目標中心距離，再除以量到的單位球中心距離，這樣PlanetRadiusMeters才會讓「六邊形本身的邊長」≈1公里，
    /// 而不是誤把「中心距離」當成邊長（這兩者相差約73%，混淆的話星球會小了將近一半）。
    /// </summary>
    private const double CenterDistanceToEdgeLengthRatio = 1.7320508075688772; // sqrt(3)

    /// <summary>實際生成整個星球時使用的地形種子。其他需要跟資料庫地形資料對得上的程式碼
    /// （例如全星球總覽貼圖）要引用這個常數，不要各自寫死42。</summary>
    public const int DefaultTerrainSeed = 42;

    /// <summary>星球目前統一的海平面等級（跟ElevationLevel同單位）。存成每格Tile自己的欄位（見Tile.SeaLevel），
    /// 不是寫死的全域常數，方便之後要做「局部海平面變動」時不用改schema。</summary>
    private const int PlanetSeaLevelLevel = 0;

    /// <summary>依細分頻率與目標邊長反推出的星球半徑（公尺）。</summary>
    public double PlanetRadiusMeters { get; }

    /// <inheritdoc />
    public long TotalTileCount { get; }

    private readonly int _n;
    private readonly ITerrainGenerator _terrainGenerator;

    /// <summary>以正式的細分頻率（<see cref="SubdivisionFrequency"/>，約2,002萬格）與預設地形參數建立生成器。</summary>
    public GoldbergPlanetGenerator() : this(SubdivisionFrequency, TerrainGenerationOptions.Default)
    {
    }

    /// <summary>供測試使用：以較小的細分頻率驗證拓樸正確性，避免正式2000萬格規模拖慢測試。</summary>
    /// <param name="subdivisionFrequency">細分頻率n，總格數＝10n²+2。</param>
    public GoldbergPlanetGenerator(int subdivisionFrequency) : this(subdivisionFrequency, TerrainGenerationOptions.Default)
    {
    }

    /// <summary>供測試使用：以較小的細分頻率＋指定地形種子建立生成器。</summary>
    /// <param name="subdivisionFrequency">細分頻率n，總格數＝10n²+2。</param>
    /// <param name="terrainSeed">地形程序化生成用的亂數種子，同種子必定產生同樣的地形。</param>
    public GoldbergPlanetGenerator(int subdivisionFrequency, int terrainSeed)
        : this(subdivisionFrequency, TerrainGenerationOptions.Default with { Seed = terrainSeed })
    {
    }

    /// <summary>建立星球生成器。</summary>
    /// <param name="subdivisionFrequency">細分頻率n，總格數＝10n²+2（其中12個為五角形）。</param>
    /// <param name="terrainOptions">地形程序化生成的完整參數（種子、大陸形狀、小島數量等）。</param>
    public GoldbergPlanetGenerator(int subdivisionFrequency, TerrainGenerationOptions terrainOptions)
    {
        _n = subdivisionFrequency;
        _terrainGenerator = new TerrainGenerator(terrainOptions);
        TotalTileCount = 10L * _n * _n + 2;
        var unitCenterDistance = EstimateUnitSphereCenterDistance(_n);
        var targetCenterDistanceMeters = TargetHexagonEdgeLengthMeters * CenterDistanceToEdgeLengthRatio;
        PlanetRadiusMeters = targetCenterDistanceMeters / unitCenterDistance;
    }

    /// <summary>
    /// 依序枚舉20個正二十面體面，對每個面做重心座標(i,j,k)細分掃描；邊界格點（面與面共用）用座標量化
    /// 去重確保只產生一次，面內部格點則直接配發新Id。用yield串流產生，呼叫端可以邊生成邊寫入資料庫，
    /// 不需要在記憶體中held住全部2000萬筆。
    /// </summary>
    public IEnumerable<Tile> GenerateTiles()
    {
        var n = _n;
        var vertices = IcosahedronGeometry.Vertices;
        var faces = IcosahedronGeometry.Faces;

        // 只有邊界（面與面共用的邊、頂點）需要去重；面內部的格點必定只屬於該面，可直接配發新Id。
        // 邊界格點數量僅約 30(n-1)+12，遠小於總格數，去重字典可以很小。
        var boundaryTileIds = new Dictionary<(long X, long Y, long Z), long>(capacity: 30 * (n - 1) + 12);
        long nextTileId = 0;

        for (var faceIndex = 0; faceIndex < faces.Length; faceIndex++)
        {
            var (ia, ib, ic) = faces[faceIndex];
            var a = vertices[ia];
            var b = vertices[ib];
            var c = vertices[ic];

            for (var i = 0; i <= n; i++)
            {
                for (var j = 0; j <= n - i; j++)
                {
                    var k = n - i - j;
                    var isBoundary = i == 0 || j == 0 || k == 0;

                    long tileId;
                    bool isNewTile;

                    var point = (a * k + b * i + c * j).Normalized();

                    if (isBoundary)
                    {
                        var key = QuantizeKey(point);
                        if (boundaryTileIds.TryGetValue(key, out var existingId))
                        {
                            tileId = existingId;
                            isNewTile = false;
                        }
                        else
                        {
                            tileId = nextTileId++;
                            boundaryTileIds[key] = tileId;
                            isNewTile = true;
                        }
                    }
                    else
                    {
                        tileId = nextTileId++;
                        isNewTile = true;
                    }

                    if (!isNewTile) continue;

                    var isPentagon = (i == 0 && j == 0) || (i == n && j == 0) || (i == 0 && j == n);
                    var (latitude, longitude) = ToLatLon(point);
                    var terrain = _terrainGenerator.Sample(latitude, longitude);

                    yield return new Tile
                    {
                        TileId = tileId,
                        Shape = isPentagon ? TileShape.Pentagon : TileShape.Hexagon,
                        FaceIndex = faceIndex,
                        Row = i,
                        Col = j,
                        Latitude = latitude,
                        Longitude = longitude,
                        Terrain = terrain.Terrain,
                        HeightCategory = terrain.HeightCategory,
                        ElevationLevel = terrain.ElevationLevel,
                        SeaLevel = PlanetSeaLevelLevel,
                    };
                }
            }
        }
    }

    /// <summary>把單位球面上的3D座標換算成經緯度（度）。</summary>
    private static (double Latitude, double Longitude) ToLatLon(Vec3 unitPoint)
    {
        var latitude = Math.Asin(Math.Clamp(unitPoint.Z, -1.0, 1.0)) * (180.0 / Math.PI);
        var longitude = Math.Atan2(unitPoint.Y, unitPoint.X) * (180.0 / Math.PI);
        return (latitude, longitude);
    }

    /// <summary>將單位球座標量化為整數鍵，供去重字典使用。1e-9解析度遠高於相鄰格點間距（約1/n），足以吸收浮點誤差同時不會誤判不同的點。</summary>
    private static (long, long, long) QuantizeKey(Vec3 point)
    {
        const double scale = 1_000_000_000.0;
        return (
            (long)Math.Round(point.X * scale),
            (long)Math.Round(point.Y * scale),
            (long)Math.Round(point.Z * scale)
        );
    }

    /// <summary>
    /// 在單位球面上取樣一個面中央的相鄰格點對（即相鄰Tile中心），量測其弦長（中心距離，不是六邊形的邊長），
    /// 做為換算實際星球半徑的比例尺。Class I 細分並非完全均勻（面中心與頂點附近的格距略有差異），
    /// 採樣面中央可得到具代表性的平均值。
    /// </summary>
    private static double EstimateUnitSphereCenterDistance(int n)
    {
        var (ia, ib, ic) = IcosahedronGeometry.Faces[0];
        var a = IcosahedronGeometry.Vertices[ia];
        var b = IcosahedronGeometry.Vertices[ib];
        var c = IcosahedronGeometry.Vertices[ic];

        var mid = n / 3;

        Vec3 PointAt(int i, int j)
        {
            var k = n - i - j;
            return (a * k + b * i + c * j).Normalized();
        }

        var p1 = PointAt(mid, mid);
        var p2 = PointAt(mid + 1, mid);
        var p3 = PointAt(mid, mid + 1);

        var edge1 = (p2 - p1).Length;
        var edge2 = (p3 - p1).Length;
        return (edge1 + edge2) / 2.0;
    }
}
