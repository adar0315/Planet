using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Planet.Simulation.Tests;

/// <summary>
/// v2 P3：板塊座標系接進真正的引擎（<see cref="CrustTransport.PlateFrame"/>）。
/// 驗收（docs/planet-evolution-v2-plan.md）：跑 1,000 步沒有 NaN、沒有板塊塌縮、邊界格比例穩定；
/// 另外存檔續跑要跟標記法一樣位元相同。
/// </summary>
public class PlateFrameTransportTests
{
    private static SimulationGrid Grid() => SimulationGrid.Build(TestPlanet.RadiusMeters, 24);

    private static SimulationEngine Engine(SimulationGrid grid) =>
        new(grid, 42, crustTransport: CrustTransport.PlateFrame);

    [Fact]
    public void ThousandSteps_NoNaN_NoCollapsedPlate_StableBoundaries()
    {
        var engine = Engine(Grid());
        var boundaryShares = new List<double>();

        for (var block = 0; block < 20; block++)
        {
            engine.Step(50);
            var s = engine.State;

            foreach (var (name, values) in new[]
            {
                ("BedrockElevation", s.BedrockElevation), ("CrustThickness", s.CrustThickness),
                ("CrustAge", s.CrustAge), ("CrustDensity", s.CrustDensity), ("WaterDepth", s.WaterDepth),
            })
            {
                Assert.True(values.All(float.IsFinite), $"第 {(block + 1) * 50} 步 {name} 出現非有限值");
            }

            var sizes = new int[engine.Plates.Plates.Length];
            foreach (var p in engine.Plates.OwnerByCell) sizes[p]++;
            Assert.True(sizes.All(n => n > 0), $"第 {(block + 1) * 50} 步有板塊塌縮成 0 格：{string.Join(",", sizes)}");

            boundaryShares.Add(s.Boundary.Count(b => b != BoundaryType.None) / (double)s.CellCount);
        }

        // 後 500 步的邊界格比例：不能一路漲或一路縮（取樣雜訊會讓「假邊界」越來越多）
        var late = boundaryShares.Skip(10).ToList();
        Assert.True(late.Max() / late.Min() < 1.5, $"邊界格比例在 {late.Min():P1}~{late.Max():P1} 之間擺盪");
        Assert.InRange(late.Average(), 0.02, 0.6);
    }

    [Fact]
    public void Step_SameSeedProducesIdenticalEvolution()
    {
        var grid = Grid();
        var a = Engine(grid);
        var b = Engine(grid);
        a.Step(150);
        b.Step(150);
        Assert.Equal(StateHash.Of(a), StateHash.Of(b));
    }

    [Fact]
    public void Snapshot_ResumeIsBitIdenticalToUninterruptedRun()
    {
        var grid = Grid();
        var uninterrupted = Engine(grid);
        uninterrupted.Step(317);

        using var stream = new MemoryStream();
        SimulationSnapshot.Write(uninterrupted, stream);
        stream.Position = 0;
        var resumed = Engine(grid);
        SimulationSnapshot.Restore(resumed, stream);

        for (var step = 1; step <= 200; step++)
        {
            uninterrupted.Step(1);
            resumed.Step(1);
            var diverged = StateHash.PerField(uninterrupted).Zip(StateHash.PerField(resumed))
                .Where(pair => pair.First.Hash != pair.Second.Hash).Select(pair => pair.First.Field).ToArray();
            Assert.True(diverged.Length == 0, $"讀檔後第 {step} 步開始分歧，欄位：{string.Join("、", diverged)}");
        }
    }

    [Fact]
    public void Snapshot_RefusesTheOtherTransport()
    {
        var grid = Grid();
        var frames = Engine(grid);
        frames.Step(5);
        using var stream = new MemoryStream();
        SimulationSnapshot.Write(frames, stream);
        stream.Position = 0;

        var markers = new SimulationEngine(grid, 42);
        Assert.Throws<InvalidDataException>(() => SimulationSnapshot.Restore(markers, stream));
    }

    [Fact]
    public void CrustAges()
    {
        // 引擎的老化排在回寫之後，板塊座標系每步重新讀——年齡要在座標系裡自己長，否則永遠是 0
        var engine = Engine(Grid());
        engine.Step(200); // 10 Myr
        var oceanAges = engine.State.CrustAge.Where((_, c) => engine.State.Crust[c] == CrustType.Oceanic).ToArray();
        Assert.True(oceanAges.Average() > 3, $"200 步（10 Myr）後海洋地殼平均年齡只有 {oceanAges.Average():F2} Myr");
    }

    [Fact]
    public void NewCrustAndConsumptionActuallyHappen()
    {
        // CLAUDE.md 3.5：機制要直接數觸發次數，不能只看結果
        var engine = Engine(Grid());
        int newCrust = 0, consumed = 0;
        for (var i = 0; i < 100; i++)
        {
            engine.Step(1);
            newCrust += engine.Tectonics.LastFrameNewCrustCells;
            consumed += engine.Tectonics.LastFrameConsumedPoints;
        }
        Assert.True(newCrust > 0, "100 步一格新海洋地殼都沒長出來——張裂沒接上");
        Assert.True(consumed > 0, "100 步一點地殼都沒被消耗——聚合沒接上");
    }
}
