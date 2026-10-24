using Planet.Domain;
using Planet.Generation;

namespace Planet.Simulation;

/// <summary>
/// 從模擬粗格（<see cref="PlanetCell"/>）取樣任意經緯度的地形：放大成 2,000 萬地塊、畫總覽貼圖，都走這裡。
///
/// ## 只有高度內插，其他屬性取最近的粗格（2026-10-17 拍板）
///
/// 一個粗格約 9 公里寬、底下有 89 個地塊。全部取最近粗格的話，地面會是一塊塊平台，
/// 平台之間的落差在走動模式裡會變成走不過去的牆。所以**高度**用三個最近粗格做重心內插把階梯抹平；
/// 地形類型、海陸仍然取最近的粗格——那不是加細節，只是不讓取樣方式自己造出地形。
///
/// 內插後的高度要跟海陸一致：最近粗格是水，高度至少在海平面下一級；是陸地，至少在海平面上。
/// 否則海岸附近會出現「標成陸地卻泡在水裡」的地塊。
///
/// 2026-10-24（水文地貌規劃 H6）起，河道、河谷、三角洲、海岸、風成地形這些比一格小的地貌交給 <see cref="LandformSampler"/>：
/// 這裡只負責內插高度、把公尺換成等級。
/// </summary>
public sealed class SimulatedTerrainSampler : ITerrainGenerator
{
    /// <summary>每個海拔等級代表幾公尺（同 <see cref="Tile.ElevationLevel"/>）。</summary>
    public const double MetersPerElevationLevel = 5.0;

    private readonly SimulationGrid _grid;
    private readonly IReadOnlyList<PlanetCell> _cells;
    private readonly LandformSampler _landforms;

    /// <summary>建立取樣器。</summary>
    /// <param name="grid">模擬網格（格子索引要跟 <paramref name="cells"/> 的 <see cref="PlanetCell.CellId"/> 一致）。</param>
    /// <param name="cells">每一格的記錄，依 <see cref="PlanetCell.CellId"/> 排序、從 0 開始連續。</param>
    public SimulatedTerrainSampler(SimulationGrid grid, IReadOnlyList<PlanetCell> cells)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(cells);
        if (cells.Count != grid.CellCount)
        {
            throw new ArgumentException($"粗格記錄有 {cells.Count} 筆，網格卻有 {grid.CellCount} 格——不是同一顆星球。", nameof(cells));
        }
        for (var i = 0; i < cells.Count; i++)
        {
            if (cells[i].CellId != i) throw new ArgumentException("粗格記錄必須依 CellId 排序、從 0 開始連續。", nameof(cells));
        }

        _grid = grid;
        _cells = cells;
        _landforms = new LandformSampler(grid, cells);
    }

    /// <summary>
    /// 地塊的「半徑」（公尺）：河道中心線離地塊中心這麼近（再加上半個河寬）就算這一格有河，拉遠時整格畫成河水的藍色
    /// （水文地貌規劃 H6）。放大成地塊時是地塊的邊長；近看細分的小六角要設成小格的邊長。
    /// </summary>
    public double TileRadiusMeters { get; init; } = 1_000;

    /// <summary>底下真正畫地貌的取樣器（河道、河谷、三角洲、海岸、風成地形）。</summary>
    public LandformSampler Landforms => _landforms;

    /// <inheritdoc />
    public TerrainSample Sample(double latitude, double longitude)
    {
        var sample = SampleDetailed(latitude, longitude);
        var (level, waterLevel) = Levels(sample);

        // 這一格範圍裡有河：拉遠時整格畫成河水的藍色（H6）
        var hasRiver = sample.RiverCellId >= 0 && sample.RiverDistanceMeters <= TileRadiusMeters + sample.RiverWidthMeters / 2;
        return new TerrainSample(sample.Terrain, sample.HeightCategory, level, sample.SourceCellId, waterLevel,
            hasRiver ? (float)sample.RiverWidthMeters : 0f, hasRiver ? sample.RiverCellId : null);
    }

    /// <summary>每一格的記錄（依格子編號）。近看細分查每月的資料用。</summary>
    public IReadOnlyList<PlanetCell> Cells => _cells;

    /// <summary>取樣某一點的完整地貌（高度已經內插）。地塊與近看細分的小格都走這一支。</summary>
    /// <param name="latitude">緯度（度）。</param>
    /// <param name="longitude">經度（度）。</param>
    public LandformSample SampleDetailed(double latitude, double longitude)
    {
        var lat = latitude * Math.PI / 180.0;
        var lon = longitude * Math.PI / 180.0;
        var x = Math.Cos(lat) * Math.Cos(lon);
        var y = Math.Cos(lat) * Math.Sin(lon);
        var z = Math.Sin(lat);

        // 1. 位置用雜訊扭一下再找粗格：粗格之間的分界（地形類型、海岸線）才不是 15 公里的六角形（水文地貌規劃 H6）
        var (wx, wy, wz) = Warp(x, y, z);
        var nearest = _grid.FindNearestCell(wx, wy, wz);

        // 2. 高度：最近粗格與鄰居的平滑核加權（跨過 Voronoi 分界時是連續的，不會有階梯）
        Span<int> neighbors = stackalloc int[SimulationGrid.MaxNeighborsPerCell];
        var count = _grid.GetNeighbors(nearest, neighbors);
        double weightSum = 0, elevationSum = 0, oceanWeight = 0, landWeight = 0;
        double low = double.MaxValue, high = double.MinValue;
        int bestOcean = -1, bestLand = -1;
        double bestOceanWeight = 0, bestLandWeight = 0;
        for (var i = -1; i < count; i++)
        {
            var k = i < 0 ? nearest : neighbors[i];
            var w = KernelWeight(k, wx, wy, wz);
            if (i < 0) w = Math.Max(w, 1e-6); // 最近的那一格一定算進去
            if (w <= 0) continue;
            var cell = _cells[k];
            weightSum += w;
            elevationSum += w * cell.SurfaceElevationMeters;
            if (cell.WaterDepthMeters > 0f && cell.IsOcean)
            {
                oceanWeight += w;
                if (w > bestOceanWeight) { bestOceanWeight = w; bestOcean = k; }
            }
            else if (cell.WaterDepthMeters <= 0f)
            {
                landWeight += w;
                if (w > bestLandWeight) { bestLandWeight = w; bestLand = k; }
                low = Math.Min(low, cell.SurfaceElevationMeters);
                high = Math.Max(high, cell.SurfaceElevationMeters);
            }
        }
        var elevation = elevationSum / weightSum;

        // 3. 海陸：湖照最近的粗格；海岸附近看平滑後的高度——海岸線跟著地形的等高線走，不是格子的邊
        var chosen = nearest;
        var nearestCell = _cells[nearest];
        var isLake = nearestCell.WaterDepthMeters > 0f && !nearestCell.IsOcean;
        double? coastProximity = null;
        if (!isLake && oceanWeight > 0 && landWeight > 0)
        {
            coastProximity = oceanWeight / weightSum;
            var sea = elevation < 0;
            chosen = sea ? bestOcean : bestLand;
        }
        else if (!isLake && oceanWeight > 0)
        {
            coastProximity = 1;
        }

        // 4. 陸地加上細部起伏：振幅看附近粗格的起伏（平原幾公尺、山區上百公尺），波長幾公里
        if (_cells[chosen].WaterDepthMeters <= 0f && landWeight > 0)
        {
            var relief = high - low;
            var amplitude = DetailReliefShare * relief + DetailBaseMeters;
            var f = _grid.PlanetRadiusMeters / DetailWavelengthMeters;
            elevation = Math.Max(elevation, 0) + amplitude * _detailNoise.Fbm(x * f, y * f, z * f, 4, 1.0);
            elevation = Math.Max(elevation, 0);
        }
        return _landforms.Sample(latitude, longitude, chosen, elevation, coastProximity);
    }

    /// <summary>位置扭曲的幅度（格距的倍數）。0.35 格距約 5 公里：分界扭成自然的曲線，又不會把一格整個搬到隔壁。</summary>
    private const double WarpCells = 0.35;

    /// <summary>位置扭曲的波長（公尺）。</summary>
    private const double WarpWavelengthMeters = 30_000;

    /// <summary>細部起伏是附近粗格起伏的多少比例，再加上這個最小值（公尺）。</summary>
    private const double DetailReliefShare = 0.1, DetailBaseMeters = 6;

    /// <summary>細部起伏最長的波長（公尺）。</summary>
    private const double DetailWavelengthMeters = 4_000;

    /// <summary>平滑核的半徑（格距的倍數）。</summary>
    private const double KernelCells = 1.1;

    private readonly SimplexNoise3D _warpNoise = new(9173);
    private readonly SimplexNoise3D _detailNoise = new(4421);

    private (double X, double Y, double Z) Warp(double x, double y, double z)
    {
        var f = _grid.PlanetRadiusMeters / WarpWavelengthMeters;
        var amplitude = WarpCells * _grid.AverageCellSpacingMeters / _grid.PlanetRadiusMeters;
        var dx = _warpNoise.Fbm(x * f, y * f, z * f, 3, 1.0);
        var dy = _warpNoise.Fbm(x * f + 31.7, y * f - 12.3, z * f + 5.1, 3, 1.0);
        var dz = _warpNoise.Fbm(x * f - 8.9, y * f + 44.2, z * f - 27.6, 3, 1.0);
        var wx = x + amplitude * dx;
        var wy = y + amplitude * dy;
        var wz = z + amplitude * dz;
        var length = Math.Sqrt(wx * wx + wy * wy + wz * wz);
        return (wx / length, wy / length, wz / length);
    }

    private double KernelWeight(int cell, double x, double y, double z)
    {
        var u = _grid.UnitVector;
        double dx = u[cell * 3] - x, dy = u[cell * 3 + 1] - y, dz = u[cell * 3 + 2] - z;
        var d = Math.Sqrt(dx * dx + dy * dy + dz * dz) * _grid.PlanetRadiusMeters / (KernelCells * _grid.AverageCellSpacingMeters);
        if (d >= 1) return 0;
        var t = 1 - d;
        return t * t * t;
    }

    /// <summary>
    /// 把公尺換成等級：水底一定低於水面（水面是這一點自己的：海是 0、高原上的湖是湖面、河是河面——
    /// 以前寫死成 −1，高地湖整片被壓成坑，水文地貌規劃 H0），陸地不低於海平面。
    /// </summary>
    /// <param name="sample">地貌取樣。</param>
    public static (int ElevationLevel, int? WaterLevel) Levels(LandformSample sample)
    {
        var level = (int)Math.Round(sample.ElevationMeters / MetersPerElevationLevel);
        int? waterLevel = null;
        if (sample.WaterSurfaceMeters is { } surface)
        {
            waterLevel = (int)Math.Round(surface / MetersPerElevationLevel);
            level = Math.Min(level, waterLevel.Value - 1);
        }
        else
        {
            level = Math.Max(level, 0);
        }
        return (level, waterLevel);
    }

    /// <summary>
    /// 在「最近的粗格＋它離這點最近的兩個鄰居」組成的三角形上做重心內插。
    /// 點落在三角形外（權重有負值）時把負的歸零再正規化，退化成兩點或一點的內插。
    /// </summary>
    private double InterpolateElevation(int nearest, double x, double y, double z)
    {
        Span<int> neighbors = stackalloc int[SimulationGrid.MaxNeighborsPerCell];
        var count = _grid.GetNeighbors(nearest, neighbors);

        int first = -1, second = -1;
        double firstDot = double.NegativeInfinity, secondDot = double.NegativeInfinity;
        for (var i = 0; i < count; i++)
        {
            var n = neighbors[i];
            var dot = Dot(n, x, y, z);
            if (dot > firstDot)
            {
                second = first; secondDot = firstDot;
                first = n; firstDot = dot;
            }
            else if (dot > secondDot)
            {
                second = n; secondDot = dot;
            }
        }

        var e0 = _cells[nearest].SurfaceElevationMeters;
        if (second < 0) return e0;

        // 解 p = w0·v0 + w1·v1 + w2·v2（克拉瑪公式）；正規化之後就是三角形上的重心座標
        var u = _grid.UnitVector;
        double ax = u[nearest * 3], ay = u[nearest * 3 + 1], az = u[nearest * 3 + 2];
        double bx = u[first * 3], by = u[first * 3 + 1], bz = u[first * 3 + 2];
        double cx = u[second * 3], cy = u[second * 3 + 1], cz = u[second * 3 + 2];

        var det = Det(ax, bx, cx, ay, by, cy, az, bz, cz);
        if (Math.Abs(det) < 1e-15) return e0;

        var w0 = Math.Max(0, Det(x, bx, cx, y, by, cy, z, bz, cz) / det);
        var w1 = Math.Max(0, Det(ax, x, cx, ay, y, cy, az, z, cz) / det);
        var w2 = Math.Max(0, Det(ax, bx, x, ay, by, y, az, bz, z) / det);
        var sum = w0 + w1 + w2;
        if (sum <= 0) return e0;

        return (w0 * e0 + w1 * _cells[first].SurfaceElevationMeters + w2 * _cells[second].SurfaceElevationMeters) / sum;
    }

    private double Dot(int cell, double x, double y, double z) =>
        x * _grid.UnitVector[cell * 3] + y * _grid.UnitVector[cell * 3 + 1] + z * _grid.UnitVector[cell * 3 + 2];

    private static double Det(double a, double b, double c, double d, double e, double f, double g, double h, double i) =>
        a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
}
