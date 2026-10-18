using System;
using System.Linq;
using Xunit;

namespace Planet.Simulation.Tests;

/// <summary>
/// v2 P5 依連通性注水（<see cref="Hydrology"/>）。驗收（docs/planet-evolution-v2-plan.md）：
/// 主洋佔水域 &gt; 95%、湖泊存在且不會被打通、水量守恆。這裡用人工地形驗湖的三種結局與海平面求解，
/// 再用真的引擎驗守恆；主洋比例與湖的數量要在 n=150 的劇本上看（見規劃文件 P5 結果）。
/// </summary>
public class HydrologyTests
{
    private static readonly SimulationGrid Grid24 = SimulationGrid.Build(TestPlanet.RadiusMeters, 24);

    /// <summary>
    /// 人工地形：全球是 −3,000 m 的海，北極周圍 35° 內是 +1,000 m 的高原，高原中央 15° 內是往中心逐漸變深的盆地
    /// （中心 bowlFloor）。盆地的溢出口就是高原面 +1,000 m。
    /// </summary>
    private static float[] BowlTerrain(float bowlFloor)
    {
        var u = Grid24.UnitVector;
        var surface = new float[Grid24.CellCount];
        for (var c = 0; c < surface.Length; c++)
        {
            var fromPole = Math.Acos(Math.Clamp(u[c * 3 + 2], -1.0, 1.0)) * 180 / Math.PI;
            surface[c] = fromPole switch
            {
                < 15 => (float)(bowlFloor + (1000 - bowlFloor) * (fromPole / 15) * 0.9),
                < 35 => 1000f,
                _ => -3000f,
            };
        }
        return surface;
    }

    private static SimulationState Climate(float precipitation, float temperature)
    {
        var state = SimulationState.CreateInitialOceanWorld(Grid24, 3000f, 42);
        Array.Fill(state.Precipitation, precipitation);
        Array.Fill(state.Temperature, temperature);
        return state;
    }

    private static int BowlBasin(Hydrology h) =>
        Enumerable.Range(0, h.Basins.Count).OrderByDescending(b => h.Basins[b].Cells).First();

    [Fact]
    public void BasinBelowSeaLevel_SurroundedByLand_IsNotOcean()
    {
        // 盆地底 −500 m、海平面 0：低於海平面，但四周是 +1,000 m 的高原，通不到大洋
        var surface = BowlTerrain(-500f);
        var h = new Hydrology(Grid24);
        h.Flood(surface, 0f);
        var bowl = Enumerable.Range(0, surface.Length).Where(c => surface[c] < 0f && surface[c] > -1000f).ToList();
        Assert.NotEmpty(bowl);
        Assert.All(bowl, c => Assert.True(h.FloodLevel[c] >= 1000f, $"格 {c} 的泛洪高度 {h.FloodLevel[c]} 低於溢出口"));
    }

    [Fact]
    public void NoRain_LeavesDryBasin()
    {
        var surface = BowlTerrain(200f);
        var h = new Hydrology(Grid24);
        h.Flood(surface, 0f);
        var volume = h.SolveLakes(Climate(0f, 15f), surface, 0f);
        var bowl = h.Basins[BowlBasin(h)];
        Assert.Equal(LakeKind.Dry, bowl.Kind);
        Assert.Equal(0, bowl.LakeCells);
        Assert.Equal(0.0, volume);
    }

    [Fact]
    public void WetClimate_FillsToSpillAndOverflows()
    {
        var surface = BowlTerrain(200f);
        var h = new Hydrology(Grid24);
        h.Flood(surface, 0f);
        // 15°C、盆地裡的降水是全球平均的兩倍：湖面蒸發 ≈ 平均降水，淹進湖的每一格都是淨入水（E &lt; P(1−r)），一定灌滿
        var state = Climate(10f, 15f);
        var u = Grid24.UnitVector;
        for (var c = 0; c < state.CellCount; c++)
            if (Math.Acos(Math.Clamp(u[c * 3 + 2], -1.0, 1.0)) * 180 / Math.PI < 15) state.Precipitation[c] = 20f;
        var volume = h.SolveLakes(state, surface, 0f);
        var bowl = h.Basins[BowlBasin(h)];
        Assert.Equal(LakeKind.Fresh, bowl.Kind);
        Assert.Equal(bowl.Cells, bowl.LakeCells);
        Assert.Equal(1000f, bowl.LevelMeters);
        Assert.True(bowl.Outflow > 0, "灌滿之後沒有水溢出去");
        Assert.True(volume > 0);
    }

    [Fact]
    public void DryHotClimate_LeavesSalineLakeBelowSpill()
    {
        var surface = BowlTerrain(200f);
        var h = new Hydrology(Grid24);
        h.Flood(surface, 0f);
        // 35°C：湖面蒸發是平均降水的 2.5 倍，盆地的逕流只撐得起一部分湖面，停在溢出口以下
        h.SolveLakes(Climate(10f, 35f), surface, 0f);
        var bowl = h.Basins[BowlBasin(h)];
        Assert.Equal(LakeKind.Saline, bowl.Kind);
        Assert.InRange(bowl.LakeCells, 1, bowl.Cells - 1);
        Assert.True(bowl.LevelMeters < 1000f);
        Assert.Equal(0.0, bowl.Outflow);
    }

    [Fact]
    public void ShallowDepression_HoldsNoWater_WaterFlowsOn()
    {
        // 盆地底 +900 m、溢出口 +1,000 m：只有 100 m 深，當成已被河流切穿，再濕也不存水
        var surface = BowlTerrain(900f);
        var h = new Hydrology(Grid24);
        h.Flood(surface, 0f);
        var state = Climate(10f, 15f);
        var volume = h.SolveLakes(state, surface, 0f);
        Assert.Equal(0.0, volume);
        Assert.All(h.LakeOf, b => Assert.Equal(-1, b));
        h.ComputeDischarge(state, 0f);
        // 水沒有停在盆地裡，一路流到海：陸地上的最大流量至少是盆地的入水
        var bowl = h.Basins[BowlBasin(h)];
        Assert.True(h.Discharge.Max() >= bowl.Inflow * 0.999, $"最大流量 {h.Discharge.Max()} 小於盆地入水 {bowl.Inflow}");
    }

    [Fact]
    public void LakeOverflow_BecomesRiverDownstream()
    {
        var surface = BowlTerrain(200f);
        var h = new Hydrology(Grid24);
        h.Flood(surface, 0f);
        var state = Climate(10f, 15f);
        var u = Grid24.UnitVector;
        for (var c = 0; c < state.CellCount; c++)
            if (Math.Acos(Math.Clamp(u[c * 3 + 2], -1.0, 1.0)) * 180 / Math.PI < 15) state.Precipitation[c] = 20f;
        h.SolveLakes(state, surface, 0f);
        h.ComputeDischarge(state, 0f);
        var bowl = h.Basins[BowlBasin(h)];
        Assert.True(bowl.Outflow > 0);
        // 湖溢出來的水從溢出口往下游流：陸地上有一格的流量至少是溢出量；湖裡與海裡不算河
        Assert.True(h.Discharge.Max() >= bowl.Outflow * 0.999);
        for (var c = 0; c < surface.Length; c++)
            if (h.LakeOf[c] >= 0 || surface[c] < 0f) Assert.Equal(0f, h.Discharge[c]);
    }

    [Fact]
    public void SolveSeaLevel_GivesExactlyTheRequestedOceanVolume()
    {
        var surface = BowlTerrain(-500f);
        // 海底加一點起伏，體積對海平面才不是單純的直線
        for (var c = 0; c < surface.Length; c++) if (surface[c] <= -3000f) surface[c] -= c % 97;
        var h = new Hydrology(Grid24);
        h.Flood(surface, 0f);
        var area = Grid24.AverageCellAreaSquareMeters;
        foreach (var target in new[] { 1e17, 5e17, 2e18 })
        {
            var level = h.SolveSeaLevel(surface, target);
            double volume = 0;
            for (var c = 0; c < surface.Length; c++)
                if (h.FloodLevel[c] < level) volume += level - surface[c];
            volume *= area;
            Assert.InRange(volume / target, 1 - 1e-6, 1 + 1e-6);
        }
    }

    [Fact]
    public void Engine_PlateFrame_ConservesWaterAndOnlyConnectedWaterIsOcean()
    {
        var engine = new SimulationEngine(Grid24, 42, crustTransport: CrustTransport.PlateFrame);
        var area = Grid24.AverageCellAreaSquareMeters;
        double Total() => engine.State.WaterDepth.Sum(d => (double)d) * area + Cryosphere.LockedWaterVolume(engine.State, area);
        var before = Total();
        engine.Step(200);
        Assert.InRange(Total() / before, 1 - 1e-6, 1 + 1e-6);

        // 有水的格子要嘛通得到主洋（泛洪高度低於海平面），要嘛是湖
        var h = engine.Hydrology!;
        for (var c = 0; c < engine.State.CellCount; c++)
        {
            if (engine.State.WaterDepth[c] <= 0f) continue;
            Assert.True(h.FloodLevel[c] < engine.State.SeaLevelMeters || h.LakeOf[c] >= 0, $"格 {c} 有水卻既不是海也不是湖");
        }
    }

    [Fact]
    public void Engine_Markers_HasNoHydrology()
    {
        // v1（標記法）照舊用單一全球海平面，不走依連通性注水
        Assert.Null(new SimulationEngine(Grid24, 42).Hydrology);
    }
}
