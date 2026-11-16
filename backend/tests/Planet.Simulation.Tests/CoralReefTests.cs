using Planet.Simulation;
using Xunit;

namespace Planet.Simulation.Tests;

/// <summary>
/// 珊瑚礁的測試。
///
/// 守的是三個真實的生長限制（暖、淺、長到海平面就停）與一個湧現行為（環礁）。
/// 這些限制如果沒生效，症狀是「整片海底都鋪滿珊瑚」或「珊瑚長到幾公里高」，
/// 兩者在總量統計上都只是一個比較大的數字，不會有錯誤訊息。
/// </summary>
public class CoralReefTests
{
    private static readonly double TestRadiusMeters = TestPlanet.RadiusMeters;

    private static SimulationGrid CreateGrid() => SimulationGrid.Build(TestRadiusMeters, 24);

    /// <summary>造出一片指定水深、指定水溫的海，用來單獨檢驗某一個生長條件。</summary>
    private static SimulationState CreateSea(SimulationGrid grid, float depthMeters, float temperatureC)
    {
        var state = new SimulationState(grid.CellCount) { SeaLevelMeters = 0f };
        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            state.BedrockElevation[cell] = -depthMeters;
            state.WaterDepth[cell] = depthMeters;
            state.Temperature[cell] = temperatureC;
            state.Crust[cell] = CrustType.Oceanic;
        }
        return state;
    }

    [Fact]
    public void Step_GrowsInWarmShallowWater()
    {
        var state = CreateSea(CreateGrid(), depthMeters: 20f, temperatureC: 27f);
        new CoralReefs().Step(state, 50_000);

        Assert.All(state.OrganicThickness, thickness => Assert.True(thickness > 0f));
    }

    /// <summary>低於18°C造礁珊瑚活不了——這是珊瑚礁只出現在低緯度的原因。</summary>
    [Fact]
    public void Step_DoesNotGrowInColdWater()
    {
        var state = CreateSea(CreateGrid(), depthMeters: 20f, temperatureC: 10f);
        new CoralReefs().Step(state, 50_000);

        Assert.All(state.OrganicThickness, thickness => Assert.Equal(0f, thickness));
    }

    /// <summary>超過透光帶就沒有足夠的光讓共生藻行光合作用，深海底不會鋪滿珊瑚。</summary>
    [Fact]
    public void Step_DoesNotGrowInDeepWater()
    {
        var state = CreateSea(CreateGrid(), depthMeters: 3000f, temperatureC: 27f);
        new CoralReefs().Step(state, 50_000);

        Assert.All(state.OrganicThickness, thickness => Assert.Equal(0f, thickness));
    }

    /// <summary>
    /// 珊瑚長到海平面就停住（長出水面會曬死），形成非常平坦的礁坪。
    /// 沒有這個上限的話礁體會無限往上長，變成幾公里高的柱子。
    /// </summary>
    [Fact]
    public void Step_StopsGrowingAtSeaLevel()
    {
        var grid = CreateGrid();
        var state = CreateSea(grid, depthMeters: 20f, temperatureC: 27f);
        var reefs = new CoralReefs();

        // 跑很多步，遠超過填滿20公尺水深所需
        for (var i = 0; i < 200; i++)
        {
            reefs.Step(state, 50_000);
            for (var cell = 0; cell < state.CellCount; cell++)
            {
                state.WaterDepth[cell] = MathF.Max(0f, state.SeaLevelMeters - state.SurfaceElevationAt(cell));
            }
        }

        // 礁頂應該停在海平面附近（風暴碎屑可以再高幾公尺），不會失控
        Assert.All(state.OrganicThickness, thickness => Assert.InRange(thickness, 20f, 30f));
    }

    /// <summary>
    /// 大陸棚上的礁停在海面下、不變成陸地；海洋地殼上的礁照舊長成珊瑚島（2026-11-16）。
    /// 沒有這個區分時，熱帶水深 60 公尺以內的大陸棚整片長到海面上 3 公尺、變成陸地。
    /// </summary>
    [Fact]
    public void Step_ContinentalReefsStayUnderwater_OceanicReefsStillBecomeIslands()
    {
        var grid = CreateGrid();
        var state = CreateSea(grid, depthMeters: 20f, temperatureC: 27f);
        for (var cell = 0; cell < state.CellCount; cell += 2) state.Crust[cell] = CrustType.Continental;
        var reefs = new CoralReefs { ContinentalReefsStayBelowSeaLevel = true };

        for (var i = 0; i < 50; i++)
        {
            reefs.Step(state, 50_000);
            for (var cell = 0; cell < state.CellCount; cell++)
                state.WaterDepth[cell] = MathF.Max(0f, state.SeaLevelMeters - state.SurfaceElevationAt(cell));
        }

        for (var cell = 0; cell < state.CellCount; cell++)
        {
            if (state.Crust[cell] == CrustType.Continental)
                Assert.InRange(state.WaterDepth[cell], 4.9f, 5.1f);
            else
                Assert.Equal(0f, state.WaterDepth[cell]);
        }
    }

    /// <summary>
    /// 露出水面的礁照設計的速率溶蝕（2026-11-16）：降水是相對尺度，要先換算成公釐／年。
    /// 全球平均的降水 ＝ 1,000 公釐／年 ＝ 每百萬年 30 公尺；沒換算時同樣的降水只溶掉幾公分，露出水面的礁只進不出。
    /// </summary>
    [Fact]
    public void Step_ExposedReefDissolvesAtTheDesignedRate()
    {
        var grid = CreateGrid();
        var state = CreateSea(grid, depthMeters: 0f, temperatureC: 27f);
        for (var cell = 0; cell < state.CellCount; cell++)
        {
            state.OrganicThickness[cell] = 100f;
            state.Precipitation[cell] = cell % 2 == 0 ? 0.5f : 1.5f; // 平均 1：一半的格子雨少、一半雨多
        }

        new CoralReefs { DissolutionUsesMillimetres = true }.Step(state, 1_000_000);

        // 0.03 公尺／百萬年／公釐 × 500、1,500 公釐
        Assert.Equal(100f - 15f, state.OrganicThickness[0], 0.1f);
        Assert.Equal(100f - 45f, state.OrganicThickness[1], 0.1f);
    }

    /// <summary>
    /// 在完整的模擬裡，珊瑚**生長**必須只發生在低緯度的溫暖海域。
    ///
    /// **守的是「哪裡在長」，不是「哪裡有礁灰岩」**（2026-09-07 改）：礁灰岩掛在標記上、
    /// 會隨板塊漂移，熱帶長成的礁體漂到高緯度是真實現象——板塊每百萬年移動約50公里，
    /// 2億年足以跨越90個緯度，地球上也確實有這種漂離熱帶的碳酸鹽台地。
    /// 所以「高緯度不存在礁灰岩」是錯的不變量，會在礁體正確漂移之後開始誤報。
    /// </summary>
    [Fact]
    public void Step_ReefsAreConfinedToWarmLowLatitudes()
    {
        var grid = CreateGrid();
        var engine = new SimulationEngine(grid, 42);
        engine.Step(2000);

        // 記下這一刻的礁體，然後**只**跑造礁這一個模型，比較出「珊瑚長高的格子」。
        //
        // **不可以改成 engine.Step(1) 再比差值**（2026-09-07 踩過）：礁灰岩掛在標記上、
        // 會隨板塊漂移，所以一塊帶著礁體的標記漂進某一格，那一格的數值也會增加——
        // 那是搬過來的，不是長出來的。實測極區就出現過這種格子，訊息是
        // 「有珊瑚長在緯度90度」，但水溫下限其實完全正常。
        // 單獨呼叫 CoralReefs.Step 才能把生長跟漂移分開。
        var before = engine.State.OrganicThickness.ToArray();
        new CoralReefs().Step(engine.State, 50_000);

        var grew = Enumerable.Range(0, engine.State.CellCount)
            .Where(cell => engine.State.OrganicThickness[cell] > before[cell] + 0.01f)
            .ToArray();

        Assert.NotEmpty(grew);

        var maxLatitude = grew.Max(cell => Math.Abs(grid.Latitude[cell]));
        Assert.True(maxLatitude < 50,
            $"有珊瑚長在緯度 {maxLatitude:F0} 度——18°C 的水溫下限沒有生效");
    }

    /// <summary>
    /// 有些礁體要能長成露出水面的珊瑚島。
    ///
    /// 這是達爾文沉降理論的產物：熱柱造出火山島 → 海洋地殼隨年齡變深而下沉 →
    /// 珊瑚往上追海平面。三個要素在這個模型裡都已經存在，所以環礁是**自己長出來的**。
    /// </summary>
    [Fact]
    public void Step_SomeReefsBecomeIslands()
    {
        var engine = new SimulationEngine(CreateGrid(), 42);
        engine.Step(4000);

        var islands = Enumerable.Range(0, engine.State.CellCount)
            .Count(cell => engine.State.OrganicThickness[cell] > 1f && engine.State.WaterDepth[cell] <= 0f);

        Assert.True(islands > 0, "跑了2億年沒有任何珊瑚礁長出海面——礁體追不上海平面，環礁不會形成");
    }

    [Fact]
    public void Step_StaysNumericallySane()
    {
        var engine = new SimulationEngine(CreateGrid(), 42);
        engine.Step(1000);

        foreach (var thickness in engine.State.OrganicThickness)
        {
            Assert.False(float.IsNaN(thickness) || float.IsInfinity(thickness), "礁體厚度出現 NaN/Infinity");
            Assert.InRange(thickness, 0f, 5000f);
        }
    }
}
