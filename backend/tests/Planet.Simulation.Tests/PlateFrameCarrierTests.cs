using System;
using System.Linq;
using Planet.Simulation.Representation;
using Xunit;

namespace Planet.Simulation.Tests;

/// <summary>
/// v2 P2 選定的表示法（板塊座標系）的守門測試。數字的來源與完整評比見 docs/planet-evolution-v2-plan.md 的 P2 結果。
///
/// 守的三件事正是 v1 的標記法做不到的：純搬運不製造雜訊、來回之後回到原點、地質作用寫回不遺失。
/// 用 n=24、每步 50 萬年，100 步大約轉 5 格，夠讓大陸跨過很多格。
/// </summary>
public class PlateFrameCarrierTests
{
    private const int Steps = 100;
    private const double DeltaMyr = 0.5;

    private static (SimulationGrid Grid, PlateSet Plates, float[] Thickness, bool[] Continental) World()
    {
        var grid = SimulationGrid.Build(TestPlanet.RadiusMeters, 24);
        var engine = new SimulationEngine(grid, 42);
        engine.Step(200);
        var state = engine.State;
        return (grid, engine.Plates, (float[])state.CrustThickness.Clone(),
            state.Crust.Select(c => c == CrustType.Continental).ToArray());
    }

    [Fact]
    public void RigidRotation_MatchesExactSolutionEverywhere()
    {
        var (grid, plates, thickness, continental) = World();
        Assert.Contains(true, continental);

        var r = RepresentationBenchmark.Run(grid, plates, thickness, continental,
            [new PlateFrameCarrier()], Steps, DeltaMyr, singleRotation: true).Single();

        Assert.True(r.ExactMeanFlipsPerStep > 1, "大陸沒有移動，這個測試沒有意義");
        Assert.Equal(0, r.FinalMismatchCells);
        Assert.Equal(r.ExactMeanFlipsPerStep, r.MeanFlipsPerStep, 6);
        Assert.Equal(r.ExactMassChangeShare, r.MassChangeShare, 4);
    }

    [Fact]
    public void RoundTrip_ReturnsToTheStart()
    {
        var (grid, plates, thickness, continental) = World();

        var r = RepresentationBenchmark.Run(grid, plates, thickness, continental,
            [new PlateFrameCarrier()], Steps, DeltaMyr, singleRotation: true, reverseAtStep: Steps / 2).Single();

        Assert.True(r.FinalMismatchShare < 0.001, $"來回之後還有 {r.FinalMismatchCells} 格不一致");
        Assert.InRange(r.MassChangeShare, -0.001, 0.001);
    }

    [Fact]
    public void WriteBack_KeepsWhatGeologyAdded()
    {
        var (grid, plates, thickness, continental) = World();

        var r = RepresentationBenchmark.Run(grid, plates, thickness, continental,
            [new PlateFrameCarrier()], Steps, DeltaMyr, singleRotation: true, hotspotKmPerStep: 0.05f).Single();

        // n=24 的粗網格上混疊的點比較多，「拉差值」會把同一格的修改加給好幾個點：
        // P3 實測多出 2.94%、P4 改成「地殼在哪就歸誰」之後 6.4%。同樣的情境在 n=150 是 1.45%
        // （P2 評比 S4 在另一個起始狀態上是 0.36%），而且兩種解析度都沒有新增的坑。
        // 這裡守的是「沒有大量遺失或倍增」，不是 n=150 的精度。
        Assert.InRange(r.EditLossShare, -0.08, 0.08);
        Assert.True(r.FinalPits <= r.InitialPits + 5, $"坑從 {r.InitialPits} 變成 {r.FinalPits}：有些地方沒收到寫回");
    }

    [Fact]
    public void RealPlateMotion_InteriorStaysExact()
    {
        var (grid, plates, thickness, continental) = World();

        var r = RepresentationBenchmark.Run(grid, plates, thickness, continental,
            [new PlateFrameCarrier()], Steps, DeltaMyr).Single();

        Assert.True(r.MeanMismatchShare < 0.01, $"板塊內部平均 {r.MeanMismatchShare:P2} 跟精確解不一致");
        Assert.True(r.ZeroFlipStepShare < 0.5, $"{r.ZeroFlipStepShare:P0} 的步數一格都沒翻——批次化了");
    }
}
