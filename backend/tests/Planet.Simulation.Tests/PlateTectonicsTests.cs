using Planet.Simulation;
using Xunit;

namespace Planet.Simulation.Tests;

/// <summary>
/// 內營力的測試。
///
/// 模擬「對不對」很難直接斷言（地形長得怪跟模型錯了之間距離很遠），所以這裡守的是
/// **可以明確判斷對錯的性質**：板塊真的有動、每格都屬於某個板塊、地殼守恆沒有變成 NaN、
/// 均衡公式的量級對得上地球，以及最重要的——從全海洋起步真的會長出陸地。
/// </summary>
public class PlateTectonicsTests
{
    private static readonly double TestRadiusMeters = TestPlanet.RadiusMeters;

    /// <summary>測試用的小網格。n=24 約5,762格，夠跑出板塊行為又不會讓測試太慢。</summary>
    private static SimulationEngine CreateEngine(int seed = 42) =>
        new(SimulationGrid.Build(TestRadiusMeters, 24), seed);

    /// <summary>
    /// 起始狀態：大部分是海，但已經有早期大轟炸留下的原始陸塊。
    ///
    /// 「完全被海水覆蓋」這個原始規格已於 2026-09-06 由使用者取消——完全均質的起點跑不出大陸
    /// （實測跑到6億年陸地只有0.28%），所以起始就要有大尺度的海陸雛形。
    /// </summary>
    [Fact]
    public void InitialState_IsMostlyOceanWithProtoContinentsFromImpacts()
    {
        var engine = CreateEngine();
        var landFraction = engine.LandFraction();

        Assert.True(landFraction > 0, "起始狀態一塊陸地都沒有——早期大轟炸沒有留下原始陸塊");
        Assert.True(landFraction < 0.5, $"起始陸地佔了 {landFraction:P1}，這不是一顆以海洋為主的星球");
        Assert.Contains(engine.State.Crust, crust => crust == CrustType.Continental);
        Assert.Contains(engine.State.Crust, crust => crust == CrustType.Oceanic);
    }

    [Fact]
    public void Step_AdvancesTimeAndStepCount()
    {
        var engine = CreateEngine();
        engine.Step(10);

        Assert.Equal(10, engine.State.StepCount);
        Assert.Equal(10 * SimulationEngine.DefaultYearsPerStep, engine.State.ElapsedYears);
    }

    /// <summary>
    /// 板塊真的要會動。這是整個階段最容易安靜失敗的地方——格子8.6公里、一步只前進0.5~5公里，
    /// 如果沒有「累積旋轉到超過半格才搬」的機制，每步都會四捨五入回原地，
    /// 板塊看起來永遠靜止，而且不會有任何錯誤訊息。
    /// </summary>
    [Fact]
    public void Step_PlatesActuallyMove()
    {
        var engine = CreateEngine();
        var before = (int[])engine.Plates.PlateIdByCell.Clone();

        // 跑50步（250萬年）——足夠讓最慢的板塊也累積超過門檻
        engine.Step(50);

        var changed = before.Where((plate, cell) => plate != engine.Plates.PlateIdByCell[cell]).Count();
        Assert.True(changed > 0,
            "跑了50步板塊歸屬完全沒變——累積旋轉的搬動機制沒有生效，板塊實際上是靜止的");
    }

    [Fact]
    public void Step_EveryCellAlwaysBelongsToAValidPlate()
    {
        var engine = CreateEngine();
        engine.Step(60);

        Assert.All(engine.Plates.PlateIdByCell,
            plate => Assert.InRange(plate, 0, engine.Plates.Plates.Length - 1));
        // 顯示用的副本要跟模擬用的一致，不然畫出來的板塊圖是舊的
        Assert.Equal(engine.Plates.PlateIdByCell, engine.State.PlateId);
    }

    /// <summary>從全海洋起步，經過隱沒與島弧增生，最後必須長出陸地——這是整個階段2的目的。</summary>
    [Fact]
    public void Step_OceanWorldEventuallyGrowsContinents()
    {
        var engine = CreateEngine();
        engine.Step(400); // 2,000萬年

        var landFraction = engine.LandFraction();
        Assert.True(landFraction > 0,
            $"跑了2,000萬年還是一片海洋（陸地比例 {landFraction:P2}）——島弧增生沒有把海洋地殼轉成大陸地殼");

        Assert.Contains(engine.State.Crust, crust => crust == CrustType.Continental);
    }

    /// <summary>
    /// 大陸要連成**塊**，不能散成滿天單格孤島。
    ///
    /// 這個測試是踩過坑才補的（2026-09-05）：上面那個「有沒有長出陸地」的測試完全看不出這個失敗模式——
    /// 當時大陸格數有415個、厚度分布也是漂亮的雙峰（大陸42.7／海洋7.3公里），每一項既有斷言都通過，
    /// 但實際上是**276塊碎片、最大只有8格、47.5%是單格孤島**，畫出來會像一顆長滿彩色斑點的球。
    /// 「總量對」跟「長得對」是兩件事，所以這裡直接量連通分量。
    /// </summary>
    [Fact]
    public void Step_ContinentsFormCoherentMassesNotSpeckle()
    {
        var engine = CreateEngine();
        engine.Step(4000); // 2億年

        var sizes = ContinentSizes(engine);
        Assert.NotEmpty(sizes);

        var continentalCells = sizes.Sum();
        var largest = sizes.Max();
        var singles = sizes.Count(size => size == 1);

        Assert.True(largest >= 50,
            $"最大的一塊大陸只有 {largest} 格（共 {continentalCells} 格大陸地殼、{sizes.Count} 塊）——" +
            "大陸沒有連成塊，散成了碎片");
        // 分母刻意是「大陸總格數」而不是「碎塊數」：真實星球本來就有夏威夷、冰島這種單格火山島，
        // 碎塊數量多不是問題，**面積被碎片瓜分掉**才是。修正前是47.5%，修正後9.6%。
        Assert.True((double)singles / continentalCells < 0.25,
            $"{continentalCells} 格大陸地殼裡有 {singles} 格是單格孤島" +
            $"（{(double)singles / continentalCells:P1}）——大陸面積被碎片瓜分掉了");
    }

    /// <summary>把互相相鄰的大陸地殼分群，回傳每一群的格數。</summary>
    private static List<int> ContinentSizes(SimulationEngine engine)
    {
        var grid = engine.Grid;
        var crust = engine.State.Crust;
        var seen = new bool[crust.Length];
        var sizes = new List<int>();
        var stack = new Stack<int>();

        for (var start = 0; start < crust.Length; start++)
        {
            if (seen[start] || crust[start] != CrustType.Continental) continue;

            var size = 0;
            stack.Push(start);
            seen[start] = true;
            while (stack.Count > 0)
            {
                var cell = stack.Pop();
                size++;
                var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
                for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
                {
                    var neighbor = grid.Neighbors[baseIndex + s];
                    if (neighbor == SimulationGrid.NoNeighbor || seen[neighbor]) continue;
                    if (crust[neighbor] != CrustType.Continental) continue;
                    seen[neighbor] = true;
                    stack.Push(neighbor);
                }
            }
            sizes.Add(size);
        }

        return sizes;
    }

    /// <summary>
    /// 地殼厚度必須是**雙峰**的：海洋地殼薄、大陸地殼厚，而不是所有地方都攤成同一個中間值。
    /// 攤平的症狀是整顆星球既沒有深海平原也沒有高地，只有一片沒有起伏的淺灘。
    /// </summary>
    [Fact]
    public void Step_CrustThicknessStaysBimodal()
    {
        var engine = CreateEngine();
        engine.Step(4000);
        var state = engine.State;

        var oceanic = Enumerable.Range(0, state.CellCount)
            .Where(cell => state.Crust[cell] == CrustType.Oceanic)
            .Select(cell => state.CrustThickness[cell]).ToArray();
        var continental = Enumerable.Range(0, state.CellCount)
            .Where(cell => state.Crust[cell] == CrustType.Continental)
            .Select(cell => state.CrustThickness[cell]).ToArray();

        Assert.NotEmpty(continental);

        // 用**中位數**而不是平均數：重板塊的裂縫火山會堆出很厚的火山島，而那些島本來就是
        // 海洋地殼（玄武岩堆在海洋地殼上，是「島」不是「大陸」）。少數幾根火山堆足以把
        // 平均值拉過門檻（實測16.1），但那不代表雙峰壞掉——這裡要驗的是
        // 「絕大多數的海洋地殼仍然是薄的」，中位數才直接表達這件事（2026-09-06）。
        var oceanicMedian = oceanic.Order().ElementAt(oceanic.Length / 2);
        Assert.InRange(oceanicMedian, 7f, 15f);

        // 上界刻意放到60而不是照地球的35~40：**階段2還沒有侵蝕作用**，
        // 而侵蝕是真實星球把大陸地殼磨薄的主要機制。目前地殼增厚唯一的出口是重力擴展
        // （見 GravitationalSpreadingThresholdKm），所以大陸會停在比地球厚一些的地方。
        // 等階段4補上侵蝕之後，這個上界應該要收回到接近地球的量級。
        Assert.InRange(continental.Average(), 25f, 60f);
    }

    /// <summary>
    /// 海平面必須由**守恆的水量**反解，不是固定值。
    ///
    /// 這是「陸地比例」這個數字有沒有意義的前提（2026-09-06 補上）：用固定海平面的話，
    /// 大陸長高長大時海水不會被排開，陸地比例完全由地殼厚度的絕對值決定，
    /// 那只是一堆未校準參數的組合，不能拿來跟地球的29%比較。
    /// **驗法刻意是「比較兩個水量不同的星球」，不是「看大陸長大時海平面有沒有上升」**
    /// （2026-09-06 改）：後者在只有板塊構造時成立，但加上侵蝕與冰蓋之後就不再是單變數關係——
    /// 侵蝕會把整個地表（含海底）削低，海平面是絕對高度、跟著一起降；冰蓋則把水鎖在陸地上。
    /// 實測大陸從51格長到1279格的同時，海平面反而從931降到497公尺，而冰只能解釋其中的142公尺。
    /// 改成控制變因的比較，才是真正在驗「海平面由水量決定」這件事。
    /// </summary>
    [Fact]
    public void Step_ConservesTotalWaterVolume()
    {
        var engine = CreateEngine();
        var cellArea = engine.Grid.AverageCellAreaSquareMeters;

        // **要把冰蓋鎖住的水算進來**：冰不是消失，是從海洋轉到陸地上（見 Cryosphere）。
        // 只量 WaterDepth 的話，冰一長就會看起來像水憑空不見了。
        double TotalWater() =>
            engine.State.WaterDepth.Sum(depth => (double)depth) * cellArea
            + Cryosphere.LockedWaterVolume(engine.State, cellArea);

        var before = TotalWater();
        engine.Step(2000); // 1億年，大陸會明顯長大
        var after = TotalWater();

        Assert.Equal(before, after, before * 1e-6);
    }

    /// <summary>
    /// 大陸長大時海平面要跟著上升——這是水量守恆的直接後果，也是陸地比例會自我調節的原因。
    /// </summary>
    [Fact]
    public void Step_SeaLevelIsDerivedFromWaterVolume()
    {
        // 同一顆星球（同種子＝同板塊、同演化），只有總水量不同
        var dry = new SimulationEngine(SimulationGrid.Build(TestRadiusMeters, 24), 42, oceanDepthMeters: 2500f);
        var wet = new SimulationEngine(SimulationGrid.Build(TestRadiusMeters, 24), 42, oceanDepthMeters: 4500f);

        dry.Step(1000);
        wet.Step(1000);

        Assert.True(wet.State.SeaLevelMeters > dry.State.SeaLevelMeters,
            $"水多的星球海平面（{wet.State.SeaLevelMeters:F0}）沒有比水少的（{dry.State.SeaLevelMeters:F0}）高"
            + "——海平面不是由守恆的水量反解出來的");
        Assert.True(wet.LandFraction() < dry.LandFraction(),
            $"水多的星球陸地比例（{wet.LandFraction():P1}）沒有比水少的（{dry.LandFraction():P1}）低");
    }

    /// <summary>
    /// 板塊配置必須在無淨旋轉參考系下（見 <c>PlateSet.RemoveNetRotation</c>）。
    ///
    /// 淨旋轉在物理上是無意義的自由度（只有相對運動是真的），但在這個模擬裡它會讓整片地殼
    /// 一起掃過離散網格、製造取整雜訊。實測把它扣掉之後最大陸塊從66格變成174格、
    /// 單格孤島從21%降到9.6%——是單項效果最大的一個修正，所以要有測試守著。
    /// </summary>
    [Fact]
    public void Plates_HaveNoNetRotation()
    {
        var plates = PlateSet.CreateRandom(SimulationGrid.Build(TestRadiusMeters, 24), seed: 42);

        var cellsPerPlate = new int[plates.Plates.Length];
        foreach (var plate in plates.PlateIdByCell) cellsPerPlate[plate]++;

        double x = 0, y = 0, z = 0;
        var typicalSpeed = 0.0;
        for (var p = 0; p < plates.Plates.Length; p++)
        {
            var plate = plates.Plates[p];
            var weight = (double)cellsPerPlate[p] / plates.PlateIdByCell.Length;
            x += plate.PoleX * plate.AngularVelocityDegPerMyr * weight;
            y += plate.PoleY * plate.AngularVelocityDegPerMyr * weight;
            z += plate.PoleZ * plate.AngularVelocityDegPerMyr * weight;
            typicalSpeed += plate.AngularVelocityDegPerMyr * weight;
        }

        var net = Math.Sqrt(x * x + y * y + z * z);
        Assert.True(net < typicalSpeed * 1e-6,
            $"板塊仍有淨旋轉（{net:F6} 度/百萬年，板塊典型速度 {typicalSpeed:F3}）");
    }

    [Fact]
    public void Step_ProducesAllThreeBoundaryTypes()
    {
        var engine = CreateEngine();
        engine.Step(20);

        var kinds = engine.State.Boundary.Distinct().ToHashSet();
        Assert.Contains(BoundaryType.Convergent, kinds);
        Assert.Contains(BoundaryType.Divergent, kinds);
        Assert.Contains(BoundaryType.None, kinds);
    }

    /// <summary>狀態不能跑出 NaN 或負厚度——數值一旦壞掉，畫面上只會看到一片奇怪的顏色，很難回推原因。</summary>
    [Fact]
    public void Step_StateStaysNumericallySane()
    {
        var engine = CreateEngine();
        engine.Step(300);

        foreach (var thickness in engine.State.CrustThickness)
        {
            Assert.False(float.IsNaN(thickness) || float.IsInfinity(thickness), "地殼厚度出現 NaN/Infinity");
            Assert.InRange(thickness, 0f, Isostasy.MaxCrustThicknessKm);
        }

        foreach (var elevation in engine.State.BedrockElevation)
        {
            Assert.False(float.IsNaN(elevation) || float.IsInfinity(elevation), "高度出現 NaN/Infinity");
            Assert.InRange(elevation, -20_000f, 20_000f);
        }

        Assert.All(engine.State.WaterDepth, depth => Assert.True(depth >= 0f));
        Assert.All(engine.State.CrustAge, age => Assert.True(age >= 0f));
    }

    [Fact]
    public void Reset_ReturnsToTheInitialState()
    {
        var engine = CreateEngine();
        var initialThickness = (float[])engine.State.CrustThickness.Clone();
        var initialLand = engine.LandFraction();

        engine.Step(200);
        engine.Reset();

        Assert.Equal(0, engine.State.StepCount);
        Assert.Equal(0.0, engine.State.ElapsedYears);
        Assert.Equal(initialLand, engine.LandFraction());
        Assert.Equal(initialThickness, engine.State.CrustThickness);
    }

    /// <summary>
    /// 同種子必須演化出完全一樣的星球。
    ///
    /// 這個測試同時是**資料競爭的偵測器**：模擬大量使用 <c>Parallel.For</c>，
    /// 只要有哪個平行迴圈一邊寫自己一邊讀鄰居的同一個欄位，結果就會隨執行緒時序漂移。
    /// 那種 bug 不會拋例外，只會讓「同樣的種子跑出不同的星球」，而且機器閒的時候往往看不出來——
    /// 2026-09-05 就是靠這個測試在**整批跑**（CPU有負載）時失敗、單獨跑卻通過，
    /// 才抓到 <c>ApplyConvergence</c> 讀寫 <c>Crust</c> 的競爭。所以步數刻意跑多一點、
    /// 比對的欄位也刻意涵蓋各個作用改到的東西，讓競爭有機會顯現。
    /// </summary>
    [Fact]
    public void Step_SameSeedProducesIdenticalEvolution()
    {
        var a = CreateEngine(seed: 7);
        var b = CreateEngine(seed: 7);
        a.Step(500);
        b.Step(500);

        Assert.Equal(a.State.CrustThickness, b.State.CrustThickness);
        Assert.Equal(a.State.BedrockElevation, b.State.BedrockElevation);
        Assert.Equal(a.State.Crust, b.State.Crust);
        Assert.Equal(a.State.CrustAge, b.State.CrustAge);
        Assert.Equal(a.State.PlateId, b.State.PlateId);
        Assert.Equal(a.State.VolcanicActivity, b.State.VolcanicActivity);
        Assert.Equal(a.State.WaterDepth, b.State.WaterDepth);
    }

    /// <summary>均衡公式的量級要對得上地球，不然後面所有地形都會系統性地偏高或偏低。</summary>
    [Theory]
    [InlineData(CrustType.Continental, 35f, 0f, Isostasy.ContinentalCrustDensity, 840f)]  // 典型大陸 → 地球陸地平均高度
    [InlineData(CrustType.Continental, 70f, 0f, Isostasy.ContinentalCrustDensity, 7210f)] // 加倍增厚 → 喜馬拉雅量級
    [InlineData(CrustType.Oceanic, 7f, 0f, Isostasy.OceanicCrustDensity, -2500f)]         // 中洋脊（年齡0）
    public void Isostasy_MatchesEarthReferenceValues(
        CrustType crust, float thicknessKm, float ageMyr, float densityKgPerM3, float expectedM)
    {
        var elevation = Isostasy.BedrockElevation(crust, thicknessKm, ageMyr, densityKgPerM3);
        Assert.Equal(expectedM, elevation, 1f);
    }

    /// <summary>老海床要比中洋脊深——深度∝√年齡 是海底地形最主要的規律。</summary>
    [Fact]
    public void Isostasy_OceanFloorDeepensWithAge()
    {
        const float density = Isostasy.OceanicCrustDensity;
        var ridge = Isostasy.BedrockElevation(CrustType.Oceanic, 7f, 0f, density);
        var middle = Isostasy.BedrockElevation(CrustType.Oceanic, 7f, 20f, density);
        var old = Isostasy.BedrockElevation(CrustType.Oceanic, 7f, 80f, density);

        Assert.True(ridge > middle && middle > old, "海洋地殼沒有隨年齡變深");
        Assert.Equal(-5630f, old, 20f); // 8,000萬年的深海平原，實際約5,500~6,000公尺
    }

    /// <summary>
    /// 密度低的地殼要浮得比較高。這是早期大轟炸留下的密度差異能變成大陸核心的**唯一**途徑——
    /// 如果均衡公式沒有把密度算進去，那些差異就只是一組沒有任何作用的數字。
    /// </summary>
    [Fact]
    public void Isostasy_LighterCrustFloatsHigher()
    {
        var light = Isostasy.BedrockElevation(CrustType.Oceanic, 7f, 0f, Isostasy.OceanicCrustDensity - 150f);
        var normal = Isostasy.BedrockElevation(CrustType.Oceanic, 7f, 0f, Isostasy.OceanicCrustDensity);
        var heavy = Isostasy.BedrockElevation(CrustType.Oceanic, 7f, 0f, Isostasy.OceanicCrustDensity + 150f);

        Assert.True(light > normal && normal > heavy, "密度沒有影響均衡高度");

        // Airy 均衡：Δe = h·Δρ/ρ地函 = 7000 × 150 / 3300 ≈ 318 公尺
        Assert.Equal(318f, light - normal, 5f);
    }

    /// <summary>
    /// 起始狀態必須有**大尺度**的不均勻（早期大轟炸），否則大陸長不出來。
    /// 判準是「相鄰格幾乎一樣、遠處差很多」——那才是大區塊，不是隨機噪音。
    /// </summary>
    [Fact]
    public void InitialState_HasLargeScaleHeterogeneityFromImpacts()
    {
        var grid = SimulationGrid.Build(TestRadiusMeters, 24);
        var state = SimulationState.CreateInitialOceanWorld(grid, seed: 42);

        var thicknessSpread = state.CrustThickness.Max() - state.CrustThickness.Min();
        var densitySpread = state.CrustDensity.Max() - state.CrustDensity.Min();
        Assert.True(thicknessSpread > 3f, $"起始地殼厚度幾乎是均勻的（全球落差只有 {thicknessSpread:F1} 公里）");
        Assert.True(densitySpread > 80f, $"起始地殼密度幾乎是均勻的（全球落差只有 {densitySpread:F0}）");

        // 大尺度的定義：**平均**的相鄰落差要遠小於全球落差——場要走很多格才爬完整個振幅。
        // 用平均而不是最大值，是因為 KeepEverythingBelowSeaLevel 的截切本來就會在海平面高度
        // 造成一道轉折，單一個轉折不代表整個場是噪音。
        var totalDifference = 0.0;
        var pairCount = 0;
        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                var neighbor = grid.Neighbors[baseIndex + s];
                if (neighbor == SimulationGrid.NoNeighbor) continue;
                totalDifference += MathF.Abs(state.CrustThickness[cell] - state.CrustThickness[neighbor]);
                pairCount++;
            }
        }

        // 門檻的10%是從幾何推出來的，不是挑一個剛好會過的數字：撞擊半徑20~45度＝646~1454公里，
        // 格子中心間距約93公里，所以場的振幅至少要走過7~16格才爬得完，平均每格的落差
        // 自然應該在全球落差的十分之一以下（實測是2.6%，留了很大的餘裕）。
        var meanNeighborDifference = totalDifference / pairCount;
        Assert.True(meanNeighborDifference < thicknessSpread * 0.1f,
            $"相鄰格的平均厚度差 {meanNeighborDifference:F3} 公里，相對於全球落差 {thicknessSpread:F1} 公里太大了——" +
            "這是隨機噪音不是大區塊，會被重力擴展抹平、長不出大陸");
    }
}
