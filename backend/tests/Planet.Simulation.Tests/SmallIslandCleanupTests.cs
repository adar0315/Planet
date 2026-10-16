using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Planet.Simulation.Tests;

/// <summary>
/// 匯出前的碎島清除（<see cref="SmallIslandCleanup"/>）的守門測試。
/// </summary>
public class SmallIslandCleanupTests
{
    private static readonly double TestRadiusMeters = TestPlanet.RadiusMeters;

    /// <summary>
    /// 粗網格測試用的面積門檻（平方公里）。
    ///
    /// n=24 一格就有 7,481 平方公里，比正式門檻 1,000 還大，換算出來的格數上限會是 0、
    /// 清除完全不執行。取 20,000（約2.7格）讓機制在這個解析度上跑得起來。
    /// **這是為了測得到機制本身，不是在測正式門檻**——正式門檻只在 n=150 有意義。
    /// </summary>
    private const double TestAreaLimitSquareKm = 20_000.0;

    private static SimulationEngine CreateEngine(int seed = 42) =>
        new(SimulationGrid.Build(TestRadiusMeters, 24), seed);

    /// <summary>陸塊連通塊的大小清單（由大到小）。</summary>
    private static List<int> LandMassSizes(SimulationEngine engine)
    {
        var grid = engine.Grid;
        var s = engine.State;
        var label = new int[s.CellCount];
        Array.Fill(label, -1);
        var sizes = new List<int>();
        var stack = new Stack<int>();

        for (var start = 0; start < s.CellCount; start++)
        {
            if (label[start] >= 0 || s.WaterDepth[start] > 0f) continue;
            var comp = sizes.Count;
            var size = 0;
            stack.Push(start);
            label[start] = comp;
            while (stack.Count > 0)
            {
                var cell = stack.Pop();
                size++;
                var b = cell * SimulationGrid.MaxNeighborsPerCell;
                for (var i = 0; i < SimulationGrid.MaxNeighborsPerCell; i++)
                {
                    var n = grid.Neighbors[b + i];
                    if (n == SimulationGrid.NoNeighbor) continue;
                    if (label[n] >= 0 || s.WaterDepth[n] > 0f) continue;
                    label[n] = comp;
                    stack.Push(n);
                }
            }
            sizes.Add(size);
        }

        sizes.Sort();
        sizes.Reverse();
        return sizes;
    }

    /// <summary>清除之後，孤立的陸塊數量要明顯下降。</summary>
    [Fact]
    public void Cleanup_ReducesTheNumberOfLandMasses()
    {
        var engine = CreateEngine();
        engine.Step(1200);

        var before = LandMassSizes(engine).Count;
        var report = engine.CleanUpSmallIslands(TestAreaLimitSquareKm);
        var after = LandMassSizes(engine).Count;

        Assert.True(report.SunkComponents > 0, "一塊都沒清掉——這條路沒有接上。");
        Assert.True(
            after < before,
            $"清除前 {before} 塊、清除後 {after} 塊，數量沒有下降。");
    }

    /// <summary>
    /// 大陸不可以被清掉。
    ///
    /// 面積門檻寫成平方公里而不是格數，所以這裡直接斷言「最大的陸塊格數不變」，
    /// 而不是斷言某個格數以下才會被清——後者在不同解析度上代表不同的東西。
    /// </summary>
    [Fact]
    public void Cleanup_LeavesTheContinentsAlone()
    {
        var engine = CreateEngine();
        engine.Step(1200);

        var before = LandMassSizes(engine);
        engine.CleanUpSmallIslands(TestAreaLimitSquareKm);
        var after = LandMassSizes(engine);

        Assert.NotEmpty(before);
        Assert.NotEmpty(after);
        Assert.Equal(before[0], after[0]);
    }

    /// <summary>
    /// **清完再清一次，第二次幾乎沒東西可清。**
    ///
    /// 這是最重要的一道：它驗證沉下去是**真的生效**了，不是只有回報。
    /// 2026-09-10 在 <see cref="SeaOutletBreach"/> 上踩過一模一樣的坑——
    /// 削地殼被 <see cref="Isostasy.MinCrustThicknessKm"/> 下限夾住時實際沒削夠，
    /// 機制卻照樣回報成功。如果 <c>Sink</c> 也失效，同一批島第二次會被原封不動再清一遍。
    /// </summary>
    [Fact]
    public void Cleanup_IsEffectivelyIdempotent()
    {
        var engine = CreateEngine();
        engine.Step(1200);

        var first = engine.CleanUpSmallIslands(TestAreaLimitSquareKm);
        var second = engine.CleanUpSmallIslands(TestAreaLimitSquareKm);

        Assert.True(first.SunkComponents > 0, "第一次就沒清到東西，這個測試沒有意義。");
        Assert.True(
            second.SunkComponents <= first.SunkComponents / 10,
            $"第一次清了 {first.SunkComponents} 塊，第二次又清了 {second.SunkComponents} 塊"
            + "——沉下去沒有真的生效，同一批島被重複清理。");
    }

    /// <summary>
    /// 夠高的小島要留著，不能因為小就一律沉掉。
    ///
    /// 那是夏威夷／冰島那一型的火山島。判準是高度而不是火山活動——
    /// 中洋脊沿線的臨時島嶼火山活動最旺，用火山當保留條件會把它們整條留下來
    /// （見 <see cref="SmallIslandCleanup"/> 的說明）。
    /// </summary>
    [Fact]
    public void Cleanup_KeepsTallIslets()
    {
        var engine = CreateEngine();
        engine.Step(1200);

        var report = engine.CleanUpSmallIslands(TestAreaLimitSquareKm);
        var s = engine.State;

        Assert.True(report.KeptTooHigh > 0, "沒有任何小島因為夠高而留下——高度門檻沒有作用。");

        // 清完之後不該還有「又小又矮」的孤立陸塊
        var sizes = LandMassSizes(engine);
        var tinyRemaining = sizes.Count(x => x == 1);
        var tallEnough = Enumerable.Range(0, s.CellCount)
            .Count(c => s.WaterDepth[c] <= 0f
                && s.SurfaceElevationAt(c) - s.SeaLevelMeters >= SmallIslandCleanup.MinKeptIsletHeightMeters);

        Assert.True(
            tinyRemaining == 0 || tallEnough > 0,
            "還剩下一格島，但沒有任何一格夠高——那些應該要被清掉才對。");
    }
}
