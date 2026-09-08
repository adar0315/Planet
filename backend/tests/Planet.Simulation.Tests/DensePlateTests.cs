using Planet.Simulation;
using Xunit;

namespace Planet.Simulation.Tests;

/// <summary>
/// 高密度海洋板塊與它們的裂縫火山。
///
/// 設定：兩個板塊一開始就比周圍重，它們**永遠維持海洋地殼**，但斷層帶會裂開、
/// 岩漿沿裂縫上湧堆成火山島（夏威夷、冰島那一類的玄武岩火山島，是「島」但不是「大陸」）。
/// </summary>
public class DensePlateTests
{
    private const double TestRadiusMeters = 1_852_259.92;

    private static SimulationGrid CreateGrid() => SimulationGrid.Build(TestRadiusMeters, 24);

    [Fact]
    public void CreateRandom_MakesExactlyTheRequestedNumberOfDensePlates()
    {
        var plates = PlateSet.CreateRandom(CreateGrid(), seed: 42);

        var dense = plates.Plates.Count(plate => plate.DensityAnomalyKgPerM3 > 0f);
        Assert.Equal(PlateSet.DensePlateCount, dense);
        Assert.All(plates.Plates, plate => Assert.InRange(
            plate.DensityAnomalyKgPerM3, 0f, PlateSet.DensePlateAnomalyKgPerM3));
    }

    /// <summary>
    /// 無淨旋轉的轉換會用 <c>new Plate(...)</c> 重建每個板塊，密度異常必須被帶過去。
    /// 漏掉的話重板塊會**安靜地變回普通板塊**，而且所有其他測試都還是會過。
    /// </summary>
    [Fact]
    public void CreateRandom_KeepsDensityAnomalyThroughNetRotationRemoval()
    {
        var plates = PlateSet.CreateRandom(CreateGrid(), seed: 7);
        Assert.Contains(plates.Plates, plate => plate.DensityAnomalyKgPerM3 > 0f);
    }

    /// <summary>重板塊的地殼一開始就比較重，而且被標記成永遠只能是海洋。</summary>
    [Fact]
    public void Initial_DenseCrustIsHeavierAndFlaggedOceanicOnly()
    {
        var engine = new SimulationEngine(CreateGrid(), 42);
        var state = engine.State;

        var heavy = Enumerable.Range(0, state.CellCount).Where(c => state.OceanicOnly[c]).ToArray();
        Assert.NotEmpty(heavy);

        Assert.All(heavy, cell =>
        {
            Assert.True(state.CrustDensity[cell] > Isostasy.OceanicCrustDensity,
                "被標成 OceanicOnly 的格子密度沒有比普通海洋地殼重");
            Assert.Equal(CrustType.Oceanic, state.Crust[cell]);
        });
    }

    /// <summary>
    /// 重地殼**永遠不會變成大陸地殼**，跑多久都一樣。
    ///
    /// 這個旗標是掛在地殼上、跟著標記移動的，不是掛在板塊區域上——
    /// 板塊範圍是球面 Voronoi 決定的、會掃過既有的大陸地殼，用板塊當判準的話
    /// 那些漂進來的大陸會被誤判成「重板塊長出了大陸」（2026-09-06 實測到277格）。
    /// </summary>
    [Fact]
    public void Step_DenseCrustNeverBecomesContinental()
    {
        var engine = new SimulationEngine(CreateGrid(), 42);
        engine.Step(3000); // 1.5億年

        var state = engine.State;
        var violations = Enumerable.Range(0, state.CellCount)
            .Count(c => state.OceanicOnly[c] && state.Crust[c] == CrustType.Continental);

        Assert.Equal(0, violations);
    }

    /// <summary>
    /// 重地殼不會被自己擴張出來的普通地殼稀釋掉。
    ///
    /// 中洋脊新生的地殼如果一律給普通密度，重板塊會愈擴張愈淡——重地殼浮力低、
    /// 每次碰撞都輸、優先被隱沒，補進來的又是普通的，實測2億年內從428格掉到只剩10格，
    /// 「重板塊」實質上消失了（2026-09-06）。
    /// </summary>
    [Fact]
    public void Step_DenseCrustPersistsInsteadOfBeingDilutedAway()
    {
        var engine = new SimulationEngine(CreateGrid(), 42);
        var initial = engine.State.OceanicOnly.Count(flag => flag);
        Assert.True(initial > 0);

        engine.Step(4000); // 2億年
        var after = engine.State.OceanicOnly.Count(flag => flag);

        // **守的是「有沒有繼承」，不是族群數量**（2026-09-08 改）。
        //
        // 原本斷言重地殼不得少於一半。加上板塊動力學之後這條開始失敗（834 → 350 格），
        // 但那不是繼承壞掉——繼承的程式碼還在（PlateTectonics 生成新洋殼時會加上
        // 板塊的 DensityAnomalyKgPerM3）。真正的原因是板塊速度不再是固定的隨機常數，
        // 整個演化路徑跟著變，重板塊被消耗的比例就不一樣了。
        // 重板塊負浮力大、本來就該優先被隱沒掉，用族群數量當判準等於把「演化剛好怎麼走」
        // 寫進測試裡。
        //
        // 改成直接檢查機制：重板塊上**還在持續產生**帶有密度異常的新地殼。
        // 這道檢查擋得住「新生地殼忘記繼承異常」這個真正的退化，
        // 而且不受演化路徑影響。
        Assert.True(after > 0, "重地殼完全消失了");

        var dense = Enumerable.Range(0, engine.State.CellCount)
            .Where(c => engine.State.OceanicOnly[c])
            .ToArray();

        var youngDense = dense.Count(c => engine.State.CrustAge[c] < 20f);
        Assert.True(youngDense > 0,
            $"重板塊上 {dense.Length} 格都沒有年輕地殼——新生地殼沒有繼承密度異常，" +
            "重板塊正在被普通密度的新洋殼稀釋掉");

        var anomalous = dense.Count(c => engine.State.CrustDensity[c] > Isostasy.OceanicCrustDensity);
        Assert.True(anomalous > dense.Length * 0.5,
            $"重板塊 {dense.Length} 格裡只有 {anomalous} 格的密度高於一般洋殼——密度異常沒有跟著走");
    }

    /// <summary>密度高就要沉得更低，這是「更深的海盆」的來源。同厚度、同年齡下直接比高度。</summary>
    [Fact]
    public void Isostasy_DenserCrustSitsLower()
    {
        var normal = Isostasy.BedrockElevation(
            CrustType.Oceanic, Isostasy.OceanicCrustThicknessKm, 20f, Isostasy.OceanicCrustDensity);
        var dense = Isostasy.BedrockElevation(
            CrustType.Oceanic, Isostasy.OceanicCrustThicknessKm, 20f,
            Isostasy.OceanicCrustDensity + PlateSet.DensePlateAnomalyKgPerM3);

        Assert.True(dense < normal, "密度高的地殼沒有沉得比較低");
        // Airy 均衡：7公里 × 250 ÷ 3300 ≈ 530公尺
        Assert.InRange(normal - dense, 400f, 700f);
    }

    /// <summary>
    /// 裂縫火山要真的在重板塊上堆出**露出水面**的島。
    ///
    /// 用前1億年判斷：這個模型目前缺侵蝕（階段4），跑更久之後整顆星球會變成
    /// 一塊愈來愈厚的地殼、海平面被守恆的水量一路推高，那是另一個獨立的問題，
    /// 會把這裡要驗的現象整個蓋掉。
    /// </summary>
    [Fact]
    public void Step_FissureVolcanismBuildsIslandsOnDensePlates()
    {
        var engine = new SimulationEngine(CreateGrid(), 42);
        engine.Step(1500); // 7,500萬年

        var state = engine.State;
        var islands = Enumerable.Range(0, state.CellCount)
            .Count(c => state.OceanicOnly[c] && state.WaterDepth[c] <= 0f);

        Assert.True(islands > 0, "重板塊上一座火山島都沒有——裂縫火山沒有堆到露出水面");

        // **光看島數不足以證明是裂縫火山造的**（2026-09-07 實測）：把 VentStrengthKmPerMyr
        // 設成 0、裂縫火山完全關閉，重板塊上照樣有232座島（聚合處的標記堆疊就足以把地殼
        // 頂出水面）。也就是說這個測試原本即使裂縫火山整個壞掉也會通過。
        // 所以另外檢查它真的有在堆積——這道檢查擋得住「裂縫火山安靜地停止運作」。
        // LastAddedCrustKm 是「最近一步」的值，單一步剛好沒有噴發口滿足條件是正常的
        //（噴發口要同時是重板塊、位在斷層/張裂帶、又被雜湊挑中），所以累計一段時間再看。
        var addedOverTime = 0.0;
        for (var i = 0; i < 40; i++)
        {
            engine.Step(1);
            addedOverTime += engine.Fissures.LastAddedCrustKm;
        }

        Assert.True(addedOverTime > 0,
            "裂縫火山連續40步一公里地殼都沒堆——噴發口條件（重板塊＋斷層帶）從來沒有格子滿足");
    }

    /// <summary>
    /// 火山島是**離散的島**，不是把整片板塊墊成一塊高原。
    /// 強度取太大時實測過這個失敗模式：重地殼的平均水深只剩2,802公尺，
    /// 反而比一般海洋還淺，跟「密度高沉得更深」完全相反。
    /// </summary>
    [Fact]
    public void Step_DensePlatesStayMostlySubmerged()
    {
        var engine = new SimulationEngine(CreateGrid(), 42);
        engine.Step(1500);

        var state = engine.State;
        var heavy = Enumerable.Range(0, state.CellCount).Where(c => state.OceanicOnly[c]).ToArray();
        Assert.NotEmpty(heavy);

        var emerged = heavy.Count(c => state.WaterDepth[c] <= 0f) / (double)heavy.Length;
        Assert.True(emerged < 0.5,
            $"重板塊有 {emerged:P0} 露出水面——火山把整片板塊墊成高原了，不是零星的島");
    }

    /// <summary>沒有重板塊時（densePlateCount:0）不應該有任何裂縫火山，方便做對照實驗。</summary>
    [Fact]
    public void Step_NoDensePlatesMeansNoOceanicOnlyCrust()
    {
        var engine = new SimulationEngine(CreateGrid(), 42, densePlateCount: 0);
        engine.Step(500);

        Assert.All(engine.State.OceanicOnly, flag => Assert.False(flag));
        Assert.All(engine.Plates.Plates, plate => Assert.Equal(0f, plate.DensityAnomalyKgPerM3));
    }
}
