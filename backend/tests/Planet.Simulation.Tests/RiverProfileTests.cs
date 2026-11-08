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
