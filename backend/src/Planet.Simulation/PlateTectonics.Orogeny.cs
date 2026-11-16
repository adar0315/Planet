using Planet.Simulation.Representation;

namespace Planet.Simulation;

// 聚合與造山：隱沒、造山帶、過厚地殼的擴散（2026-11-17 從 PlateTectonics.cs 拆出來，內容沒改）
public sealed partial class PlateTectonics
{
    /// <summary>
    /// 處理聚合邊界：決定誰隱沒、在上覆板塊造山與島弧增厚、島弧夠厚時變成大陸地殼。
    /// 鄰居一律讀快照（<see cref="_crustSnapshot"/>），避免平行迴圈的資料競爭。
    /// </summary>
    /// <param name="grid">模擬網格。</param>
    /// <param name="state">模擬狀態（會被修改）。</param>
    /// <param name="plates">板塊集合。</param>
    /// <param name="deltaMyr">這一步的時間長度（百萬年）。</param>
    private void ApplyConvergence(SimulationGrid grid, SimulationState state, PlateSet plates, double deltaMyr)
    {
        // 先把地殼類型拍一張快照，平行迴圈裡讀鄰居一律讀快照。
        //
        // **這不只是為了正確，是為了不要有資料競爭**：迴圈會寫 state.Crust[cell]（島弧變成大陸地殼），
        // 同時又要讀 state.Crust[neighbor]，而那個鄰居可能正被別的執行緒寫。
        // 症狀非常難查——同一個種子在機器閒的時候結果一致、負載一高就分岔，
        // 所以「同種子演化相同」的測試單獨跑會過、跟其他測試一起跑才會失敗（2026-09-05 踩過）。
        // 用快照同時也比較物理：每一格的判斷都基於同一個時刻的世界，
        // 而不是取決於鄰居有沒有剛好先被算到。
        Array.Copy(state.Crust, _crustSnapshot, state.CellCount);
        Parallel.For(0, state.CellCount, cell => _liftSnapshot[cell] = Isostasy.BedrockElevation(
            state.Crust[cell], state.CrustThickness[cell], state.CrustAge[cell], state.CrustDensity[cell]));

        // 1. 每條線段這一步供應多少地殼。這裡只算預算，還不動地殼。
        Array.Clear(_orogenBudget, 0, state.CellCount);
        Parallel.For(0, state.CellCount, cell =>
        {
            if (state.Boundary[cell] != BoundaryType.Convergent) return;

            var neighbor = _convergingNeighbor[cell];
            if (neighbor < 0) return;

            var mine = _crustSnapshot[cell];
            var theirs = _crustSnapshot[neighbor];

            var isOverriding = (mine, theirs) switch
            {
                (CrustType.Continental, CrustType.Oceanic) => true,
                (CrustType.Oceanic, CrustType.Continental) => false,
                (CrustType.Continental, CrustType.Continental) => true, // 對撞，兩邊都增厚

                // **都是海洋：浮得高的當上盤，不是年輕的**（2026-09-07 改）。
                //
                // 年齡只是密度的代理變數（老的比較冷、比較重），但這個模型裡密度是**明寫的**
                // ——重板塊有 DensityAnomalyKgPerM3。用年齡判斷等於忽略那個異常。
                //
                // 改用均衡高度也讓兩個層級一致：標記層級的聚合格迴圈（Rasterize 步驟3）早就是
                // 「誰浮得高誰留在地表」，只有這裡還在用另一套規則。
                //
                // **老實說：這個改動單獨量不出效果**（重板塊厚度 25.0 vs 25.0 公里）。
                // 留著是因為「忽略明寫的密度異常、改用年齡這個代理變數」本身就是錯的，
                // 不是因為它修好了什麼。
                _ => _liftSnapshot[cell] >= _liftSnapshot[neighbor],
            };

            if (!isOverriding) return;

            // **速率取整條線段的平均，不是這一格自己的**：逐格的值是從「自己最強的那個鄰居」
            // 算出來的，在格子尺度上很跳；一條隱沒帶整體被推擠的速率才是有物理意義的量。
            // 權重則來自線段的長度與離端點的距離（見 TraceConvergentSegments）。
            var convergenceKm = _segmentMeanConvergence[cell] / 1000.0 * deltaMyr;
            var addedKm = convergenceKm * ThickeningPerConvergence * _convergenceWeight[cell];
            if (addedKm <= 0) return;

            _orogenBudget[cell] = addedKm;

            // **島弧火山留在線上，不跟著攤開。** 變形帶是寬的，火山弧卻是窄的
            // （日本、安地斯的火山鏈只有幾十公里寬）——脫水熔融發生在板片到達特定深度的那條線上。
            state.VolcanicActivity[cell] += (float)(addedKm / Math.Max(deltaMyr, 1e-9));
        });

        // 2. 從線往**自己這一側的內陸**泛洪，畫出造山帶的範圍。
        //    只往同一個板塊擴散——「線後方」指的就是上盤那一側。
        var spacingKm = grid.AverageCellSpacingMeters / 1000.0;
        var maxHops = Math.Max(1, (int)(OrogenWidthKm / spacingKm));
        var bandCells = BuildOrogenBand(grid, state, plates, maxHops);
        LastOrogenCellCount = bandCells;

        // 3. 每條線段的預算，攤到它所屬的帶上。權重在線上最大、往內陸線性遞減到0。
        Array.Clear(_segmentBudget, 0, LastSegmentCount);
        Array.Clear(_segmentProfileSum, 0, LastSegmentCount);
        for (var cell = 0; cell < state.CellCount; cell++)
        {
            if (_orogenBudget[cell] > 0) _segmentBudget[_segmentId[cell]] += _orogenBudget[cell];

            var segment = _orogenSegment[cell];
            if (segment < 0) continue;
            _segmentProfileSum[segment] += OrogenProfile(_orogenHops[cell], maxHops);
        }

        Array.Clear(_arcAdded, 0, state.CellCount);
        Parallel.For(0, state.CellCount, cell =>
        {
            var segment = _orogenSegment[cell];
            if (segment < 0) return;

            var profileSum = _segmentProfileSum[segment];
            if (profileSum <= 0) return;

            var share = OrogenProfile(_orogenHops[cell], maxHops) / profileSum;
            var addedKm = _segmentBudget[segment] * share;
            if (addedKm <= 0) return;

            _arcAdded[cell] = addedKm;
            state.CrustThickness[cell] = MathF.Min(
                state.CrustThickness[cell] + (float)addedKm, Isostasy.MaxCrustThicknessKm);

            // 岩漿分異：島弧的岩漿是分異過的，加上去的物質比原本的海洋地殼輕，
            // 所以地殼一邊變厚一邊**變輕**。這是海洋地殼能真的轉成大陸地殼的化學面，
            // 只增厚而密度不變的話那只是一塊比較厚的海洋地殼。
            var addedFraction = (float)(addedKm / Math.Max(state.CrustThickness[cell], 1e-6f));
            state.CrustDensity[cell] +=
                (Isostasy.ContinentalCrustDensity - state.CrustDensity[cell]) * MathF.Min(addedFraction, 1f);

            // 增生到夠厚就變成大陸地殼——全海洋星球長出陸地的關鍵一步
            if (_crustSnapshot[cell] == CrustType.Oceanic
                && state.CrustThickness[cell] >= Isostasy.ContinentalThresholdKm)
            {
                state.TryMakeContinental(cell);
            }
        });
    }

    /// <summary>
    /// 造山帶內的分配曲線：線上最大、往內陸線性遞減到帶緣為0。
    /// 真實的變形強度也是從隱沒帶往內陸遞減的（安地斯的地殼厚度從海岸往東遞減）。
    /// </summary>
    private static double OrogenProfile(int hops, int maxHops) =>
        Math.Max(0.0, 1.0 - (double)hops / (maxHops + 1));

    /// <summary>
    /// 從聚合線往上盤內陸泛洪，畫出造山帶的範圍，並記下每一格離線幾格、屬於哪一條線段。
    ///
    /// 只往**同一個板塊**擴散——「線後方」指的就是上盤那一側；跨過線就是另一塊板塊，
    /// 那邊是隱沒下去的一方，不會被抬升。
    ///
    /// 走訪是逐層的廣度優先，種子依格子索引排序放入，所以結果與執行緒排程無關。
    /// </summary>
    /// <returns>落在造山帶內的格數。</returns>
    private int BuildOrogenBand(SimulationGrid grid, SimulationState state, PlateSet plates, int maxHops)
    {
        Array.Fill(_orogenSegment, -1, 0, grid.CellCount);
        Array.Fill(_orogenHops, 0, 0, grid.CellCount);

        var count = 0;
        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            if (_orogenBudget[cell] <= 0) continue;
            _orogenSegment[cell] = _segmentId[cell];
            _orogenHops[cell] = 0;
            _orogenQueue[count++] = cell;
        }

        for (var head = 0; head < count; head++)
        {
            var cell = _orogenQueue[head];
            var hops = _orogenHops[cell];
            if (hops >= maxHops) continue;

            var plate = plates.OwnerByCell[cell];
            var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
            for (var n = 0; n < SimulationGrid.MaxNeighborsPerCell; n++)
            {
                var neighbor = grid.Neighbors[baseIndex + n];
                if (neighbor == SimulationGrid.NoNeighbor) continue;
                if (_orogenSegment[neighbor] >= 0) continue;
                if (plates.OwnerByCell[neighbor] != plate) continue;

                // **只往大陸地殼裡擴散**（2026-09-07 量測後加的）。
                //
                // 縮短量攤進海洋地殼是沒有用的：以1公里/百萬年跑5,000萬年是50公里的縮短量，
                // 全部由15公里寬的最前排吸收，那一格縮短超過100%、厚度翻倍、越過大陸門檻；
                // 攤到400公里寬的帶上只縮短12.5%，7公里變成7.9公里，永遠不會變成大陸。
                // **均勻分攤的縮短造不出大陸，集中的縮短才行。**
                // 實測把帶開放給海洋地殼時，大陸格數 22,965 → 14,962（流速0.05）→ 12,413（0.40），
                // 三種流速全都是稀釋。
                //
                // 但抬升發生在**既有的大陸**上就完全正確：西藏是寬闊高原，是因為印度撞進來時
                // 後面本來就已經是大陸。而且大陸岩石圈遠比海洋岩石圈軟——
                // 這正是高原長在大陸上、而海洋上的島弧總是窄的原因。
                if (_crustSnapshot[neighbor] != CrustType.Continental) continue;

                _orogenSegment[neighbor] = _orogenSegment[cell];
                _orogenHops[neighbor] = hops + 1;
                _orogenQueue[count++] = neighbor;
            }
        }

        return count;
    }

    /// <summary>
    /// 重力擴展（造山帶重力崩解，orogenic collapse）：地殼厚到一定程度就撐不住自己的重量，
    /// 會像一坨太厚的糖漿一樣往側向流開。這是西藏之所以是一片**寬闊高原**而不是一根尖峰的原因，
    /// 也是為什麼地球的大陸地殼厚度普遍收斂在35~40公里、而不是有些地方薄有些地方無限厚。
    ///
    /// **這一段是「大陸會長大」而不只是「長高」的關鍵**（2026-09-05 補上）：
    /// 在加這段之前，質量平衡其實是對的——10億年下來平均厚度增加1.38公里，換算成0.059 km³/年，
    /// 跟地球的1.8 km³/年按本星球表面積（地球的8.45%）縮放後的0.152 km³/年同一個數量級。
    /// problem 不在產量，在**分布**：只有14個格子越過大陸門檻，然後它們每次聚合都繼續贏、
    /// 一路增厚到75公里的上限就飽和，把所有成長囤在自己身上，旁邊的格子一格都分不到。
    /// 結果是10億年後陸地只佔0.23%（地球是29%）。把超厚的部分擴散給鄰居，
    /// 才會把「厚度」換成「面積」。
    /// </summary>
    /// <summary>
    /// 造山帶內地殼側向流動的速率（每百萬年抹平厚度差的比例）。
    ///
    /// ## 為什麼只在造山帶內流
    ///
    /// 這是使用者提出的「推動的時候，線的後面一片區塊也要一起抬升」實際生效的那一步。
    /// 縮短量原本全部由最前排那一格吸收——因為板塊是**剛體**旋轉，整塊板塊每個標記都用
    /// 同一個歐拉旋轉在動，後面完全不變形。真實的板塊只有內部是剛性的，
    /// 邊界附近會內部變形，所以造山帶是寬帶：安地斯約500公里、西藏約1,000公里。
    ///
    /// **先前試過「用厚度差驅動流動」而且失敗過**（見 <see cref="GravitationalSpreadingThresholdKm"/>
    /// 的說明）：失敗原因是它作用在**每一個**地方，於是正常的大陸邊緣永遠在流失——
    /// 一塊35公里的大陸旁邊必然貼著7公里的海洋地殼，落差28公里超過任何合理門檻，
    /// 實測起始4.55%的陸地到6億年只剩0.35%。
    ///
    /// 這裡的差別是**把它限制在活躍的造山帶內**（<c>_orogenSegment</c> 標出來的那條帶）。
    /// 物理上正是這樣：活躍造山帶的地殼是熱的、軟的、會流（西藏的地殼流 channel flow，
    /// 也是西藏成為一片寬闊高原而不是一根尖峰的原因）；穩定的克拉通邊緣是冷的、硬的，不流。
    ///
    /// 也試過用重力擴展的門檻來加寬（把 40 降到 34、28），那條路**行不通**：
    /// 一格40公里的地殼分給6個7公里的海洋鄰居，鄰居只變成7.5公里、離25公里的大陸門檻還很遠，
    /// 而來源自己掉下去了——實測大陸格數 22,965 → 19,771 → 18,568，是稀釋不是加寬。
    /// 差別在於造山帶內的鄰居本來就厚，流過去才推得過門檻。
    /// </summary>
    private const double OrogenFlowRatePerMyr = 0.15;

    /// <summary>
    /// 造山帶內的地殼側向流動：把最前排吸收掉的縮短量攤到線後方整條帶上。
    ///
    /// 只在同一條造山帶內、而且只從厚流到薄，所以是守恆的重分配，不會憑空生出地殼。
    /// 帶外一律不動——那是這個作法跟先前失敗那版最關鍵的差別（見 <see cref="OrogenFlowRatePerMyr"/>）。
    /// </summary>
    private void FlowWithinOrogenBand(SimulationGrid grid, SimulationState state, double deltaMyr)
    {
        Array.Copy(state.CrustThickness, _spreadBuffer, grid.CellCount);

        // 上限0.1跟重力擴展同一個理由：一格最多6個鄰居，每個都流出這個比例的話總流出0.6倍，
        // 再大就會流過頭、在相鄰兩格之間來回震盪而不是擴散開。
        var factor = (float)Math.Min(OrogenFlowRatePerMyr * deltaMyr, 0.1);

        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            var segment = _orogenSegment[cell];
            if (segment < 0) continue;

            var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                var neighbor = grid.Neighbors[baseIndex + s];
                if (neighbor == SimulationGrid.NoNeighbor) continue;
                if (_orogenSegment[neighbor] != segment) continue;

                var difference = state.CrustThickness[cell] - state.CrustThickness[neighbor];
                if (difference <= 0f) continue;

                var flow = difference * factor;
                _spreadBuffer[cell] -= flow;
                _spreadBuffer[neighbor] += flow;
            }
        }

        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            if (_orogenSegment[cell] < 0) continue;
            state.CrustThickness[cell] = MathF.Min(_spreadBuffer[cell], Isostasy.MaxCrustThicknessKm);

            // 流過來的地殼一樣可以厚到變成大陸地殼——這就是「線後面一片區塊跟著抬升」
            if (state.Crust[cell] == CrustType.Oceanic
                && state.CrustThickness[cell] >= Isostasy.ContinentalThresholdKm)
            {
                state.TryMakeContinental(cell);
            }
        }
    }

    /// <summary>重力擴展：過厚的地殼往較薄的鄰居流出一部分厚度（out-of-place，用 <see cref="_spreadBuffer"/> 暫存）。</summary>
    /// <param name="grid">模擬網格。</param>
    /// <param name="state">模擬狀態（會被修改地殼厚度）。</param>
    /// <param name="deltaMyr">這一步的時間長度（百萬年）。</param>
    private void SpreadOverthickenedCrust(SimulationGrid grid, SimulationState state, double deltaMyr)
    {
        Array.Copy(state.CrustThickness, _spreadBuffer, grid.CellCount);

        // 上限0.1是數值穩定性的要求：一格最多有6個鄰居，每個都流出這個比例的話總流出量是0.6倍，
        // 再大就會流過頭、在相鄰兩格之間來回震盪而不是擴散開。
        var factor = (float)Math.Min(GravitationalSpreadingRatePerMyr * deltaMyr, 0.1);

        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            // 只有**超出自身均衡厚度**的部分會流，不是跟鄰居比。
            var excess = state.CrustThickness[cell] - GravitationalSpreadingThresholdKm;
            if (excess <= 0f) continue;

            var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                var neighbor = grid.Neighbors[baseIndex + s];
                if (neighbor == SimulationGrid.NoNeighbor) continue;
                if (state.CrustThickness[neighbor] >= state.CrustThickness[cell]) continue;

                var flow = excess * factor;
                _spreadBuffer[cell] -= flow;
                _spreadBuffer[neighbor] += flow;
            }
        }

        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            state.CrustThickness[cell] = MathF.Min(_spreadBuffer[cell], Isostasy.MaxCrustThicknessKm);

            // 被擴展過來的地殼一樣可以厚到變成大陸地殼——大陸就是這樣往外長大的
            if (state.Crust[cell] == CrustType.Oceanic
                && state.CrustThickness[cell] >= Isostasy.ContinentalThresholdKm)
            {
                state.TryMakeContinental(cell);
            }
        }
    }
}
