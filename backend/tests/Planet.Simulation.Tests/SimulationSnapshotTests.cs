using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Planet.Simulation.Tests;

/// <summary>
/// 存檔／讀檔（<see cref="SimulationSnapshot"/>）的守門測試。
/// </summary>
public class SimulationSnapshotTests
{
    private const double TestRadiusMeters = 1_852_259.92;

    private static SimulationGrid BuildGrid() => SimulationGrid.Build(TestRadiusMeters, 24);

    /// <summary>存了再讀，格子狀態要一模一樣。</summary>
    [Fact]
    public void RoundTrip_RestoresTheGridStateExactly()
    {
        var grid = BuildGrid();
        var source = new SimulationEngine(grid, 42);
        source.Step(300);

        using var stream = new MemoryStream();
        SimulationSnapshot.Write(source, stream);
        stream.Position = 0;

        var target = new SimulationEngine(grid, 42); // 全新的、還沒跑過
        SimulationSnapshot.Restore(target, stream);

        var a = source.State;
        var b = target.State;

        Assert.Equal(a.StepCount, b.StepCount);
        Assert.Equal(a.ElapsedYears, b.ElapsedYears);
        Assert.Equal(a.SeaLevelMeters, b.SeaLevelMeters);
        Assert.Equal(a.BedrockElevation, b.BedrockElevation);
        Assert.Equal(a.CrustThickness, b.CrustThickness);
        Assert.Equal(a.CrustAge, b.CrustAge);
        Assert.Equal(a.CrustDensity, b.CrustDensity);
        Assert.Equal(a.Crust, b.Crust);
        Assert.Equal(a.WaterDepth, b.WaterDepth);
        Assert.Equal(a.IceThickness, b.IceThickness);
        Assert.Equal(a.SedimentThickness, b.SedimentThickness);
        Assert.Equal(a.OrganicThickness, b.OrganicThickness);
        Assert.Equal(a.Temperature, b.Temperature);
        Assert.Equal(a.OceanicOnly, b.OceanicOnly);
    }

    /// <summary>
    /// 板塊也要一模一樣——歐拉極與角速度每步都被力矩平衡改寫，不能靠種子重新生成。
    /// </summary>
    [Fact]
    public void RoundTrip_RestoresThePlates()
    {
        var grid = BuildGrid();
        var source = new SimulationEngine(grid, 42);
        source.Step(300);

        using var stream = new MemoryStream();
        SimulationSnapshot.Write(source, stream);
        stream.Position = 0;

        var target = new SimulationEngine(grid, 42);
        SimulationSnapshot.Restore(target, stream);

        Assert.Equal(source.Plates.Plates, target.Plates.Plates);
        Assert.Equal(source.Plates.PlateIdByCell, target.Plates.PlateIdByCell);
    }

    /// <summary>讀回來之後要能繼續推進，而且不會爆掉或跑出非有限值。</summary>
    [Fact]
    public void RoundTrip_CanKeepStepping()
    {
        var grid = BuildGrid();
        var source = new SimulationEngine(grid, 42);
        source.Step(300);

        using var stream = new MemoryStream();
        SimulationSnapshot.Write(source, stream);
        stream.Position = 0;

        var target = new SimulationEngine(grid, 42);
        SimulationSnapshot.Restore(target, stream);
        target.Step(100);

        Assert.Equal(400, target.State.StepCount);
        Assert.True(float.IsFinite(target.State.SeaLevelMeters));
        Assert.All(target.Plates.Plates, p =>
            Assert.True(double.IsFinite(p.AngularVelocityDegPerMyr)));
        Assert.True(
            target.LandFraction() is > 0 and < 1,
            $"讀檔續跑之後陸地比例是 {target.LandFraction()}，不合理。");
    }

    /// <summary>
    /// 網格對不上就要拒絕，不能硬讀。
    ///
    /// 存檔是跟特定網格綁定的（每格一個元素、順序就是格子索引），
    /// 套到不同解析度上只會得到一堆錯位的資料，而**二進位讀錯不會拋例外**，
    /// 只會安靜地變成一顆亂掉的星球。
    /// </summary>
    [Fact]
    public void Restore_RejectsAMismatchedGrid()
    {
        var source = new SimulationEngine(BuildGrid(), 42);
        source.Step(10);

        using var stream = new MemoryStream();
        SimulationSnapshot.Write(source, stream);
        stream.Position = 0;

        var otherGrid = SimulationGrid.Build(TestRadiusMeters, 12);
        var target = new SimulationEngine(otherGrid, 42);

        var ex = Assert.Throws<InvalidDataException>(() => SimulationSnapshot.Restore(target, stream));
        Assert.Contains("套不過來", ex.Message);
    }

    /// <summary>不是存檔的內容要拒絕，而不是讀出垃圾。</summary>
    [Fact]
    public void Restore_RejectsGarbage()
    {
        var grid = BuildGrid();
        var target = new SimulationEngine(grid, 42);
        using var stream = new MemoryStream(new byte[256]);

        Assert.Throws<InvalidDataException>(() => SimulationSnapshot.Restore(target, stream));
    }
}
