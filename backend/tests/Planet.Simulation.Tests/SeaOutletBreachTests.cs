using System;
using System.Linq;
using Xunit;

namespace Planet.Simulation.Tests;

/// <summary>
/// 被陸地封死的海會被鑿開出口（<see cref="SeaOutletBreach"/>）的守門測試。
/// </summary>
public class SeaOutletBreachTests
{
    private static readonly double TestRadiusMeters = TestPlanet.RadiusMeters;

    private static SimulationEngine CreateEngine(int seed = 42) =>
        new(SimulationGrid.Build(TestRadiusMeters, 24), seed);

    /// <summary>
    /// 跑久了之後，絕大多數的水必須仍然屬於同一個大洋。
    ///
    /// 這是這個機制存在的理由：板塊運動會不斷把海圍起來，沒有鑿出口的話水域會被切碎。
    /// 實測 n=150 跑到第1,250步，**關掉這個機制時主洋只剩水域的39%**
    /// （最大的「內海」有49,663格，跟主洋57,597格是同一個量級——等於兩個互不相通的大洋）。
    /// </summary>
    [Fact]
    public void Step_MostWaterStaysInOneConnectedOcean()
    {
        var engine = CreateEngine();
        engine.Step(1200);

        var report = OceanTopology.Analyze(engine.Grid, engine.State);
        var mainShare = (double)report.MainOceanCells / Math.Max(1, report.WaterCells);

        Assert.True(
            mainShare >= 0.90,
            $"主洋只佔水域的 {mainShare:P1}，水被切成 {report.WaterComponentCount} 塊互不相通的區域"
            + $"（最大的內海 {(report.EnclosedSeaSizes.Count > 0 ? report.EnclosedSeaSizes[0] : 0)} 格）。");
    }

    /// <summary>
    /// 機制真的有在動——不是「條件永遠不成立所以永遠不做事」。
    ///
    /// CLAUDE.md 3.5 與 3.8 各記過一次這種坑：機制看起來裝好了，實際上一次都沒觸發，
    /// 而**事後統計完全看不出來**，因為做成了跟沒事做的世界長得一樣。
    /// 所以要直接數觸發次數。
    /// </summary>
    [Fact]
    public void Step_BreachActuallyFires()
    {
        var engine = CreateEngine();

        var fired = 0;
        for (var i = 0; i < 60; i++)
        {
            engine.Step(20);
            fired += engine.Breach.LastBreachCount;
        }

        Assert.True(fired > 0, "跑了1,200步，鑿開出口的次數是 0——這條路沒有接上。");
    }

    /// <summary>
    /// 鑿出來的通道要真的通到水面以下，不可以「回報成功但實際沒削夠」。
    ///
    /// 2026-09-10 踩過：<c>Carve</c> 無條件把內部的地表高度設成目標值，
    /// 於是地殼厚度一撞到 <see cref="Isostasy.MinCrustThicknessKm"/> 下限、實際削不動時，
    /// 機制照樣以為自己鑿通了。症狀是每次都回報「鑿開了39~52個」而連通性毫無改善。
    /// 這裡容許少量（薄地殼的屏障確實削不動），但不能大面積失敗。
    /// </summary>
    [Fact]
    public void Step_CarveMostlyReachesItsTarget()
    {
        var engine = CreateEngine();

        var shortfall = 0;
        var breaches = 0;
        for (var i = 0; i < 60; i++)
        {
            engine.Step(20);
            shortfall += engine.Breach.LastCarveShortfallCells;
            breaches += engine.Breach.LastBreachCount;
        }

        Assert.True(breaches > 0, "沒有任何一次鑿通，這個測試沒有意義。");
        Assert.True(
            shortfall <= breaches * 3,
            $"{breaches} 次鑿通裡有 {shortfall} 格削不到目標深度——屏障多半是薄地殼，削不動。");
    }

    /// <summary>
    /// 一兩格的水窪是**湖**，不該被打通。
    ///
    /// 地球上到處都是內流湖（裏海、死海、鹹海），全部打通反而不像真的星球。
    /// 門檻寫成面積而不是格數（CLAUDE.md 第4條），所以這裡不能斷言「幾格以下不通」——
    /// n=24 一格就有7,481平方公里，本來就超過門檻。改成斷言仍然有孤立水域存在。
    /// </summary>
    [Fact]
    public void Step_SmallLakesSurvive()
    {
        var engine = CreateEngine();
        engine.Step(1200);

        var report = OceanTopology.Analyze(engine.Grid, engine.State);
        Assert.True(
            report.WaterComponentCount > 1,
            "所有水域都被打通成同一塊了——湖應該要留得住。");
    }
}
