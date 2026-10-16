using System.Linq;
using Xunit;

namespace Planet.Simulation.Tests;

/// <summary>固定儀表的守門測試：分布要加總回全體、雜湊要能分辨「同一顆」與「不同顆」星球。</summary>
public class SimulationDiagnosticsTests
{
    private static SimulationEngine Run(int seed, int steps)
    {
        var engine = new SimulationEngine(SimulationGrid.Build(TestPlanet.RadiusMeters, 24), seed);
        engine.Step(steps);
        return engine;
    }

    [Fact]
    public void Hypsometry_CountsEveryCellOnce()
    {
        var engine = Run(42, 40);
        var bins = SimulationDiagnostics.Hypsometry(engine.State);

        Assert.Equal(engine.State.CellCount, bins.Sum(b => b.Cells));
        Assert.True(bins.Zip(bins.Skip(1)).All(p => p.First.FromMeters < p.Second.FromMeters), "高度分格要由低到高排列");
    }

    [Fact]
    public void LandMassBuckets_AddUpToAllLand()
    {
        var engine = Run(42, 40);
        var buckets = SimulationDiagnostics.LandMassBuckets(engine.Grid, engine.State);
        var landCells = engine.State.WaterDepth.Count(d => d <= 0f);

        Assert.Equal(landCells, buckets.Sum(b => b.Cells));
        if (landCells > 0) Assert.Equal(1.0, buckets.Sum(b => b.ShareOfLand), 9);
    }

    [Fact]
    public void StateHash_SameSeedMatches_DifferentSeedDiffers()
    {
        var a = SimulationDiagnostics.StateHash(Run(42, 20));
        var b = SimulationDiagnostics.StateHash(Run(42, 20));
        var c = SimulationDiagnostics.StateHash(Run(43, 20));

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}
