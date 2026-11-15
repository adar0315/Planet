using Planet.Domain;
using Planet.Generation;
using Planet.Service;
using Planet.Simulation;

namespace Planet.Service.Tests;

/// <summary><see cref="CellSeasonsService"/>（粗格的四季）。</summary>
public class CellSeasonsServiceTests
{
    private static readonly SimulationGrid Grid = SimulationGrid.Build(new GoldbergPlanetGenerator().PlanetRadiusMeters, 24);

    private static byte[] Bytes(params float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static CellSeasonsService ForSource(ITerrainGenerator source) => new(_ => Task.FromResult(source));

    private static SimulatedTerrainSampler Simulated()
    {
        var cells = Enumerable.Range(0, Grid.CellCount)
            .Select(c => new PlanetCell
            {
                CellId = c,
                Terrain = TerrainType.Grassland,
                SurfaceElevationMeters = 100f,
                // 每格的值不同，才認得出回傳的是哪一格
                MonthlyTemperature = Bytes(Enumerable.Range(0, 12).Select(m => c + m / 100f).ToArray()),
                MonthlyPrecipitation = Bytes(Enumerable.Range(0, 12).Select(m => (float)m).ToArray()),
                MonthlyDischargeRatio = Bytes(Enumerable.Repeat(1f, 12).ToArray()),
                SnowMonths = c % 13,
                FrozenMonths = c % 7,
                FlowRegime = c % 4,
            })
            .ToArray();
        return new SimulatedTerrainSampler(Grid, cells);
    }

    [Fact]
    public async Task ProceduralPlanet_HasNoSeasons()
    {
        var service = ForSource(new TestWorld.ProceduralSource());

        Assert.Empty(await service.GetSeasonsAsync([0, 1, 2], default));
    }

    [Fact]
    public async Task ReturnsTheRequestedCells_DecodedFromTheirBytes()
    {
        var service = ForSource(Simulated());

        var result = await service.GetSeasonsAsync([5, 300], default);

        Assert.Equal([5, 300], result.Select(r => r.CellId));
        var cell = result[1];
        Assert.Equal(12, cell.Temperature.Length);
        Assert.Equal(300f, cell.Temperature[0]);
        Assert.Equal(300.11f, cell.Temperature[11]);
        Assert.Equal(Enumerable.Range(0, 12).Select(m => (float)m), cell.Precipitation);
        Assert.All(cell.DischargeRatio, v => Assert.Equal(1f, v));
        Assert.Equal((300 % 13, 300 % 7, 300 % 4), (cell.SnowMonths, cell.FrozenMonths, cell.FlowRegime));
    }

    [Fact]
    public async Task OutOfRangeAndDuplicateIds_AreDroppedInsteadOfFailing()
    {
        var service = ForSource(Simulated());

        var result = await service.GetSeasonsAsync([-1, 7, Grid.CellCount, 7, int.MaxValue, 8], default);

        Assert.Equal([7, 8], result.Select(r => r.CellId));
    }
}
