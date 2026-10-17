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
/// </summary>
public sealed class SimulatedTerrainSampler : ITerrainGenerator
{
    /// <summary>每個海拔等級代表幾公尺（同 <see cref="Tile.ElevationLevel"/>）。</summary>
    public const double MetersPerElevationLevel = 5.0;

    private readonly SimulationGrid _grid;
    private readonly IReadOnlyList<PlanetCell> _cells;

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
    }

    /// <inheritdoc />
    public TerrainSample Sample(double latitude, double longitude)
    {
        var lat = latitude * Math.PI / 180.0;
        var lon = longitude * Math.PI / 180.0;
        var x = Math.Cos(lat) * Math.Cos(lon);
        var y = Math.Cos(lat) * Math.Sin(lon);
        var z = Math.Sin(lat);

        var nearest = _grid.FindNearestCell(x, y, z);
        var cell = _cells[nearest];
        var elevation = InterpolateElevation(nearest, x, y, z);

        var level = (int)Math.Round(elevation / MetersPerElevationLevel);
        level = cell.WaterDepthMeters > 0f ? Math.Min(level, -1) : Math.Max(level, 0);

        return new TerrainSample(cell.Terrain, cell.HeightCategory, level, nearest);
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
