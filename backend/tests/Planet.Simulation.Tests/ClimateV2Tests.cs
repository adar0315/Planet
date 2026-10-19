using System;
using System.Linq;
using Xunit;

namespace Planet.Simulation.Tests;

/// <summary>
/// v2 P6 氣候與冰（只在板塊座標系模式開）：冰蓋要看降雪（<see cref="Cryosphere.SnowLimited"/>）、
/// 海洋熱輸送（<see cref="Climate.OceanHeatTransport"/>）。驗收的「海平面沒有週期性暴漲暴跌、冰帽面積與地球同量級」
/// 要在 n=150 的劇本上看（見規劃文件 P6 結果），這裡只盯機制本身。
/// </summary>
public class ClimateV2Tests
{
    private static readonly SimulationGrid Grid24 = SimulationGrid.Build(TestPlanet.RadiusMeters, 24);

    /// <summary>全球都是露出水面、−20°C 的陸地（冰蓋有資格長）。</summary>
    private static SimulationState ColdLand(float precipitation)
    {
        var state = SimulationState.CreateInitialOceanWorld(Grid24, 3000f, 42);
        Array.Fill(state.WaterDepth, 0f);
        Array.Fill(state.Temperature, -20f);
        Array.Fill(state.Precipitation, precipitation);
        return state;
    }

    [Fact]
    public void SnowLimitedIce_DryColdLandGrowsNoIce()
    {
        // 又冷又乾：全球降水 0，沒有雪就沒有冰（原本只看氣溫，乾冷沙漠也長幾公里厚的冰）
        var state = ColdLand(0f);
        var ice = new Cryosphere { SnowLimited = true };
        for (var i = 0; i < 10; i++) ice.Step(state, 1e30, Grid24.AverageCellAreaSquareMeters);
        Assert.All(state.IceThickness, t => Assert.Equal(0f, t));
    }

    [Fact]
    public void SnowLimitedIce_SnowyColdLandGrowsIce_DryCornerStaysThin()
    {
        var state = ColdLand(10f);
        // 一半的格子幾乎不下雪
        for (var c = 0; c < state.CellCount; c += 2) state.Precipitation[c] = 0.1f;
        var ice = new Cryosphere { SnowLimited = true };
        for (var i = 0; i < 20; i++) ice.Step(state, 1e30, Grid24.AverageCellAreaSquareMeters);
        var snowy = Enumerable.Range(0, state.CellCount).Where(c => c % 2 == 1).Average(c => state.IceThickness[c]);
        var dry = Enumerable.Range(0, state.CellCount).Where(c => c % 2 == 0).Average(c => state.IceThickness[c]);
        Assert.True(snowy > 500f, $"有雪的地方冰只有 {snowy} 公尺");
        Assert.True(dry < snowy * 0.1f, $"幾乎不下雪的地方冰 {dry} 公尺，有雪的地方 {snowy} 公尺");
    }

    [Fact]
    public void Markers_IceStillTemperatureOnly()
    {
        // 標記法（v1）照舊只看氣溫：沒有降水也長冰
        var state = ColdLand(0f);
        var ice = new Cryosphere();
        ice.Step(state, 1e30, Grid24.AverageCellAreaSquareMeters);
        Assert.Contains(state.IceThickness, t => t > 0f);
    }

    [Fact]
    public void OceanHeatTransport_WarmsMidLatitudes_CoolsEquator()
    {
        // 全球是海：有熱輸送時赤道比沒有時涼、45° 比沒有時暖
        var state = SimulationState.CreateInitialOceanWorld(Grid24, 3000f, 42);
        Array.Fill(state.WaterDepth, 3000f);
        var plain = new Climate(Grid24);
        plain.UpdateTemperatureOnly(state);
        var before = (float[])state.Temperature.Clone();
        var transport = new Climate(Grid24) { OceanHeatTransport = true };
        transport.UpdateTemperatureOnly(state);

        double Band(float[] t, double lo, double hi) => Enumerable.Range(0, state.CellCount)
            .Where(c => Math.Abs(Grid24.Latitude[c]) >= lo && Math.Abs(Grid24.Latitude[c]) < hi).Average(c => t[c]);
        Assert.True(Band(state.Temperature, 0, 10) < Band(before, 0, 10), "有熱輸送時赤道沒有比較涼");
        Assert.True(Band(state.Temperature, 40, 50) > Band(before, 40, 50) + 3, "有熱輸送時 45° 沒有暖 3°C 以上");
    }

    [Fact]
    public void Engine_PlateFrameOnly_TurnsOnV2Climate()
    {
        var v2 = new SimulationEngine(Grid24, 42, crustTransport: CrustTransport.PlateFrame);
        Assert.False(v2.StopCriteria.InlandWaterIsCoast);
        var v1 = new SimulationEngine(Grid24, 42);
        Assert.True(v1.StopCriteria.InlandWaterIsCoast);
        Assert.All(v1.State.StructuralRelief, r => Assert.Equal(0f, r));
    }
}
