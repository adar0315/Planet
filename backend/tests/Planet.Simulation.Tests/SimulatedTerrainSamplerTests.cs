using System;
using System.Collections.Generic;
using System.Linq;
using Planet.Domain;
using Planet.Generation;
using Xunit;

namespace Planet.Simulation.Tests;

/// <summary>
/// P1「端到端管線」：模擬粗格 → 地形分類 → 放大取樣。
/// 守的是放大不能自己造出地形：海陸跟最近粗格一致、高度內插不超出周圍粗格、每個地塊都追得回來源。
/// </summary>
public class SimulatedTerrainSamplerTests
{
    private static readonly Lazy<(SimulationGrid Grid, SimulationEngine Engine, PlanetCell[] Cells)> World = new(() =>
    {
        var grid = SimulationGrid.Build(TestPlanet.RadiusMeters, 24);
        var engine = new SimulationEngine(grid, 42);
        engine.Step(60);
        return (grid, engine, PlanetMaterializer.BuildCells(grid, engine.State));
    });

    private static (double Lat, double Lon) LatLonOf(double x, double y, double z) =>
        (Math.Asin(Math.Clamp(z, -1, 1)) * 180 / Math.PI, Math.Atan2(y, x) * 180 / Math.PI);

    [Fact]
    public void BuildCells_WaterAndLandMatchTheSimulation()
    {
        var (_, engine, cells) = World.Value;
        var state = engine.State;

        Assert.Contains(cells, c => c.WaterDepthMeters <= 0f);
        Assert.Contains(cells, c => c.WaterDepthMeters > 0f);
        foreach (var cell in cells)
        {
            var isWaterTerrain = cell.Terrain is TerrainType.Seawater or TerrainType.Freshwater or TerrainType.CoralReef;
            var wet = state.WaterDepth[cell.CellId] > 0f;
            if (isWaterTerrain) Assert.True(wet, $"格 {cell.CellId} 分類成 {cell.Terrain}，模擬裡卻是陸地");
            if (wet && cell.Terrain != TerrainType.Glacier) Assert.True(isWaterTerrain, $"格 {cell.CellId} 有水卻分類成 {cell.Terrain}");
            if (cell.Terrain == TerrainType.Seawater) Assert.True(cell.IsOcean);
            if (cell.Terrain == TerrainType.Freshwater) Assert.False(cell.IsOcean);
        }
    }

    /// <summary>
    /// 降水是相對尺度。2026-10-17 第一次試寫拿地球的公釐門檻去切，陸地全部變成沙漠——
    /// 溫暖的陸地要分得出沙漠、灌木、草原、林地。
    /// </summary>
    [Fact]
    public void BuildCells_WarmLandHasAllVegetationBands()
    {
        var (_, _, cells) = World.Value;
        var warmLand = cells.Where(c => c.WaterDepthMeters <= 0f && c.Terrain is TerrainType.Sand
            or TerrainType.Shrubland or TerrainType.Grassland or TerrainType.Forest or TerrainType.Swamp).ToList();

        Assert.True(warmLand.Count > 50, $"溫暖的植被陸地只有 {warmLand.Count} 格");
        foreach (var band in new[] { TerrainType.Sand, TerrainType.Shrubland, TerrainType.Grassland, TerrainType.Forest })
        {
            var share = warmLand.Count(c => c.Terrain == band) / (double)warmLand.Count;
            Assert.True(share is > 0.05 and < 0.6, $"{band} 佔 {share:P0}");
        }
    }

    [Fact]
    public void BuildCells_EnclosedWaterIsFreshwater()
    {
        var (grid, engine, _) = World.Value;
        var state = engine.State;

        // 在一塊陸地中間挖一格水：四周都是陸地，所以是湖，不是海（避開礁體與冰，那兩種會分類成別的）
        var lake = Enumerable.Range(0, state.CellCount).First(c =>
        {
            if (state.WaterDepth[c] > 0f || state.OrganicThickness[c] > 0f || state.IceThickness[c] > 0f) return false;
            Span<int> buffer = stackalloc int[SimulationGrid.MaxNeighborsPerCell];
            var count = grid.GetNeighbors(c, buffer);
            for (var i = 0; i < count; i++) if (state.WaterDepth[buffer[i]] > 0f) return false;
            return true;
        });
        var saved = state.WaterDepth[lake];
        state.WaterDepth[lake] = 30f;
        try
        {
            var cells = PlanetMaterializer.BuildCells(grid, state);
            Assert.Equal(TerrainType.Freshwater, cells[lake].Terrain);
            Assert.False(cells[lake].IsOcean);
        }
        finally
        {
            state.WaterDepth[lake] = saved;
        }
    }

    [Fact]
    public void Sample_AtCellCenter_ReturnsThatCell()
    {
        var (grid, _, cells) = World.Value;
        var sampler = new SimulatedTerrainSampler(grid, cells);

        for (var c = 0; c < grid.CellCount; c += 37)
        {
            var (lat, lon) = (grid.Latitude[c], grid.Longitude[c]);
            var sample = sampler.Sample(lat, lon);
            Assert.Equal(c, sample.SourceCellId);
            Assert.Equal(cells[c].Terrain, sample.Terrain);
        }
    }

    [Fact]
    public void Sample_SeaAndLandAgreeWithNearestCell_AndHeightStaysWithinNeighbours()
    {
        var (grid, _, cells) = World.Value;
        var sampler = new SimulatedTerrainSampler(grid, cells);
        var random = new Random(7);
        Span<int> neighbors = stackalloc int[SimulationGrid.MaxNeighborsPerCell];

        for (var i = 0; i < 5_000; i++)
        {
            double x = random.NextDouble() * 2 - 1, y = random.NextDouble() * 2 - 1, z = random.NextDouble() * 2 - 1;
            var length = Math.Sqrt(x * x + y * y + z * z);
            if (length < 1e-3) continue;
            var (lat, lon) = LatLonOf(x / length, y / length, z / length);

            var sample = sampler.Sample(lat, lon);
            var source = cells[sample.SourceCellId!.Value];
            if (source.WaterDepthMeters > 0f) Assert.True(sample.ElevationLevel < 0, $"水格取樣出 {sample.ElevationLevel} 級");
            else Assert.True(sample.ElevationLevel >= 0, $"陸地格取樣出 {sample.ElevationLevel} 級");

            // 內插不能超出「來源格＋鄰居」的高度範圍（再加上海陸夾限造成的一級誤差）
            var count = grid.GetNeighbors(source.CellId, neighbors);
            var around = new List<float> { source.SurfaceElevationMeters };
            for (var k = 0; k < count; k++) around.Add(cells[neighbors[k]].SurfaceElevationMeters);
            var meters = sample.ElevationLevel * SimulatedTerrainSampler.MetersPerElevationLevel;
            var low = Math.Min(around.Min(), source.WaterDepthMeters > 0f ? -5f : 0f) - 5;
            var high = Math.Max(around.Max(), source.WaterDepthMeters > 0f ? -5f : 0f) + 5;
            Assert.InRange(meters, low, high);
        }
    }

    [Fact]
    public void Sample_HeightIsSmootherThanNearestCell()
    {
        // 沿一條經線走，相鄰取樣的高度跳動：內插要比「直接取最近粗格」小，否則就沒有抹平階梯
        var (grid, _, cells) = World.Value;
        var sampler = new SimulatedTerrainSampler(grid, cells);

        double interpolatedJumps = 0, nearestJumps = 0;
        int? previousLevel = null, previousNearest = null;
        for (var lat = -80.0; lat <= 80.0; lat += 0.05)
        {
            var sample = sampler.Sample(lat, 37.0);
            var nearestLevel = (int)Math.Round(cells[sample.SourceCellId!.Value].SurfaceElevationMeters / 5);
            if (previousLevel is { } p) interpolatedJumps += Math.Abs(sample.ElevationLevel - p);
            if (previousNearest is { } q) nearestJumps += Math.Abs(nearestLevel - q);
            previousLevel = sample.ElevationLevel;
            previousNearest = nearestLevel;
        }

        Assert.True(interpolatedJumps < nearestJumps, $"內插總跳動 {interpolatedJumps}，最近粗格 {nearestJumps}");
    }

    [Fact]
    public void Generator_EveryTileTracesBackToItsSourceCell()
    {
        var (grid, _, cells) = World.Value;
        var generator = new GoldbergPlanetGenerator(40, new SimulatedTerrainSampler(grid, cells));

        var tiles = generator.GenerateTiles().ToList();

        Assert.Equal(generator.TotalTileCount, tiles.Count);
        Assert.All(tiles, t =>
        {
            Assert.NotNull(t.SourceCellId);
            Assert.Equal(cells[t.SourceCellId!.Value].Terrain, t.Terrain);
        });
    }

    [Fact]
    public void Sampler_RejectsCellsFromAnotherGrid()
    {
        var (grid, _, cells) = World.Value;
        Assert.Throws<ArgumentException>(() => new SimulatedTerrainSampler(grid, cells.Take(100).ToArray()));
    }
}
