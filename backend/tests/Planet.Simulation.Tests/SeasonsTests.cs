using System;
using System.Linq;
using Xunit;

namespace Planet.Simulation.Tests;

/// <summary>
/// 水文地貌規劃 H3、H4：12 個月的氣候與四季的水文。全年的總量要跟年平均一致，四季只改變「什麼時候」。
/// </summary>
public class SeasonsTests
{
    private static readonly Lazy<SimulationEngine> World = new(() =>
    {
        var grid = SimulationGrid.Build(TestPlanet.RadiusMeters, 24);
        var engine = new SimulationEngine(grid, 42, crustTransport: CrustTransport.PlateFrame, continentLayout: ContinentLayoutParameters.Default);
        engine.Step(60); // 跨過第 40 步那一次重解氣候
        return engine;
    });

    [Fact]
    public void Declination_MatchesTheFrontendFormula()
    {
        // 北半球夏至前後最大、冬至前後最小，跟 frontend/src/map/sunPosition.ts 同一個式子
        Assert.InRange(Climate.SolarDeclinationDegrees(171), 23.3, 23.44);
        Assert.InRange(Climate.SolarDeclinationDegrees(354), -23.44, -23.3);
        Assert.Equal(0.0, Climate.DailyInsolation(80, -23), 6); // 極夜
    }

    [Fact]
    public void SolveSeasons_KeepsAnnualMeans_AndLeavesTheStateUntouched()
    {
        var engine = World.Value;
        var state = engine.State;
        var before = StateHash.Of(engine);

        var (climate, _) = engine.SolveSeasons(12);

        Assert.Equal(before, StateHash.Of(engine));
        for (var c = 0; c < state.CellCount; c += 7)
        {
            var meanT = Enumerable.Range(0, 12).Average(m => climate.Temperature[m][c]);
            var meanP = Enumerable.Range(0, 12).Average(m => climate.Precipitation[m][c]);
            Assert.Equal(state.Temperature[c], meanT, 2);
            Assert.Equal(state.Precipitation[c], meanP, 3);
        }
    }

    [Fact]
    public void Seasons_AreOppositeInTheTwoHemispheres_AndStrongerInland()
    {
        var engine = World.Value;
        var state = engine.State;
        var grid = engine.Grid;
        var (climate, _) = engine.SolveSeasons(12);
        double Swing(int c) => climate.Temperature[6][c] - climate.Temperature[0][c]; // 7 月 − 1 月

        var northLand = Enumerable.Range(0, state.CellCount).Where(c => grid.Latitude[c] is > 40 and < 60 && state.WaterDepth[c] <= 0f).ToList();
        var southLand = Enumerable.Range(0, state.CellCount).Where(c => grid.Latitude[c] is < -40 and > -60 && state.WaterDepth[c] <= 0f).ToList();
        var northSea = Enumerable.Range(0, state.CellCount).Where(c => grid.Latitude[c] is > 40 and < 60 && state.WaterDepth[c] > 0f).ToList();
        Assert.NotEmpty(northLand);
        Assert.NotEmpty(southLand);
        Assert.NotEmpty(northSea);

        Assert.True(northLand.Average(Swing) > 10, $"北緯 40~60 陸地 7 月比 1 月只暖 {northLand.Average(Swing):F1}°C");
        Assert.True(southLand.Average(Swing) < -10, $"南緯 40~60 陸地 7 月比 1 月 {southLand.Average(Swing):F1}°C");
        Assert.True(northSea.Average(Swing) < northLand.Average(Swing) / 2, "海的季節變化應該比陸地小得多");
    }

    [Fact]
    public void SeasonalHydrology_ClassifiesRivers_AndNeverCreatesWater()
    {
        var engine = World.Value;
        var state = engine.State;
        var discharge = engine.Hydrology!.Discharge;
        var (_, hydrology) = engine.SolveSeasons(12);

        // 年平均的總量只會因為河道損失而變少，不會憑空變多
        for (var c = 0; c < state.CellCount; c++)
            Assert.True(hydrology.MeanDischarge[c] <= discharge[c] * 1.001 + 1e-3,
                $"格 {c}：四季平均 {hydrology.MeanDischarge[c]}，年平均水系 {discharge[c]}");

        var min = Rivers.MinDischarge(engine.Grid, state);
        var rivers = Enumerable.Range(0, state.CellCount).Where(c => discharge[c] >= min && state.WaterDepth[c] <= 0f).ToList();
        Assert.NotEmpty(rivers);
        Assert.Contains(rivers, c => hydrology.Regime[c] == FlowRegime.Perennial);
        Assert.True(rivers.Average(c => hydrology.BaseflowShare[c]) > 0.05, "大河幾乎沒有地下水基流");
        Assert.All(rivers, c => Assert.InRange(hydrology.FloodFactor[c], 1f, 3f));
    }

    [Fact]
    public void Winter_HasSnowAndIce_AtHighLatitudes()
    {
        var engine = World.Value;
        var state = engine.State;
        var grid = engine.Grid;
        var (_, hydrology) = engine.SolveSeasons(12);
        var northLand = Enumerable.Range(0, state.CellCount).Where(c => grid.Latitude[c] > 50 && state.WaterDepth[c] <= 0f).ToList();
        Assert.NotEmpty(northLand);

        var januarySnow = northLand.Count(c => hydrology.SnowCovered[0][c]);
        var julySnow = northLand.Count(c => hydrology.SnowCovered[6][c]);
        Assert.True(januarySnow > julySnow, $"北緯 50 度以上的陸地：1 月積雪 {januarySnow} 格、7 月 {julySnow} 格");
    }

    [Fact]
    public void Engine_UpdatesFloodFactorWhenClimateIsSolved()
    {
        var state = World.Value.State;
        Assert.Contains(Enumerable.Range(0, state.CellCount), c => state.FloodFactor[c] > 1.05f);
    }
}
