using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Planet.Simulation.Tests;

/// <summary>
/// 水文地貌規劃 H2：岩性影響侵蝕、搬運容量在陸上堆出沖積地形、河口長出三角洲。
/// 數值要在 n=150 的劇本上校準，這裡只盯機制有沒有接上（CLAUDE.md 3.5）。
/// </summary>
public class LandformErosionTests
{
    private static readonly SimulationGrid Grid24 = SimulationGrid.Build(TestPlanet.RadiusMeters, 24);

    private static SimulationEngine Scenario() =>
        new(Grid24, 42, crustTransport: CrustTransport.PlateFrame, continentLayout: ContinentLayoutParameters.Default);

    private static SimulationEngine CopyOf(SimulationEngine engine)
    {
        using var stream = new MemoryStream();
        SimulationSnapshot.Write(engine, stream);
        stream.Position = 0;
        var copy = Scenario();
        SimulationSnapshot.Restore(copy, stream);
        return copy;
    }

    [Fact]
    public void HardRockErodesLessThanSoftRock()
    {
        var engine = Scenario();
        engine.Step(20);
        var soft = CopyOf(engine);
        var hard = CopyOf(engine);
        for (var c = 0; c < Grid24.CellCount; c++)
        {
            soft.State.SedimentThickness[c] = 0f;
            hard.State.SedimentThickness[c] = 0f;
            soft.State.CoverThickness[c] = 5_000f;
            soft.State.CoverRock[c] = CoverRock.Sedimentary;
            hard.State.CoverThickness[c] = 0f; // 大陸露出結晶基盤
        }

        soft.FluvialErosion!.Step(soft.State, soft.Hydrology!, soft.YearsPerStep);
        hard.FluvialErosion!.Step(hard.State, hard.Hydrology!, hard.YearsPerStep);

        Assert.True(hard.FluvialErosion.LastErodedVolumeKm3 < 0.6 * soft.FluvialErosion.LastErodedVolumeKm3,
            $"結晶岩刨了 {hard.FluvialErosion.LastErodedVolumeKm3:F0} km³、沉積岩 {soft.FluvialErosion.LastErodedVolumeKm3:F0} km³");
    }

    /// <summary>沖積扇、沖積平原、三角洲都要出現，而且侵蝕量仍然等於堆積量。</summary>
    [Fact]
    public void Evolution_BuildsAlluviumAndDeltas_AndConservesMass()
    {
        var engine = Scenario();
        var fluvial = engine.FluvialErosion!;
        double land = 0, delta = 0;
        for (var i = 0; i < 100; i++)
        {
            engine.Step(1);
            // 相對誤差：堆積分成陸上、三角洲、湖、海好幾條路徑加總，十幾萬立方公里時絕對誤差會到 1e-5
            Assert.True(Math.Abs(fluvial.LastErodedVolumeKm3 - fluvial.LastDepositedVolumeKm3) <= 1e-9 * fluvial.LastErodedVolumeKm3,
                $"侵蝕 {fluvial.LastErodedVolumeKm3} km³、堆積 {fluvial.LastDepositedVolumeKm3} km³");
            land += fluvial.LastLandDepositKm3;
            delta += fluvial.LastDeltaDepositKm3;
        }

        Assert.True(land > 0, "100 步都沒有在陸上堆積（搬運容量沒接上）");
        Assert.True(delta > 0, "100 步都沒有三角洲");
        var state = engine.State;
        Assert.Contains(Enumerable.Range(0, state.CellCount), c => state.AggradationRate[c] > 0f && state.WaterDepth[c] <= 0f);
        Assert.Contains(Enumerable.Range(0, state.CellCount), c => state.DeltaSupply[c] > 0f);
        Assert.Contains(Enumerable.Range(0, state.CellCount), c => state.IncisionRate[c] > 0f);
    }
}
