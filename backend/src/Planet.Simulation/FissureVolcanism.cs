namespace Planet.Simulation;

/// <summary>
/// 裂縫火山：重板塊的斷層帶裂開，岩漿沿裂縫上湧，堆成火山島。
///
/// ## 為什麼是「重」的板塊
///
/// 密度高的海洋板塊在均衡上沉得更低（<c>Isostasy.BedrockElevation</c> 的 <c>densityLift</c> 是負的），
/// 形成更深的海盆。**陸地不是來自這些板塊自己浮起來，而是來自堆在它們上面的火山**——
/// 負浮力讓板塊持續承受拉張與剪切應力，斷層因此比周圍活躍，岩漿沿著裂縫上湧堆成火山島。
/// 地球上的夏威夷正是坐落在又老又重的太平洋板塊上。
///
/// ## 跟另外三種火山的差別
///
/// | 種類 | 發生在哪 | 隨什麼移動 |
/// |---|---|---|
/// | 中洋脊 | 張裂邊界 | 邊界本身 |
/// | 島弧 | 聚合邊界 | 邊界本身 |
/// | 地函熱柱（<see cref="MantlePlumes"/>） | 板塊內部任何地方 | **不動**（固定在地函），板塊從上面滑過 |
/// | 裂縫（這裡） | 重板塊的斷層帶 | 斷層帶所在的位置 |
///
/// ## 噴發口是離散的，不是整條邊界都在噴
///
/// 如果讓斷層帶上每一格都噴，堆出來的是一道連續的牆而不是「島嶼」。真實的裂隙噴發集中在
/// 少數幾個火山中心（岩漿要有夠大的通道才上得來），所以這裡用格子索引的雜湊挑出
/// 一部分的格子當噴發口。用雜湊而不是亂數是為了**位置穩定**：
/// 同一個地方會持續噴上百萬年，才堆得起一座島；每步重新亂數挑的話只會抹出一片薄薄的均勻層。
/// </summary>
public sealed class FissureVolcanism
{
    /// <summary>
    /// 一座火山中心涵蓋多少平方公里。
    ///
    /// **這個值一定要是面積、不能是「每幾格」**（2026-09-07 踩過，正是 CLAUDE.md
    /// 那條「凡是幾格的參數都要改寫成物理量」的實例）：原本寫死成「每40格挑一個」，
    /// 而符合噴發條件的格子（重板塊＋位在斷層/張裂帶）在兩種解析度下數量差很多——
    /// n=150 有329格、1/40 抽出13座噴發口；n=24 只有47格、期望值1.2、**實際抽中0**。
    /// 也就是**整個裂縫火山在所有測試裡從來沒有執行過一次**。
    ///
    /// 後果不只是機制沒作用：`VentStrengthKmPerMyr` 被連續三次「實測校準」，
    /// 而校準時它根本沒在動——把強度從0.25掃到0.8、甚至整個關成0，
    /// 重板塊的厚度都停在25公里不動（那25公里其實來自聚合處的標記堆疊）。
    /// **死掉的參數會讓人以為自己在調模型，其實在調一個沒接線的旋鈕。**
    ///
    /// 取7,665：等於 n=150 原本的行為（40 × 該解析度每格191.6平方公里），
    /// 換算成一座噴發口約每88公里一座。
    /// </summary>
    private const double AreaPerVentSquareKm = 7_665;

    /// <summary>
    /// 噴發強度（公里地殼／百萬年），對應 <see cref="PlateSet.DensePlateAnomalyKgPerM3"/> 那個密度異常。
    ///
    /// **這是校準出來的，校準目標是「島真的要露出水面」**：
    /// 重板塊上約20百萬年的海床深度是 2500+350×√20 ≈ 4,070公尺，再加上密度異常造成的
    /// 約320公尺下沉，總共要補上約4,400公尺。海洋地殼每多1公里厚度浮起182公尺，
    /// 所以需要約24公里的額外地殼。噴發口的地殼會隨板塊移出，實際停留時間有限，
    /// 取1.2。原本是3.5，那是在「一格一個標記、平均地殼27~34公里」的時代調的；
    /// 改成一格多個標記之後地殼只有12~15公里，同樣的噴發量就把整片重板塊墊成高原了——
    /// 實測重板塊露出水面的比例達43~76%，而全球陸地只有16~46%，
    /// **重板塊比平均還更露出，跟「密度高沉得更低」完全相反**（2026-09-06 第二次校準）。
    ///
    /// 先前兩次實測夾出3.5的過程（保留供參）：
    /// 6.0 讓**整片重板塊被墊成火山高原**而不是「深海盆＋零星火山島」——重地殼平均水深
    /// 只剩2,802公尺，反而比一般海洋的6,811公尺還淺，跟「密度高沉得更深」完全相反；
    /// 2.0 則反過來，1億年之後一座島都撐不住。噴發口間距也一併從12格拉到40格，
    /// 讓島是離散的而不是連成一道牆。
    /// </summary>
    private const double VentStrengthKmPerMyr = 1.2;

    /// <summary>噴發物有多少比例落在鄰居上（山體的側翼）。跟 <see cref="MantlePlumes"/> 同一個道理。</summary>
    private const double NeighborShare = 0.4;

    /// <summary>診斷用：最近一步堆積的地殼厚度總和（公里·格）。地殼收支要把它算進去。</summary>
    public double LastAddedCrustKm { get; private set; }

    /// <summary>
    /// 推進一步的裂縫火山作用。
    /// </summary>
    /// <param name="grid">網格。</param>
    /// <param name="state">模擬狀態，地殼厚度與火山活動會被更新。</param>
    /// <param name="plates">板塊配置，用來判斷哪些格子屬於重板塊。</param>
    /// <param name="deltaMyr">這一步代表多少百萬年。</param>
    public void Step(SimulationGrid grid, SimulationState state, PlateSet plates, double deltaMyr)
    {
        LastAddedCrustKm = 0;

        // 把「每座噴發口涵蓋多少面積」換算回這個解析度的「每幾格挑一個」
        var ventSpacing = Math.Max(1,
            (int)Math.Round(AreaPerVentSquareKm / (grid.AverageCellAreaSquareMeters / 1e6)));

        for (var cell = 0; cell < state.CellCount; cell++)
        {
            var anomaly = plates.Plates[plates.PlateIdByCell[cell]].DensityAnomalyKgPerM3;
            if (anomaly <= 0f) continue;

            // 應力集中在斷層與拉張的地方。轉形邊界就是斷層與地震帶
            // （見 BoundaryType.Transform 的說明），張裂邊界則是直接拉開。
            var boundary = state.Boundary[cell];
            if (boundary is not (BoundaryType.Transform or BoundaryType.Divergent)) continue;

            if (!IsVent(cell, ventSpacing)) continue;

            // 密度異常越大 → 負浮力越大 → 應力越大 → 裂縫越發達
            var scale = anomaly / PlateSet.DensePlateAnomalyKgPerM3;
            var rate = VentStrengthKmPerMyr * scale;
            var added = rate * deltaMyr;

            LastAddedCrustKm += Erupt(state, cell, (float)(added * (1.0 - NeighborShare)), rate * (1.0 - NeighborShare));

            var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
            var neighborCount = 0;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                if (grid.Neighbors[baseIndex + s] != SimulationGrid.NoNeighbor) neighborCount++;
            }
            if (neighborCount == 0) continue;

            var perNeighbor = added * NeighborShare / neighborCount;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                var neighbor = grid.Neighbors[baseIndex + s];
                if (neighbor == SimulationGrid.NoNeighbor) continue;
                LastAddedCrustKm += Erupt(state, neighbor, (float)perNeighbor, rate * NeighborShare / neighborCount);
            }
        }
    }

    /// <summary>
    /// 堆積火山物質並記錄活躍度。
    ///
    /// **刻意不呼叫 <c>TryMakeContinental</c>**：這些島是玄武岩堆在海洋地殼上的火山島
    /// （夏威夷、冰島那一類），是「島」但不是「大陸」。重板塊本來就被指定為永遠是海洋地殼，
    /// 就算呼叫了那道門也會擋下來，這裡不呼叫是為了讓意圖直接寫在程式碼上。
    /// </summary>
    /// <returns>實際堆上去的厚度（公里）。可能小於要求的量，因為有上限。</returns>
    private static float Erupt(SimulationState state, int cell, float thicknessKm, double activityKmPerMyr)
    {
        var before = state.CrustThickness[cell];
        state.CrustThickness[cell] = MathF.Min(before + thicknessKm, Isostasy.MaxCrustThicknessKm);
        state.VolcanicActivity[cell] += (float)activityKmPerMyr;
        return state.CrustThickness[cell] - before;
    }

    /// <summary>
    /// 這一格是不是噴發口。用格子索引的雜湊而不是亂數，位置才會穩定（見類別說明）。
    /// 直接用 <c>cell % spacing</c> 不行——網格的索引有空間規律，那樣會挑出一條條的直線。
    /// </summary>
    /// <param name="cell">格子索引。</param>
    /// <param name="spacing">每幾格挑一個，由 <see cref="AreaPerVentSquareKm"/> 換算而來。</param>
    private static bool IsVent(int cell, int spacing)
    {
        var hash = (uint)cell * 2654435761u; // Knuth 的乘法雜湊
        return hash % (uint)spacing == 0;
    }
}
