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
