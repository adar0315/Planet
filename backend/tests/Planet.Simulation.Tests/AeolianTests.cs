using System;
using System.Linq;
using Xunit;

namespace Planet.Simulation.Tests;

/// <summary>水文地貌規劃 H5：乾燥地區的風蝕、下風處的黃土，物質守恆。</summary>
public class AeolianTests
{
    [Fact]
    public void Wind_BlowsDustFromDryLandToWetterLand_AndConservesMass()
    {
        var grid = SimulationGrid.Build(TestPlanet.RadiusMeters, 24);
        var engine = new SimulationEngine(grid, 42, crustTransport: CrustTransport.PlateFrame, continentLayout: ContinentLayoutParameters.Default);
        var aeolian = engine.Aeolian!;
        var deflated = 0.0;
        for (var i = 0; i < 120; i++)
        {
            engine.Step(1);
            deflated += aeolian.LastDeflatedKm3;
            Assert.True(Math.Abs(aeolian.LastDeflatedKm3 - aeolian.LastDustDepositedKm3) <= 1e-9 * Math.Max(1, aeolian.LastDeflatedKm3),
                $"吹走 {aeolian.LastDeflatedKm3} km³、落下 {aeolian.LastDustDepositedKm3} km³");
        }

        Assert.True(deflated > 0, "120 步都沒有風蝕");
        var state = engine.State;
        var mean = state.Precipitation.Average();
        var loess = Enumerable.Range(0, state.CellCount).Where(c => state.LoessRate[c] >= 2f && state.WaterDepth[c] <= 0f).ToList(); // 跟放大時畫成黃土的門檻一樣
        Assert.NotEmpty(loess);
        var deflation = Enumerable.Range(0, state.CellCount).Where(c => state.DeflationRate[c] > 0f).ToList();
        Assert.NotEmpty(deflation);
        // 吹蝕的地方比落黃土的地方乾
        Assert.True(deflation.Average(c => state.Precipitation[c]) < loess.Average(c => state.Precipitation[c]),
            $"風蝕區平均降水 {deflation.Average(c => state.Precipitation[c]):F2}、黃土區 {loess.Average(c => state.Precipitation[c]):F2}（全球 {mean:F2}）");
    }
}
