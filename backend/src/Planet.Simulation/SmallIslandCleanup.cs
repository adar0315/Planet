namespace Planet.Simulation;

/// <summary>
/// 把又小又矮的孤立陸塊沉回水面下。
///
/// ## 為什麼這件事放在匯出而不是模擬迴圈
///
/// 這跟 <see cref="SeaOutletBreach"/> 的定位刻意不同。鑿開海洋出口是**持續發生的地質作用**
/// （屏障會一再形成、也會一再被切穿），所以必須跟著板塊一起跑；
/// 而碎島是**快照的表面現象**，成因在取樣本身：
///
/// marker-in-cell 每約兩步就把標記重新取樣到格子上一次，格子的地殼厚度因而跳動，
/// **實測 n=150 每步約有 1,200 格在海陸之間翻面**——而海平面的 ±3 公尺震盪
/// 只能解釋其中約 210 格，其餘六分之五來自取樣本身。
/// 也就是說碎島不是某個機制寫錯，而是取樣解析度的產物：任何一個瞬間都會有一批
/// 格子剛好落在海平面附近。
///
/// 這種東西不能靠加強侵蝕解決——實測把海浪侵蝕改成按暴露邊數計費（孤立小島因此
/// 被削得比海岸快5~6倍）只讓一格島從669降到529，因為**生成速率才是主導**。
/// 再加大就會開始吃掉真正的海岸線。而降低取樣雜訊要走「一格多個標記」，
/// 那條路先前量測過會把大陸抹薄到12~14公里（見 <c>PlateTectonics.MarkersPerCell</c>），
/// 是獨立的題目。
///
/// 所以留在模擬裡讓它照物理跑，交付地形時再把雜訊清掉。
///
/// ## 判準是「又小又矮」，刻意**不**看火山活動
///
/// 第一版寫成「有火山活動就保留」，本意是留住夏威夷／冰島那一型的火山島。
/// 那條例外錯得剛好相反——**中洋脊正是火山活動最旺的地方**，
/// 那條規則會把整條洋脊的島鏈完整保留下來，而那些正是最該清掉的。
///
/// 洋脊會浮出水面是這顆星球的校準問題：新生洋脊地殼在 −2,500 公尺
/// （<c>Isostasy</c> 的 RidgeDepthM），而實測海平面在 −2,687 公尺，
/// 於是每一段新海床都**先以陸地的形式誕生**，要等熱沉降約6步（0.29百萬年）才沉下去。
/// 畫面上因此每條洋脊沿線都掛著一串壽命六步的臨時島嶼。
///
/// 而那條例外本來就多餘：實測真正的火山島高度 p90 是 562 公尺、最高 2,534，
/// 遠高於下面那道 200 公尺的門檻，光靠高度就留得住。
/// </summary>
public static class SmallIslandCleanup
{
    /// <summary>
    /// 小於這個面積的孤立陸塊才是候選（平方公里）。
    ///
    /// 寫成面積而不是格數（CLAUDE.md 第4條）：n=150 一格約192平方公里、n=24 約7,481。
    /// 取 1,000：約等於 n=150 的5格。實測 n=150 的陸塊分布是
    /// 1格529塊、2格119塊、3~5格89塊、6~10格29塊，而**6塊500格以上的陸塊就佔了
    /// 92%的陸地面積**——碎島在面積上微不足道，在數量上卻壓倒性。
    /// </summary>
    public const double MaxCleanedIslandAreaSquareKm = 1000.0;

    /// <summary>
    /// 高過海平面這麼多就留著，不管多小（公尺）。
    ///
    /// 取 200：實測一格島的高度分布是 p25 = 9.4、中位 = 28.4、p75 = 101.9、p90 = 562.5，
    /// 所以這條線大約保留最高的兩成——那一段才是真的地形，其餘是海平面附近的取樣殘留。
    /// 對照組是最大陸塊的中位高度 1,873 公尺，跟碎島差了六十幾倍。
    /// </summary>
    public const float MinKeptIsletHeightMeters = 200f;

    /// <summary>
    /// 沉下去之後要低於海平面幾公尺。
    ///
    /// 不能只沉到剛好齊平：清完之後若還要繼續跑模擬，均衡與侵蝕每步都在動，
    /// 齊平的格子馬上又會冒出來。取 50 公尺留一點餘裕。
    /// </summary>
    private const float SinkBelowSeaLevelMeters = 50f;

    /// <summary>
    /// 把符合條件的孤立陸塊沉回水面下。
    ///
    /// **改的是地殼厚度不是高度**：高度每步都由 <see cref="Isostasy.BedrockElevation"/>
    /// 從厚度重算，直接改高度會被下一次均衡蓋掉。呼叫端做完要重新跑一次均衡與注水，
    /// 水深才會跟著更新（見 <c>SimulationEngine.CleanUpSmallIslands</c>）。
    /// </summary>
    /// <param name="grid">網格。</param>
    /// <param name="state">模擬狀態，<see cref="SimulationState.CrustThickness"/> 會被修改。</param>
    /// <param name="maxIslandAreaSquareKm">
    /// 覆寫面積門檻（平方公里）。正式用途留空即可，用 <see cref="MaxCleanedIslandAreaSquareKm"/>。
    ///
    /// **存在的理由是粗網格測試**：n=24 一格就有 7,481 平方公里，比預設門檻 1,000 還大，
    /// 於是換算出來的格數上限是 **0**，每一塊都立刻被判為「太大」——
    /// 清除在粗網格上完全不會執行。那個行為嚴格說是對的（7,481平方公里的島本來就不小，
    /// 波多黎各才9,100），但它讓機制在測試解析度上變成死路，
    /// 而「裝了卻從未觸發、事後統計又看不出來」正是 CLAUDE.md 3.5 記的那種坑。
    /// </param>
    /// <returns>清了幾塊、留了幾塊、各是什麼原因。</returns>
    public static SmallIslandCleanupReport Apply(
        SimulationGrid grid, SimulationState state, double? maxIslandAreaSquareKm = null)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(state);

        var areaLimit = maxIslandAreaSquareKm ?? MaxCleanedIslandAreaSquareKm;
        var maxCells = (int)(areaLimit * 1_000_000.0 / grid.AverageCellAreaSquareMeters);
        var seaLevel = state.SeaLevelMeters;
        var target = seaLevel - SinkBelowSeaLevelMeters;

        var label = new int[state.CellCount];
        Array.Fill(label, -1);

        var members = new List<int>();
        var stack = new Stack<int>();

        var sunkComponents = 0;
        var sunkCells = 0;
        var keptTooBig = 0;
        var keptTooHigh = 0;
        var sunkVolcanic = 0;

        for (var start = 0; start < state.CellCount; start++)
        {
            if (label[start] >= 0 || state.WaterDepth[start] > 0f) continue;

            var component = start;
            members.Clear();
            stack.Push(start);
            label[start] = component;

            var highest = float.MinValue;
            var volcanic = false;
            var tooBig = false;

            while (stack.Count > 0)
            {
                var cell = stack.Pop();
                members.Add(cell);

                highest = MathF.Max(highest, state.SurfaceElevationAt(cell) - seaLevel);
                if (state.VolcanicActivity[cell] > 0f) volcanic = true;

                // 已經超過門檻就不必再走完整塊大陸——最大的陸塊有五萬多格，
                // 每次都走完等於把整個清理變成 O(陸地格數 × 塊數)。
                if (members.Count > maxCells)
                {
                    tooBig = true;
                    break;
                }

                var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
                for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
                {
                    var neighbor = grid.Neighbors[baseIndex + s];
                    if (neighbor == SimulationGrid.NoNeighbor) continue;
                    if (label[neighbor] >= 0 || state.WaterDepth[neighbor] > 0f) continue;

                    label[neighbor] = component;
                    stack.Push(neighbor);
                }
            }

            if (tooBig)
            {
                // 剩下的格子也要標記，否則會被當成新的一塊重新走一次
                while (stack.Count > 0)
                {
                    var cell = stack.Pop();
                    var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
                    for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
                    {
                        var neighbor = grid.Neighbors[baseIndex + s];
                        if (neighbor == SimulationGrid.NoNeighbor) continue;
                        if (label[neighbor] >= 0 || state.WaterDepth[neighbor] > 0f) continue;
                        label[neighbor] = component;
                        stack.Push(neighbor);
                    }
                }

                keptTooBig++;
                continue;
            }

            if (highest >= MinKeptIsletHeightMeters) { keptTooHigh++; continue; }

            // 火山活動只記錄、不再當成保留的理由（見類別說明：那條例外會保住整條洋脊島鏈）
            if (volcanic) sunkVolcanic++;

            foreach (var cell in members) Sink(state, cell, target);
            sunkComponents++;
            sunkCells += members.Count;
        }

        return new SmallIslandCleanupReport
        {
            SunkComponents = sunkComponents,
            SunkCells = sunkCells,
            KeptTooBig = keptTooBig,
            KeptTooHigh = keptTooHigh,
            SunkVolcanic = sunkVolcanic,
        };
    }

    /// <summary>把一格的地殼削薄到地表低於目標高度。</summary>
    private static void Sink(SimulationState state, int cell, float target)
    {
        var surface = state.SurfaceElevationAt(cell);
        if (surface <= target) return;

        var referenceDensity = state.Crust[cell] == CrustType.Continental
            ? Isostasy.ContinentalCrustDensity
            : Isostasy.OceanicCrustDensity;
        var metersPerKm = Isostasy.MetersPerKmOfCrust
            + 1000f * (referenceDensity - state.CrustDensity[cell]) / Isostasy.MantleDensity;

        if (metersPerKm <= 1f) return;

        var thinnedKm = (surface - target) / metersPerKm;
        state.CrustThickness[cell] = MathF.Max(
            Isostasy.MinCrustThicknessKm, state.CrustThickness[cell] - thinnedKm);
    }
}

/// <summary><see cref="SmallIslandCleanup.Apply"/> 的結果。</summary>
public sealed record SmallIslandCleanupReport
{
    /// <summary>沉掉了幾塊孤立陸塊。</summary>
    public required int SunkComponents { get; init; }

    /// <summary>沉掉的格數總計。</summary>
    public required int SunkCells { get; init; }

    /// <summary>因為面積夠大而留下的陸塊數。</summary>
    public required int KeptTooBig { get; init; }

    /// <summary>因為夠高而留下的小島數（見 <see cref="SmallIslandCleanup.MinKeptIsletHeightMeters"/>）。</summary>
    public required int KeptTooHigh { get; init; }

    /// <summary>
    /// 沉掉的碎島裡有幾塊帶著火山活動。
    ///
    /// **這個數字大不代表出錯**：中洋脊沿線的臨時島嶼正是火山活動最旺的地方，
    /// 而那些就是最該清掉的（見類別說明）。真正的火山島靠高度門檻留得住。
    /// </summary>
    public required int SunkVolcanic { get; init; }
}
