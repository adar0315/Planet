using System;
using System.Collections.Generic;
using System.Linq;
using Planet.Domain;
using Xunit;

namespace Planet.Simulation.Tests;

/// <summary>
/// 河的上游一定比下游高（2026-11-07 使用者回報）。量到過的三種壞法：畫成河的小格沿用谷壁上的高度、
/// 流向穿過乾涸的內流盆地翻過山脊、河穿過冰河格。這裡用一條手造的河盯住這些規則。
/// </summary>
public class RiverProfileTests
{
    private static readonly SimulationGrid Grid = SimulationGrid.Build(TestPlanet.RadiusMeters, 24);

    /// <summary>全部是 1,000 公尺的陸地，加上一條不繞回頭的河，最後一格是 <paramref name="end"/> 指定的水體。</summary>
    private static (PlanetCell[] Cells, int[] Path) River(double[] heights, Action<PlanetCell>? end = null)
    {
        var cells = Enumerable.Range(0, Grid.CellCount).Select(c => new PlanetCell
        {
            CellId = c,
            Terrain = TerrainType.Forest,
            SurfaceElevationMeters = 1_000f,
            RiverDownstreamCellId = -1,
        }).ToArray();

        var path = new List<int> { 0 };
        Span<int> neighbors = stackalloc int[SimulationGrid.MaxNeighborsPerCell];
        var u = Grid.UnitVector;
        while (path.Count <= heights.Length)
        {
            var count = Grid.GetNeighbors(path[^1], neighbors);
            var next = -1;
            var farthest = double.MaxValue;
            for (var i = 0; i < count; i++)
            {
                var k = neighbors[i];
                if (path.Contains(k)) continue;
                var dot = u[k * 3] * u[0] + u[k * 3 + 1] * u[1] + u[k * 3 + 2] * u[2];
                if (dot < farthest) { farthest = dot; next = k; }
            }
            path.Add(next);
        }

        for (var i = 0; i < heights.Length; i++)
        {
            var cell = cells[path[i]];
            cell.SurfaceElevationMeters = (float)heights[i];
            cell.RiverDownstreamCellId = path[i + 1];
            cell.RiverWidthMeters = 40f;
            cell.IncisionRate = 100f;
        }
        var last = cells[path[^1]];
        last.Terrain = TerrainType.Seawater;
        last.WaterDepthMeters = 100f;
        last.IsOcean = true;
        end?.Invoke(last);
        return (cells, path.ToArray());
    }

    /// <summary>沿河道中心線由上游往下游取樣高度。</summary>
    private static List<double> BedProfile(LandformSampler sampler, IEnumerable<int> path)
    {
        var profile = new List<double>();
        foreach (var c in path)
            foreach (var (latitude, longitude) in sampler.RiverCenterline(c))
            {
                var sample = sampler.Sample(latitude, longitude);
                if (sample.RiverCellId >= 0) profile.Add(sample.ElevationMeters);
            }
        return profile;
    }

    private static void AssertDescends(IReadOnlyList<double> profile)
    {
        Assert.True(profile.Count > 10);
        for (var i = 1; i < profile.Count; i++)
            Assert.True(profile[i] <= profile[i - 1] + 0.5, $"第 {i} 點往下游升高：{profile[i - 1]:F1} → {profile[i]:F1}");
    }

    [Fact]
    public void BedDescendsThroughARiseInTheCoarseTerrain()
    {
        // 第三格比上游高 150 公尺：要刻一段峽谷穿過去，不能跟著地表上去
        var (cells, path) = River([900, 600, 750, 500, 300]);
        AssertDescends(BedProfile(new LandformSampler(Grid, cells), path[..^1]));
    }

    [Fact]
    public void CellsDrawnAsRiverSitAtTheBedNotOnTheValleyWall()
    {
        var (cells, path) = River([900, 700, 500, 300, 100]);
        var sampler = new LandformSampler(Grid, cells);
        var degrees = 150 / Grid.PlanetRadiusMeters * 180 / Math.PI;
        var compared = 0;
        foreach (var (latitude, longitude) in sampler.RiverCenterline(path[1]))
        {
            var center = sampler.Sample(latitude, longitude);
            var aside = sampler.Sample(latitude + degrees, longitude);
            if (aside.RiverDistanceMeters > LandformSampler.VisibleRiverHalfWidthMeters) continue;
            compared++;
            Assert.True(Math.Abs(aside.ElevationMeters - center.ElevationMeters) < 15, $"{aside.ElevationMeters:F1} vs {center.ElevationMeters:F1}");
        }
        Assert.True(compared > 10);
    }

    [Fact]
    public void RiverEndsWhereTheDownstreamCellIsFarHigher()
    {
        // 乾涸的內流盆地：盆底 100 公尺，流向卻接到 2,000 公尺的溢出口
        var (cells, path) = River([500, 100, 2_000, 1_500, 1_000]);
        var sampler = new LandformSampler(Grid, cells);
        Assert.NotEmpty(sampler.RiverCenterline(path[0]));
        Assert.Empty(sampler.RiverCenterline(path[1]));
        Assert.NotEmpty(sampler.RiverCenterline(path[2]));
    }

    [Fact]
    public void NoRiverOnGlaciers()
    {
        var (cells, path) = River([900, 700, 500, 300, 100]);
        cells[path[2]].Terrain = TerrainType.Glacier;
        var sampler = new LandformSampler(Grid, cells);
        Assert.Empty(sampler.RiverCenterline(path[1])); // 流到冰河就結束
        Assert.Empty(sampler.RiverCenterline(path[2]));
        Assert.NotEmpty(sampler.RiverCenterline(path[3])); // 冰河末端重新開始
    }

    /// <summary>三角洲往海裡長出去（2026-11-07：原本只畫在河那格的陸地上，又只有幾公里，地圖上看不到）。</summary>
    [Fact]
    public void DeltaBuildsLandOutIntoTheSea()
    {
        var (cells, path) = River([300, 200, 100, 50, 20], sea => sea.DeltaSupply = 1_000f);
        var sampler = new LandformSampler(Grid, cells);
        cells[path[^1]].DeltaSupply = 0f;
        var withoutDelta = new LandformSampler(Grid, cells);
        var u = Grid.UnitVector;
        (double X, double Y, double Z) At(int c) => (u[c * 3], u[c * 3 + 1], u[c * 3 + 2]);
        var (land, sea) = (At(path[^2]), At(path[^1]));
        LandformSample SampleSeaward(double km, LandformSampler? from = null)
        {
            // 河那格與海那格的中點（海岸）往海的方向走 km 公里，再往側邊偏 2 公里避開主河道
            var t = 0.5 + km * 1000 / (Math.Sqrt(Math.Pow(sea.X - land.X, 2) + Math.Pow(sea.Y - land.Y, 2) + Math.Pow(sea.Z - land.Z, 2)) * Grid.PlanetRadiusMeters);
            var p = (X: land.X + (sea.X - land.X) * t, Y: land.Y + (sea.Y - land.Y) * t, Z: land.Z + (sea.Z - land.Z) * t);
            var side = (X: land.Y * sea.Z - land.Z * sea.Y, Y: land.Z * sea.X - land.X * sea.Z, Z: land.X * sea.Y - land.Y * sea.X);
            var sideLength = Math.Sqrt(side.X * side.X + side.Y * side.Y + side.Z * side.Z);
            var offset = 2_000 / Grid.PlanetRadiusMeters / sideLength;
            p = (p.X + side.X * offset, p.Y + side.Y * offset, p.Z + side.Z * offset);
            var length = Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
            return (from ?? sampler).Sample(Math.Asin(p.Z / length) * 180 / Math.PI, Math.Atan2(p.Y, p.X) * 180 / Math.PI);
        }

        var nearShore = SampleSeaward(3);
        Assert.NotEqual(TerrainType.Seawater, nearShore.Terrain);
        Assert.True(nearShore.ElevationMeters > -5, $"{nearShore.ElevationMeters:F1}");
        Assert.Equal(TerrainType.Seawater, SampleSeaward(3, withoutDelta).Terrain); // 沒有三角洲那裡是海
    }

    /// <summary>
    /// 2026-11-18 正式站：三角洲泥灘長到 -1,075 公尺的海上，邊緣是一道垂直的牆。
    /// 外緣要是沙（河口沙洲），外面的海底從淺處慢慢變深（前積層），遠處才是海床原本的深度。
    /// </summary>
    [Fact]
    public void DeltaFrontIsSandAndSlopesIntoTheSea()
    {
        var (cells, path) = River([300, 200, 100, 50, 20], sea => sea.DeltaSupply = 1_000f);
        var sampler = new LandformSampler(Grid, cells);
        var u = Grid.UnitVector;
        (double X, double Y, double Z) At(int c) => (u[c * 3], u[c * 3 + 1], u[c * 3 + 2]);
        var (land, sea) = (At(path[^2]), At(path[^1]));
        var spacing = Math.Sqrt(Math.Pow(sea.X - land.X, 2) + Math.Pow(sea.Y - land.Y, 2) + Math.Pow(sea.Z - land.Z, 2)) * Grid.PlanetRadiusMeters;
        const double floor = -3_000;
        LandformSample Seaward(double meters)
        {
            // 從海岸往海走，往側邊偏 2 公里避開主分流；海那格的海床當成 3,000 公尺深
            var t = 0.5 + meters / spacing;
            var p = (X: land.X + (sea.X - land.X) * t, Y: land.Y + (sea.Y - land.Y) * t, Z: land.Z + (sea.Z - land.Z) * t);
            var side = (X: land.Y * sea.Z - land.Z * sea.Y, Y: land.Z * sea.X - land.X * sea.Z, Z: land.X * sea.Y - land.Y * sea.X);
            var offset = 2_000 / Grid.PlanetRadiusMeters / Math.Sqrt(side.X * side.X + side.Y * side.Y + side.Z * side.Z);
            p = (p.X + side.X * offset, p.Y + side.Y * offset, p.Z + side.Z * offset);
            var length = Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
            return sampler.Sample(Math.Asin(p.Z / length) * 180 / Math.PI, Math.Atan2(p.Y, p.X) * 180 / Math.PI, path[^1], floor);
        }

        LandformSample? previous = null;
        var sawFront = false;
        for (var m = 0.0; m <= 120_000; m += 200)
        {
            var s = Seaward(m);
            if (previous is { } p)
            {
                if (p.Terrain != TerrainType.Seawater && s.Terrain == TerrainType.Seawater)
                {
                    sawFront = true;
                    Assert.Equal(TerrainType.Sand, p.Terrain); // 最外緣是沙
                    Assert.True(s.ElevationMeters > -20, $"三角洲外第一步的海底 {s.ElevationMeters:F0} 公尺");
                }
                if (p.Terrain == TerrainType.Seawater && s.Terrain == TerrainType.Seawater)
                    Assert.True(Math.Abs(s.ElevationMeters - p.ElevationMeters) < 120, $"{m} 公尺處海底一步差 {Math.Abs(s.ElevationMeters - p.ElevationMeters):F0} 公尺");
            }
            previous = s;
        }
        Assert.True(sawFront, "沒走到三角洲外緣");
        Assert.Equal(floor, previous!.Value.ElevationMeters); // 遠處是海床原本的深度
    }

    /// <summary>
    /// 前積層延伸到別的海格也要連續：以前只有「記得這個三角洲」的格子才有深度上限，跨到沒記的格子海底跳好幾百公尺
    /// （2026-11-18 正式站遠景地塊，粗格交界上 720 公尺）。做法：河口外整片都是 3,000 公尺深的海，鋪 1 公里的點陣看相鄰兩點的高差。
    /// </summary>
    [Fact]
    public void ProdeltaIsContinuousAcrossCells()
    {
        var (cells, path) = River([300, 200, 100, 50, 20], sea => sea.DeltaSupply = 1_000f);
        var u = Grid.UnitVector;
        (double X, double Y, double Z) At(int c) => (u[c * 3], u[c * 3 + 1], u[c * 3 + 2]);
        var (land, sea) = (At(path[^2]), At(path[^1]));
        var axis = (X: sea.X - land.X, Y: sea.Y - land.Y, Z: sea.Z - land.Z);
        var mid = (X: (sea.X + land.X) / 2, Y: (sea.Y + land.Y) / 2, Z: (sea.Z + land.Z) / 2);
        foreach (var c in cells)
        {
            if (path.Contains(c.CellId)) continue;
            var p = At(c.CellId);
            if ((p.X - mid.X) * axis.X + (p.Y - mid.Y) * axis.Y + (p.Z - mid.Z) * axis.Z <= 0) continue;
            c.Terrain = TerrainType.Seawater;
            c.WaterDepthMeters = 3_000f;
            c.IsOcean = true;
        }
        foreach (var c in cells.Where(c => c.IsOcean)) c.SurfaceElevationMeters = -3_000f;
        var sampler = new LandformSampler(Grid, cells);

        static (double X, double Y, double Z) Unit((double X, double Y, double Z) v)
        {
            var l = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            return (v.X / l, v.Y / l, v.Z / l);
        }
        var coast = Unit(mid);
        var ax = Unit(axis);
        var side = Unit((coast.Y * ax.Z - coast.Z * ax.Y, coast.Z * ax.X - coast.X * ax.Z, coast.X * ax.Y - coast.Y * ax.X));
        const double step = 1_000;
        const int size = 200; // 往海 200 公里、兩側各 100 公里
        var previousRow = new double?[size];
        var worst = 0.0;
        var where = "";
        for (var i = 0; i < size; i++)
        {
            double? left = null;
            for (var j = 0; j < size; j++)
            {
                var a = i * step / Grid.PlanetRadiusMeters;
                var b = (j - size / 2) * step / Grid.PlanetRadiusMeters;
                var p = Unit((coast.X + a * ax.X + b * side.X, coast.Y + a * ax.Y + b * side.Y, coast.Z + a * ax.Z + b * side.Z));
                var s = sampler.Sample(Math.Asin(p.Z) * 180 / Math.PI, Math.Atan2(p.Y, p.X) * 180 / Math.PI);
                double? here = s.Terrain == TerrainType.Seawater ? s.ElevationMeters : null;
                if (here is { } h)
                {
                    if (left is { } l && Math.Abs(h - l) > worst) { worst = Math.Abs(h - l); where = $"{i},{j} {l:F0}→{h:F0} 左右"; }
                    if (previousRow[j] is { } up && Math.Abs(h - up) > worst) { worst = Math.Abs(h - up); where = $"{i},{j} {up:F0}→{h:F0} 前後"; }
                }
                previousRow[j] = here;
                left = here;
            }
        }
        // 接 3,000 公尺深的海床，最陡約 6 度（1 公里約 110 公尺）
        Assert.True(worst < 150, $"相鄰兩點（1 公里）海底高差 {worst:F0} 公尺（{where}，往海第幾公里,側向第幾格）");
    }

    /// <summary>
    /// 小格的海岸線會被雜訊扭曲、跟著等高線走，可能跑到三角洲頂點的後面（內陸）：那裡的海也要填成三角洲，
    /// 不然三角洲跟陸地之間夾一條海，變成離岸的島（2026-11-08 實測：半徑 5.9 公里的三角洲，頂點後面 1 公里還是海）。
    /// </summary>
    [Fact]
    public void DeltaStaysAttachedWhenTheCoastIsPulledBehindTheApex()
    {
        // 供砂 38.7 → 半徑約 5.9 公里，頂點在海岸往內陸約 1.8 公里
        var (cells, path) = River([300, 200, 100, 50, 20], sea => sea.DeltaSupply = 38.7f);
        var sampler = new LandformSampler(Grid, cells);
        var u = Grid.UnitVector;
        (double X, double Y, double Z) At(int c) => (u[c * 3], u[c * 3 + 1], u[c * 3 + 2]);
        var (land, sea) = (At(path[^2]), At(path[^1]));
        var seaCell = path[^1];
        // 海岸（兩格中點）往內陸 km 公里，但當成海那一格（模擬被扭到內陸的海岸線）
        LandformSample InlandButSea(double km)
        {
            var spacing = Math.Sqrt(Math.Pow(sea.X - land.X, 2) + Math.Pow(sea.Y - land.Y, 2) + Math.Pow(sea.Z - land.Z, 2)) * Grid.PlanetRadiusMeters;
            var t = 0.5 - km * 1000 / spacing;
            var p = (X: land.X + (sea.X - land.X) * t, Y: land.Y + (sea.Y - land.Y) * t, Z: land.Z + (sea.Z - land.Z) * t);
            var length = Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
            return sampler.Sample(Math.Asin(p.Z / length) * 180 / Math.PI, Math.Atan2(p.Y, p.X) * 180 / Math.PI, seaCell);
        }

        Assert.NotEqual(TerrainType.Seawater, InlandButSea(2.5).Terrain); // 頂點後面 0.7 公里：填成三角洲
        Assert.Equal(TerrainType.Seawater, InlandButSea(10).Terrain); // 遠離三角洲的海照舊是海
    }

    /// <summary>
    /// 三角洲往海裡長出去的部分要是一整塊，不能有脫離主體的碎塊（2026-11-15 在正式站量到：同一帶有 4、4、6、26、29 格的泥灘，
    /// 四周都是海）。成因是外緣用「照位置取樣的雜訊」切：主體外面雜訊剛好偏高的一小塊也被當成扇面。
    /// 現在雜訊只跟方向有關，從頂點往外每個方向只有一個外緣距離。
    /// 做法：河口周圍鋪 400 公尺的點陣、全部當成海格取樣，被填成三角洲的點只能有一個連通塊。
    /// </summary>
    [Theory]
    [InlineData(38.7f)]
    [InlineData(300f)]
    [InlineData(1_000f)]
    [InlineData(4_000f)]
    public void DeltaIsOnePieceWithNoDetachedIslands(float supply)
    {
        var (cells, path) = River([300, 200, 100, 50, 20], sea => sea.DeltaSupply = supply);
        var sampler = new LandformSampler(Grid, cells);
        var u = Grid.UnitVector;
        (double X, double Y, double Z) At(int c) => (u[c * 3], u[c * 3 + 1], u[c * 3 + 2]);
        static (double X, double Y, double Z) Unit((double X, double Y, double Z) v)
        {
            var l = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            return (v.X / l, v.Y / l, v.Z / l);
        }
        var (land, sea) = (At(path[^2]), At(path[^1]));
        var coast = Unit((land.X + sea.X, land.Y + sea.Y, land.Z + sea.Z));
        var axis = Unit((sea.X - land.X, sea.Y - land.Y, sea.Z - land.Z));
        var side = Unit((coast.Y * axis.Z - coast.Z * axis.Y, coast.Z * axis.X - coast.X * axis.Z, coast.X * axis.Y - coast.Y * axis.X));

        // 涵蓋整個扇面再多一圈（半徑最多 60 公里，外緣最遠到 1.325 倍）
        const double step = 400;
        var reach = 60_000 * 1.5;
        var size = (int)(2 * reach / step) + 1;
        var inFan = new bool[size, size];
        var total = 0;
        for (var i = 0; i < size; i++)
        {
            for (var j = 0; j < size; j++)
            {
                var a = (-reach + i * step) / Grid.PlanetRadiusMeters;
                var b = (-reach + j * step) / Grid.PlanetRadiusMeters;
                var p = Unit((coast.X + a * axis.X + b * side.X, coast.Y + a * axis.Y + b * side.Y, coast.Z + a * axis.Z + b * side.Z));
                var sample = sampler.Sample(Math.Asin(p.Z) * 180 / Math.PI, Math.Atan2(p.Y, p.X) * 180 / Math.PI, path[^1]);
                inFan[i, j] = sample.Terrain != TerrainType.Seawater;
                if (inFan[i, j]) total++;
            }
        }
        Assert.True(total > 50, $"三角洲只取樣到 {total} 點：場景沒有長出三角洲");

        // 連通塊（八方向）
        var seen = new bool[size, size];
        var pieces = new List<int>();
        var stack = new Stack<(int, int)>();
        for (var i = 0; i < size; i++)
        {
            for (var j = 0; j < size; j++)
            {
                if (!inFan[i, j] || seen[i, j]) continue;
                var count = 0;
                seen[i, j] = true;
                stack.Push((i, j));
                while (stack.Count > 0)
                {
                    var (ci, cj) = stack.Pop();
                    count++;
                    for (var di = -1; di <= 1; di++)
                    {
                        for (var dj = -1; dj <= 1; dj++)
                        {
                            int ni = ci + di, nj = cj + dj;
                            if (ni < 0 || nj < 0 || ni >= size || nj >= size || !inFan[ni, nj] || seen[ni, nj]) continue;
                            seen[ni, nj] = true;
                            stack.Push((ni, nj));
                        }
                    }
                }
                pieces.Add(count);
            }
        }
        pieces.Sort();
        Assert.True(pieces.Count == 1, $"三角洲分成 {pieces.Count} 塊（點數由小到大：{string.Join(", ", pieces)}）");
    }

    [Fact]
    public void RiverIntoAHighLakeStaysAboveTheLakeSurface()
    {
        var (cells, path) = River([1_500, 1_400, 1_300, 1_250, 1_210], lake =>
        {
            lake.Terrain = TerrainType.Freshwater;
            lake.IsOcean = false;
            lake.WaterSurfaceMeters = 1_200f;
        });
        var profile = BedProfile(new LandformSampler(Grid, cells), path[..^1]);
        AssertDescends(profile);
        Assert.True(profile.Min() > 1_190, $"河床 {profile.Min():F0} 公尺，比湖面低太多");
    }
}
