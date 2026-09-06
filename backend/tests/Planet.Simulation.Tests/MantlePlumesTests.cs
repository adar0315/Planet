using Planet.Simulation;
using Xunit;

namespace Planet.Simulation.Tests;

/// <summary>
/// 地函熱柱（板塊內部火山）的測試。
///
/// 這裡守的重點是**熱點固定在地函、板塊在上面滑過去**——那是它跟另外兩種火山
/// （中洋脊、島弧，兩者都綁在板塊邊界上）最根本的差別，也是夏威夷式島鏈的成因。
/// 如果哪天有人「順手」把熱點改成跟著板塊移動，火山島鏈會安靜地消失、
/// 變成單一一座永遠長在同一格上的山，而其他測試都還會通過。
/// </summary>
public class MantlePlumesTests
{
    private const double TestRadiusMeters = 1_852_259.92;

    private static SimulationGrid CreateGrid() => SimulationGrid.Build(TestRadiusMeters, 24);

    [Fact]
    public void Step_ThickensCrustAboveThePlume()
    {
        var grid = CreateGrid();
        var state = SimulationState.CreateInitialOceanWorld(grid);
        var plumes = new MantlePlumes(seed: 42);

        var before = (float[])state.CrustThickness.Clone();
        plumes.Step(grid, state, 1.0);

        var thickened = Enumerable.Range(0, state.CellCount)
            .Count(cell => state.CrustThickness[cell] > before[cell]);
        Assert.True(thickened > 0, "熱柱一步下來完全沒有堆積任何地殼");
    }

    [Fact]
    public void Step_RecordsVolcanicActivitySoItCanBeRendered()
    {
        var grid = CreateGrid();
        var state = SimulationState.CreateInitialOceanWorld(grid);
        var plumes = new MantlePlumes(seed: 42);

        plumes.Step(grid, state, 1.0);

        Assert.Contains(state.VolcanicActivity, activity => activity > 0f);
        Assert.All(state.VolcanicActivity, activity =>
        {
            Assert.False(float.IsNaN(activity) || float.IsInfinity(activity));
            Assert.True(activity >= 0f);
        });
    }

    /// <summary>
    /// 熱點固定在地函參考系：板塊移動不會把熱點帶走，所以噴發位置在**世界座標上**不變。
    /// </summary>
    [Fact]
    public void Plumes_StayFixedWhilePlatesMoveOverThem()
    {
        var grid = CreateGrid();
        var engine = new SimulationEngine(grid, seed: 42);

        var before = engine.Plumes.Plumes
            .Select(plume => (plume.X, plume.Y, plume.Z)).ToArray();

        // 只跑到不會有熱柱壽命耗盡的程度（最短壽命2,000萬年＝400步）
        engine.Step(300);

        var after = engine.Plumes.Plumes
            .Select(plume => (plume.X, plume.Y, plume.Z)).ToArray();

        Assert.Equal(before, after);
    }

    /// <summary>
    /// 板塊從熱點上滑過去，會在板塊上拖出一串火山島（夏威夷－天皇海山鏈）。
    ///
    /// 判準是「增厚的格子連成一條長條」而不是集中在一個點上：量增厚區域的**跨度**，
    /// 如果熱點錯誤地跟著板塊跑，所有噴發都會疊在同一格上，跨度會接近0。
    /// </summary>
    [Fact]
    public void Plumes_LeaveVolcanicChainsAsPlatesDriftOver()
    {
        var grid = CreateGrid();
        var engine = new SimulationEngine(grid, seed: 42);
        var before = (float[])engine.State.CrustThickness.Clone();

        engine.Step(300); // 1,500萬年，板塊約移動數格

        // 找出被熱柱明顯堆厚、又不在板塊邊界上的格子（排除島弧與中洋脊的貢獻）
        var built = Enumerable.Range(0, engine.State.CellCount)
            .Where(cell => engine.State.CrustThickness[cell] - before[cell] > 1f)
            .Where(cell => engine.State.Boundary[cell] == BoundaryType.None)
            .ToArray();

        Assert.True(built.Length >= MantlePlumes.DefaultPlumeCount,
            $"板塊內部只有 {built.Length} 格被火山堆厚，少於熱柱數量 {MantlePlumes.DefaultPlumeCount}——" +
            "熱柱沒有在板塊上拖出島鏈");

        // 至少要有一支熱柱拖出跨越多格的鏈，而不是只堆在原地一格
        var spacing = Math.Sqrt(grid.AverageCellAreaSquareMeters);
        var maxSpan = 0.0;
        foreach (var a in built)
        {
            foreach (var b in built)
            {
                var distance = Distance(grid, a, b);
                // 只看同一串（距離在合理範圍內），跨到另一支熱柱的距離不算數
                if (distance < spacing * 8 && distance > maxSpan) maxSpan = distance;
            }
        }

        Assert.True(maxSpan > spacing * 2,
            $"火山堆積的最大跨度只有 {maxSpan / 1000:F0} 公里（格子間距約 {spacing / 1000:F0} 公里）——" +
            "噴發全部疊在同一處，熱點可能被錯誤地跟著板塊移動了");
    }

    private static double Distance(SimulationGrid grid, int a, int b)
    {
        var dot = grid.UnitVector[a * 3] * grid.UnitVector[b * 3]
            + grid.UnitVector[a * 3 + 1] * grid.UnitVector[b * 3 + 1]
            + grid.UnitVector[a * 3 + 2] * grid.UnitVector[b * 3 + 2];
        return Math.Acos(Math.Clamp(dot, -1.0, 1.0)) * grid.PlanetRadiusMeters;
    }

    /// <summary>熱柱有壽命，熄滅後要在別處重新生成一支，維持長時間下的變化。</summary>
    [Fact]
    public void Plumes_EventuallyDieAndRespawnElsewhere()
    {
        var grid = CreateGrid();
        var engine = new SimulationEngine(grid, seed: 42);

        var before = engine.Plumes.Plumes.Select(p => (p.X, p.Y, p.Z)).ToArray();
        engine.Step(4000); // 2億年，遠超過最長壽命1.2億年
        var after = engine.Plumes.Plumes.Select(p => (p.X, p.Y, p.Z)).ToArray();

        Assert.True(before.Zip(after).Any(pair => pair.First != pair.Second),
            "跑了2億年沒有任何熱柱熄滅重生——熱點會永遠固定在同樣幾個位置反覆堆高");
        Assert.Equal(MantlePlumes.DefaultPlumeCount, engine.Plumes.Plumes.Length);
    }

    /// <summary>三種火山（中洋脊、島弧、熱柱）都要記錄到 VolcanicActivity 裡，渲染才看得到全部。</summary>
    [Fact]
    public void Step_RecordsVolcanismAtRidgesArcsAndPlumes()
    {
        var engine = new SimulationEngine(CreateGrid(), seed: 42);
        engine.Step(200);

        var active = Enumerable.Range(0, engine.State.CellCount)
            .Where(cell => engine.State.VolcanicActivity[cell] > 0f)
            .ToArray();

        Assert.Contains(active, cell => engine.State.Boundary[cell] == BoundaryType.Divergent);
        Assert.Contains(active, cell => engine.State.Boundary[cell] == BoundaryType.Convergent);
        Assert.Contains(active, cell => engine.State.Boundary[cell] == BoundaryType.None);
    }
}
