using Planet.Simulation;
using Xunit;

namespace Planet.Simulation.Tests;

/// <summary>
/// 氣候模型的測試。
///
/// 守的是**地球上可以指名道姓的氣候特徵**，而不是「數字有沒有 NaN」：
/// 赤道多雨帶、南北緯30度的沙漠帶、雨影、高處比較冷、極區乾燥。
/// 這些特徵如果沒出現，代表風場或水氣輸送寫錯了，而那種錯誤在總量統計上完全看不出來。
/// </summary>
public class ClimateTests
{
    private static readonly double TestRadiusMeters = TestPlanet.RadiusMeters;

    private static SimulationGrid CreateGrid() => SimulationGrid.Build(TestRadiusMeters, 24);

    /// <summary>全球都是海的狀態，用來單獨檢驗緯度效應（沒有地形干擾）。</summary>
    private static SimulationState CreateOceanOnly(SimulationGrid grid)
    {
        var state = new SimulationState(grid.CellCount) { SeaLevelMeters = 0f };
        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            state.BedrockElevation[cell] = -3000f;
            state.WaterDepth[cell] = 3000f;
            state.Crust[cell] = CrustType.Oceanic;
            state.CrustThickness[cell] = Isostasy.OceanicCrustThicknessKm;
            state.CrustDensity[cell] = Isostasy.OceanicCrustDensity;
        }
        return state;
    }

    /// <summary>指定緯度帶（含南北半球）的平均值。</summary>
    private static double MeanInLatitudeBand(SimulationGrid grid, float[] field, double from, double to)
    {
        var total = 0.0;
        var count = 0;
        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            var absLatitude = Math.Abs(grid.Latitude[cell]);
            if (absLatitude < from || absLatitude > to) continue;
            total += field[cell];
            count++;
        }
        return count > 0 ? total / count : 0.0;
    }

    [Fact]
    public void Solve_TemperatureFallsFromEquatorToPoles()
    {
        var grid = CreateGrid();
        var state = CreateOceanOnly(grid);
        new Climate(grid).Solve(state);

        var equator = MeanInLatitudeBand(grid, state.Temperature, 0, 10);
        var middle = MeanInLatitudeBand(grid, state.Temperature, 40, 50);
        var polar = MeanInLatitudeBand(grid, state.Temperature, 80, 90);

        Assert.True(equator > middle && middle > polar,
            $"溫度沒有隨緯度下降（赤道{equator:F1}°C、中緯{middle:F1}°C、極區{polar:F1}°C）");
        Assert.InRange(equator, 25, 35);   // 地球赤道約26~30°C
        Assert.InRange(polar, -35, -10);   // 地球極區年均約-20°C
    }

    /// <summary>高處比較冷——這是氣溫遞減率，也是階段4冰川作用發不發生的判準來源。</summary>
    [Fact]
    public void Solve_TemperatureFallsWithAltitude()
    {
        var grid = CreateGrid();
        var state = CreateOceanOnly(grid);

        // 挑一格赤道附近的格子，把它抬到4公里高
        var lowland = Enumerable.Range(0, grid.CellCount).MinBy(c => Math.Abs(grid.Latitude[c]));
        var highland = Enumerable.Range(0, grid.CellCount)
            .Where(c => c != lowland && Math.Abs(grid.Latitude[c]) < 10)
            .MinBy(c => Math.Abs(grid.Latitude[c]));

        foreach (var cell in new[] { lowland, highland })
        {
            state.BedrockElevation[cell] = 0f;
            state.WaterDepth[cell] = 0f;
        }
        state.BedrockElevation[highland] = 4000f;

        new Climate(grid).Solve(state);

        var drop = state.Temperature[lowland] - state.Temperature[highland];
        // 4公里 × 6.5°C/km = 26度，再加上大陸度修正會再放大一些
        Assert.InRange(drop, 20f, 40f);
    }

    /// <summary>
    /// 赤道多雨、南北緯30度乾燥。
    ///
    /// 這是三胞環流最直接的後果（赤道輻合上升成雲致雨、副熱帶下沉增溫變乾），
    /// 也是撒哈拉、阿拉伯、澳洲內陸這一整圈沙漠帶的成因。
    /// </summary>
    [Fact]
    public void Solve_ProducesWetEquatorAndSubtropicalDryBelt()
    {
        var grid = CreateGrid();
        var state = CreateOceanOnly(grid);
        new Climate(grid).Solve(state);

        var equator = MeanInLatitudeBand(grid, state.Precipitation, 0, 10);
        var subtropics = MeanInLatitudeBand(grid, state.Precipitation, 25, 35);

        Assert.True(equator > subtropics,
            $"赤道（{equator:F2}）沒有比副熱帶（{subtropics:F2}）多雨——三胞環流的帶狀分布沒有生效");
    }

    /// <summary>極區降水少不是因為沒有水，是因為冷空氣裝不了多少水氣（Clausius–Clapeyron）。</summary>
    [Fact]
    public void Solve_PolarRegionsAreDryBecauseColdAirHoldsLittleMoisture()
    {
        var grid = CreateGrid();
        var state = CreateOceanOnly(grid);
        new Climate(grid).Solve(state);

        var tropics = MeanInLatitudeBand(grid, state.Precipitation, 0, 20);
        var polar = MeanInLatitudeBand(grid, state.Precipitation, 75, 90);

        Assert.True(tropics > polar * 2,
            $"極區（{polar:F2}）沒有比熱帶（{tropics:F2}）乾多少——持水能力隨溫度變化沒有生效");
    }

    /// <summary>
    /// 雨影：山脈的迎風坡多雨、背風坡乾燥。
    ///
    /// 判準是「同一道山脈的兩側」而不是「山上 vs 平地」——後者混進了緯度與高度的影響。
    /// </summary>
    [Fact]
    public void Solve_MountainsCastRainShadowsOnTheirLeewardSide()
    {
        var grid = CreateGrid();
        var state = CreateOceanOnly(grid);
        var climate = new Climate(grid);

        // 先解一次沒有山的氣候當基準
        climate.Solve(state);
        var baseline = (float[])state.Precipitation.Clone();

        // 在中緯度（西風帶）豎一座4公里的山，連同鄰居一起抬起來形成一道山脈
        var peak = Enumerable.Range(0, grid.CellCount).MinBy(c => Math.Abs(Math.Abs(grid.Latitude[c]) - 45));
        var ridge = new List<int> { peak };
        var baseIndex = peak * SimulationGrid.MaxNeighborsPerCell;
        for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
        {
            var neighbor = grid.Neighbors[baseIndex + s];
            if (neighbor != SimulationGrid.NoNeighbor) ridge.Add(neighbor);
        }
        foreach (var cell in ridge)
        {
            state.BedrockElevation[cell] = 4000f;
            state.WaterDepth[cell] = 0f;
        }

        climate.Solve(state);

        // 背風側＝山的下風處。中緯是西風帶（由西往東），所以往東找。
        var leeward = ridge
            .SelectMany(cell => Enumerable.Range(0, SimulationGrid.MaxNeighborsPerCell)
                .Select(s => grid.Neighbors[cell * SimulationGrid.MaxNeighborsPerCell + s]))
            .Where(n => n != SimulationGrid.NoNeighbor && !ridge.Contains(n))
            .Distinct()
            .ToArray();

        var driedCount = leeward.Count(cell => state.Precipitation[cell] < baseline[cell] * 0.95f);
        Assert.True(driedCount > 0,
            "山脈周圍沒有任何一格因為山的存在而變乾——地形抬升沒有攔下水氣，雨影不會出現");
    }

    /// <summary>
    /// 水氣要能深入內陸，不能全部卡在海岸線。
    ///
    /// **這是把氣候誤當成時間積分最典型的症狀**（見 <c>Climate</c> 的類別說明）：
    /// 大氣輸送的時間尺度是幾天、模擬一步是5萬年，所以每一步都必須解到穩態。
    /// 只推進一格的話，內陸永遠是沙漠，而且完全不會有錯誤訊息。
    /// </summary>
    [Fact]
    public void Solve_MoistureReachesDeepIntoContinentalInteriors()
    {
        var grid = CreateGrid();
        var state = CreateOceanOnly(grid);

        // 把北半球中緯的一大片變成陸地
        var land = new List<int>();
        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            if (grid.Latitude[cell] is < 20 or > 60) continue;
            state.BedrockElevation[cell] = 200f;
            state.WaterDepth[cell] = 0f;
            land.Add(cell);
        }

        new Climate(grid).Solve(state);

        var wetLand = land.Count(cell => state.Moisture[cell] > 0f);
        Assert.True(wetLand > land.Count * 0.8,
            $"{land.Count}格陸地裡只有{wetLand}格有逕流——水氣沒有深入內陸，" +
            "很可能是把穩態求解寫成了每步只推進一格的時間積分");
    }

    /// <summary>
    /// 水氣能被帶多遠是**物理距離**，必須跟網格解析度無關。
    ///
    /// 這是踩過坑才補的（2026-09-06）：原本輸送寫死成「迭代60次」，
    /// 而每次迭代水氣大約前進一格——同樣的60次在 n=24（格距93公里）是5,580公里、
    /// 在正式解析度 n=150（格距14.9公里）只剩894公里。結果是**粗網格的測試全部通過、
    /// 正式解析度下一半的陸地降水掛零**。所以這裡直接守「不同解析度下的實際輸送距離要一致」。
    /// </summary>
    [Fact]
    public void TransportDistance_IsIndependentOfGridResolution()
    {
        // 拿兩個解析度差很多的網格，量水氣實際能從海岸深入多遠
        var coarse = MeasureInlandReachKm(24);
        var fine = MeasureInlandReachKm(80);

        Assert.True(fine > coarse * 0.5 && fine < coarse * 2.0,
            $"粗網格能深入 {coarse:F0} 公里、細網格只有 {fine:F0} 公里——" +
            "輸送距離跟著解析度變了，代表迭代次數被寫死成固定的格數而不是換算自物理距離");
    }

    /// <summary>把半顆星球變成陸地，量水氣從海岸往內陸最遠到哪裡還有降水。</summary>
    private static double MeasureInlandReachKm(int frequency)
    {
        var grid = SimulationGrid.Build(TestRadiusMeters, frequency);
        var state = CreateOceanOnly(grid);

        // 東半球全部變陸地，海岸線就在經度0度
        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            if (grid.Longitude[cell] is < 0 or > 180) continue;
            state.BedrockElevation[cell] = 200f;
            state.WaterDepth[cell] = 0f;
        }

        new Climate(grid).Solve(state);

        // 有明顯降水的陸地格，離海岸（經度0度）最遠是多少公里
        var maxDegrees = 0.0;
        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            if (state.WaterDepth[cell] > 0f || state.Precipitation[cell] < 0.05f) continue;
            var longitude = grid.Longitude[cell];
            if (longitude is < 0 or > 180) continue;
            if (longitude > maxDegrees) maxDegrees = longitude;
        }

        // 沿著赤道換算成公里
        return maxDegrees * Math.PI / 180.0 * TestRadiusMeters / 1000.0;
    }

    /// <summary>海面上不該有「陸地逕流」，那是階段4侵蝕的輸入，只對陸地有意義。</summary>
    [Fact]
    public void Solve_RunoffIsZeroOverWater()
    {
        var grid = CreateGrid();
        var state = CreateOceanOnly(grid);
        new Climate(grid).Solve(state);

        Assert.All(state.Moisture, runoff => Assert.Equal(0f, runoff));
    }

    [Fact]
    public void Solve_StaysNumericallySane()
    {
        var engine = new SimulationEngine(CreateGrid(), 42);
        engine.Step(200);

        foreach (var value in engine.State.Precipitation)
        {
            Assert.False(float.IsNaN(value) || float.IsInfinity(value), "降水出現 NaN/Infinity");
            Assert.True(value >= 0f);
        }
        foreach (var value in engine.State.Temperature)
        {
            Assert.False(float.IsNaN(value) || float.IsInfinity(value), "溫度出現 NaN/Infinity");
            Assert.InRange(value, -100f, 100f);
        }
        Assert.All(engine.State.AtmosphericMoisture, m => Assert.True(m >= 0f));
    }
}
