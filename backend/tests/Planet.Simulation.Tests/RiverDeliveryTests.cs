using System;
using System.Linq;
using Planet.Domain;
using Xunit;

namespace Planet.Simulation.Tests;

/// <summary>
/// v2 P9 的河：放大時把河記進粗格（<see cref="PlanetMaterializer"/>）、取樣時沿著「格子中心 → 下游格中心」畫成細的淡水河道
/// （<see cref="SimulatedTerrainSampler"/>），地質演化視角的圖層（<see cref="SimulationField.RiversAndLakes"/>）。
/// </summary>
public class RiverDeliveryTests
{
    private static readonly SimulationGrid Grid24 = SimulationGrid.Build(TestPlanet.RadiusMeters, 24);

    private static PlanetCell[] AllGrass() => Enumerable.Range(0, Grid24.CellCount)
        .Select(c => new PlanetCell { CellId = c, Terrain = TerrainType.Grassland, SurfaceElevationMeters = 100f })
        .ToArray();

    private static (double Lat, double Lon) LatLon(double x, double y, double z)
    {
        var length = Math.Sqrt(x * x + y * y + z * z);
        return (Math.Asin(z / length) * 180 / Math.PI, Math.Atan2(y, x) * 180 / Math.PI);
    }

    [Fact]
    public void Sampler_PointOnRiverSegmentIsFreshwater_PointBesideItIsNot()
    {
        var cells = AllGrass();
        const int from = 500;
        Span<int> neighbors = stackalloc int[SimulationGrid.MaxNeighborsPerCell];
        Grid24.GetNeighbors(from, neighbors);
        var to = neighbors[0];
        cells[from].RiverDownstreamCellId = to;
        cells[from].RiverSize = 0.5f;
        var sampler = new SimulatedTerrainSampler(Grid24, cells);

        var u = Grid24.UnitVector;
        // 河段上、靠近起點那一端（還在起點那一格裡）
        double ax = u[from * 3], ay = u[from * 3 + 1], az = u[from * 3 + 2];
        double bx = u[to * 3], by = u[to * 3 + 1], bz = u[to * 3 + 2];
        var (lat, lon) = LatLon(ax + 0.3 * (bx - ax), ay + 0.3 * (by - ay), az + 0.3 * (bz - az));
        Assert.Equal(TerrainType.Freshwater, sampler.Sample(lat, lon).Terrain);

        // 往旁邊偏 20 公里（遠大於河寬），不是河
        double px = ay * (bz - az) - az * (by - ay), py = az * (bx - ax) - ax * (bz - az), pz = ax * (by - ay) - ay * (bx - ax);
        var pl = Math.Sqrt(px * px + py * py + pz * pz);
        var offset = 20_000 / TestPlanet.RadiusMeters / pl;
        (lat, lon) = LatLon(ax + 0.3 * (bx - ax) + px * offset, ay + 0.3 * (by - ay) + py * offset, az + 0.3 * (bz - az) + pz * offset);
        Assert.Equal(TerrainType.Grassland, sampler.Sample(lat, lon).Terrain);
    }

    [Fact]
    public void Materializer_RiversOnlyOnLand_WithValidDownstream()
    {
        var engine = new SimulationEngine(Grid24, 42, crustTransport: CrustTransport.PlateFrame);
        engine.Step(40);
        var cells = PlanetMaterializer.BuildCells(Grid24, engine.State, engine.Hydrology);
        var rivers = cells.Where(c => c.RiverDownstreamCellId >= 0).ToArray();
        Assert.NotEmpty(rivers);
        Assert.All(rivers, c =>
        {
            Assert.True(c.WaterDepthMeters <= 0f, $"第 {c.CellId} 格是水卻被標成河");
            Assert.InRange(c.RiverDownstreamCellId, 0, Grid24.CellCount - 1);
            Assert.InRange(c.RiverSize, 0f, 1f);
        });
        // 沒給水系（v1）就沒有河
        Assert.All(PlanetMaterializer.BuildCells(Grid24, engine.State), c => Assert.Equal(-1, c.RiverDownstreamCellId));
    }

    [Fact]
    public void RiversLayer_RendersWithAndWithoutHydrology()
    {
        var engine = new SimulationEngine(Grid24, 42, crustTransport: CrustTransport.PlateFrame);
        engine.Step(5);
        var renderer = new SimulationFieldRenderer(Grid24);
        Assert.Equal(SimulationFieldRenderer.Width * SimulationFieldRenderer.Height * 4,
            renderer.Render(engine.State, SimulationField.RiversAndLakes, engine.Hydrology).Length);
        Assert.Equal(SimulationFieldRenderer.Width * SimulationFieldRenderer.Height * 4,
            renderer.Render(engine.State, SimulationField.RiversAndLakes).Length);
    }
}
