using System;
using System.Linq;
using Xunit;

namespace Planet.Simulation.Tests;

/// <summary>
/// v2 P7 外營力與陸地降水（只在板塊座標系模式開）：地形抬升從水面算起、基礎降水依距離（<see cref="Climate"/>）、
/// 沿 P5 流向的侵蝕與沉積（<see cref="FluvialErosion"/>）。陸地高度、最高峰、停止條件的校準要在 n=150 的劇本上看
/// （見規劃文件 P7 結果），這裡只盯機制本身。
/// </summary>
public class ErosionV2Tests
{
    private static readonly SimulationGrid Grid24 = SimulationGrid.Build(TestPlanet.RadiusMeters, 24);

    /// <summary>經度 0~90、緯度 ±60 以內是 500 公尺高的陸地，其餘是 4,000 公尺深的海；海平面 0。</summary>
    private static SimulationState OneContinent()
    {
        var state = SimulationState.CreateInitialOceanWorld(Grid24, 3000f, 42);
        state.SeaLevelMeters = 0f;
        for (var c = 0; c < state.CellCount; c++)
        {
            state.SedimentThickness[c] = 0f;
            state.OrganicThickness[c] = 0f;
            state.VegetationThickness[c] = 0f;
            state.IceThickness[c] = 0f;
            state.StructuralRelief[c] = 0f;
            var land = Grid24.Longitude[c] is >= 0 and <= 90 && Math.Abs(Grid24.Latitude[c]) <= 60;
            state.BedrockElevation[c] = land ? 500f : -4000f;
            state.WaterDepth[c] = land ? 0f : 4000f;
        }
        return state;
    }

    private static double LandPrecipitationRatio(SimulationState state)
    {
        var land = Enumerable.Range(0, state.CellCount).Where(c => state.WaterDepth[c] <= 0f).Average(c => state.Precipitation[c]);
        return land / state.Precipitation.Average();
    }

    [Fact]
    public void OrographyAboveWater_AirReachesLand()
    {
        // 原本用水底的地表：海上的空氣「從 −4 公里的海床爬上大陸」，靠岸就把水氣降光，陸地幾乎沒有雨
        var state = OneContinent();
        new Climate(Grid24).Solve(state);
        var v1 = LandPrecipitationRatio(state);

        state = OneContinent();
        new Climate(Grid24) { OrographyAboveWater = true, BaselineRainReferenceKm = 200 }.Solve(state);
        var v2 = LandPrecipitationRatio(state);

        Assert.True(v2 > 0.2, $"v2 的陸地降水只有全球平均的 {v2:F2}（v1 {v1:F2}）");
        Assert.True(v1 < 0.1, $"v1 的陸地降水 {v1:F2}——測試的地形沒有重現「靠岸就降光」");
        Assert.True(v2 > 3 * v1, $"v2 陸地降水 {v2:F2}、v1 {v1:F2}，沒有明顯變多");
    }

    [Fact]
    public void PlateFrame_ErosionEqualsDeposition()
    {
        var engine = new SimulationEngine(Grid24, 42, crustTransport: CrustTransport.PlateFrame);
        var fluvial = engine.FluvialErosion!;
        var eroded = 0.0;
        for (var i = 0; i < 30; i++)
        {
            engine.Step(1);
            eroded += fluvial.LastErodedVolumeKm3;
            Assert.Equal(fluvial.LastErodedVolumeKm3, fluvial.LastDepositedVolumeKm3, 6);
        }
        Assert.True(eroded > 0, "30 步都沒有侵蝕");
    }

    [Fact]
    public void PlateFrame_NoSedimentDumpedInPlace()
    {
        // 沉積物不會自己長出海面：第一版的「就地堆」「平均鋪」把整條河的泥沙倒在一格，堆出幾十公里的沉積柱
        var engine = new SimulationEngine(Grid24, 42, crustTransport: CrustTransport.PlateFrame);
        engine.Step(40);
        var fallback = engine.FluvialErosion!.FallbackDeposits;
        Assert.True(fallback[1] + fallback[2] < 1.0, $"平均鋪 {fallback[1]:F0}、就地堆 {fallback[2]:F0}（公尺×格）");
    }

    [Fact]
    public void Engine_PlateFrameOnly_TurnsOnV2Erosion()
    {
        var v2 = new SimulationEngine(Grid24, 42, crustTransport: CrustTransport.PlateFrame);
        Assert.NotNull(v2.FluvialErosion);
        Assert.Equal(0.008, v2.StopCriteria.MinBigIslandShare);
        var v1 = new SimulationEngine(Grid24, 42);
        Assert.Null(v1.FluvialErosion);
        Assert.Equal(ContinentCriteria.Default.MinBigIslandShare, v1.StopCriteria.MinBigIslandShare);
    }
}
