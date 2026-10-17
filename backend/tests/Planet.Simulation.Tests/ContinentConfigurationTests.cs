using System;
using Xunit;

namespace Planet.Simulation.Tests;

/// <summary>
/// 停止條件「陸地集中成兩大塊＋至少兩個大島」的守門測試。
///
/// 用人工畫出來的陸地測，不用真的演化：在網格上畫球冠當陸地，球冠面積正比於 1−cos(半徑)，
/// 比例可以精確控制。拿真的演化去碰運氣的話，測試只會告訴你「這顆星球剛好怎樣」。
/// </summary>
public class ContinentConfigurationTests
{
    private static readonly SimulationGrid Grid = SimulationGrid.Build(TestPlanet.RadiusMeters, 24);

    /// <summary>全部是海，再把指定的球冠標成陸地。</summary>
    private static SimulationState Planet(params (double Lat, double Lon, double RadiusDeg)[] caps)
    {
        var state = new SimulationState(Grid.CellCount);
        Array.Fill(state.WaterDepth, 1000f);

        foreach (var (lat, lon, radiusDeg) in caps)
        {
            var la = lat * Math.PI / 180;
            var lo = lon * Math.PI / 180;
            // 跟網格的單位向量同一個座標系（Z 為極軸）
            var cx = Math.Cos(la) * Math.Cos(lo);
            var cy = Math.Cos(la) * Math.Sin(lo);
            var cz = Math.Sin(la);
            var minDot = Math.Cos(radiusDeg * Math.PI / 180);
            for (var cell = 0; cell < Grid.CellCount; cell++)
            {
                var dot = Grid.UnitVector[cell * 3] * cx + Grid.UnitVector[cell * 3 + 1] * cy
                    + Grid.UnitVector[cell * 3 + 2] * cz;
                if (dot >= minDot) state.WaterDepth[cell] = 0f;
            }
        }

        return state;
    }

    [Fact]
    public void TwoContinentsAndTwoBigIslands_IsMet()
    {
        // 球冠 40° 與 30°（約 60% / 35%），加上兩個 8° 的島（各約 2.5%）
        var state = Planet((30, 0, 40), (-30, 150, 30), (0, -100, 8), (-60, -40, 8));
        var r = ContinentConfiguration.Analyze(Grid, state);

        Assert.True(r.IsMet, $"兩塊 {r.LargestShare:P1} / {r.SecondShare:P1}、大島 {r.BigIslandCount}");
        Assert.Equal(2, r.BigIslandCount);
        Assert.InRange(r.LargestShare, 0.55, 0.66);
        Assert.InRange(r.SecondShare, 0.30, 0.40);
        Assert.InRange(r.LargestRaggedness, 0.8, 2.0); // 球冠接近圓盤
    }

    /// <summary>
    /// 面積比例都對、但大陸是破的（v1 第 1,160 步的實況：內部滿是湖洞與胡椒狀的海洋地殼）——不能算成立。
    /// </summary>
    [Fact]
    public void ProportionsRightButContinentFullOfHoles_IsNotMet()
    {
        var state = Planet((30, 0, 40), (-30, 150, 30), (0, -100, 8), (-60, -40, 8));
        var largestBefore = ContinentConfiguration.Analyze(Grid, state).LargestRaggedness;

        // 在最大那塊（北緯30、經度0、半徑40°）的內部每隔幾格打一個洞
        var cos35 = Math.Cos(35 * Math.PI / 180);
        var cx = Math.Cos(Math.PI / 6);
        var cz = Math.Sin(Math.PI / 6);
        for (var cell = 0; cell < Grid.CellCount; cell += 5)
        {
            var dot = Grid.UnitVector[cell * 3] * cx + Grid.UnitVector[cell * 3 + 2] * cz;
            if (dot >= cos35) state.WaterDepth[cell] = 50f;
        }

        var r = ContinentConfiguration.Analyze(Grid, state);

        Assert.True(r.TwoLargestShare >= 0.75 && r.BigIslandCount >= 2, "比例條件本身要成立，這個測試才有意義");
        Assert.False(r.IsMet, $"破碎度 {r.LargestRaggedness:F2}（打洞前 {largestBefore:F2}）");
        Assert.True(r.LargestRaggedness > ContinentCriteria.Default.MaxRaggedness);
    }

    [Fact]
    public void OneSupercontinent_IsNotMet()
    {
        // 一塊超大陸＋一塊小陸地：合計夠了，但第二塊不到 20%
        var state = Planet((30, 0, 55), (-30, 150, 15), (0, -100, 8), (-60, -40, 8));
        var r = ContinentConfiguration.Analyze(Grid, state);

        Assert.False(r.IsMet);
        Assert.True(r.SecondShare < 0.20, $"第二塊 {r.SecondShare:P1}");
    }

    [Fact]
    public void TwoContinentsButOnlyOneBigIsland_IsNotMet()
    {
        var state = Planet((30, 0, 40), (-30, 150, 30), (0, -100, 8));
        var r = ContinentConfiguration.Analyze(Grid, state);

        Assert.False(r.IsMet);
        Assert.Equal(1, r.BigIslandCount);
    }

    [Fact]
    public void ScatteredContinents_IsNotMet()
    {
        // 四塊差不多大的陸地：最大兩塊合計只有一半左右
        var state = Planet((40, 0, 25), (-40, 90, 25), (40, 180, 25), (-40, -90, 25));
        var r = ContinentConfiguration.Analyze(Grid, state);

        Assert.False(r.IsMet);
        Assert.True(r.TwoLargestShare < 0.75, $"最大兩塊合計 {r.TwoLargestShare:P1}");
    }

    [Fact]
    public void SpeckIslands_AreExcludedFromTheShares()
    {
        // 在符合條件的配置上撒一堆單格碎島。n=24 一格就有 7,481 km²，比預設門檻大，
        // 所以用自訂門檻（約 2.7 格）讓單格碎島被排除。
        var state = Planet((30, 0, 40), (-30, 150, 30), (0, -100, 8), (-60, -40, 8));
        var specks = 0;
        for (var cell = 0; cell < Grid.CellCount && specks < 40; cell += 97)
        {
            if (state.WaterDepth[cell] <= 0f) continue;
            var isolated = true;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                var n = Grid.Neighbors[cell * SimulationGrid.MaxNeighborsPerCell + s];
                if (n != SimulationGrid.NoNeighbor && state.WaterDepth[n] <= 0f) isolated = false;
            }
            if (!isolated) continue;
            state.WaterDepth[cell] = 0f;
            specks++;
        }

        var criteria = ContinentCriteria.Default with { MinLandMassAreaSquareKm = 20_000 };
        var r = ContinentConfiguration.Analyze(Grid, state, criteria);

        Assert.True(specks > 10, "沒撒到碎島，這個測試沒有意義");
        Assert.True(r.IsMet);
        Assert.Equal(4, r.LandMassCount); // 碎島不算
    }

    /// <summary>
    /// 停止條件是**邊緣觸發**：成立的那一刻觸發一次；持續成立時不再觸發。
    /// 否則使用者看完按繼續，下一次檢查又會立刻停下來，永遠走不出去。
    /// </summary>
    [Fact]
    public void Engine_StopsOnceWhenConditionBecomesTrue_ThenKeepsGoing()
    {
        var engine = new SimulationEngine(Grid, 42)
        {
            // 門檻全部歸零：第一次檢查就一定成立
            StopCriteria = new ContinentCriteria(0, 0, 0, 0, 1, 0, double.MaxValue),
        };

        var first = engine.StepUntilStopCondition(100);
        Assert.True(engine.StopConditionTriggered);
        Assert.Equal(20, first); // 每 100 萬年（20 步）檢查一次

        var second = engine.StepUntilStopCondition(100);
        Assert.False(engine.StopConditionTriggered);
        Assert.Equal(100, second);
    }
}
