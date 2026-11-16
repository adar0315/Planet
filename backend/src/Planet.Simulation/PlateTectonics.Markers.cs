using Planet.Simulation.Representation;

namespace Planet.Simulation;

// 標記法（v1）：取樣、標記的配置與回收、存檔、浮力與增生（2026-11-17 從 PlateTectonics.cs 拆出來，內容沒改）
public sealed partial class PlateTectonics
{
    /// <summary>
    /// 把標記取樣到格子上：每個標記落到最近的格子，一格多個標記＝聚合，一格沒有＝張裂。
    /// </summary>
    private void Rasterize(SimulationGrid grid, SimulationState state, PlateSet plates)
    {
        Array.Fill(_ownerMarker, -1, 0, grid.CellCount);
        Array.Fill(_spareAtCell, -1, 0, grid.CellCount);

        // 1. 每個標記算出自己落在哪一格（彼此無相依，可以平行）。
        //
        //    **用局部搜尋而不是全域的 FindNearestCell**：兩次取樣之間標記最多只漂移
        //    半個格子中心間距（<see cref="RasterizeThresholdInCellSpacings"/> 保證的），
        //    所以新的所在格必定是「上次那一格或它的鄰居」，比對7個候選就夠。
        //    全域桶搜尋是這一步最貴的東西——n=150 時每2步就要對22.5萬個標記做一次
        //    （格距只有14.9公里，板塊一步就走4公里），實測整個 PlateTectonics.Step
        //    每步要102ms，幾乎全部花在這裡。
        Parallel.For(0, _markerCount, marker =>
        {
            if (!_markerAlive[marker]) return;

            var x = _markerX[marker];
            var y = _markerY[marker];
            var z = _markerZ[marker];
            var from = _markerCell[marker];

            var best = from;
            var bestDot = x * grid.UnitVector[from * 3]
                + y * grid.UnitVector[from * 3 + 1]
                + z * grid.UnitVector[from * 3 + 2];

            var baseIndex = from * SimulationGrid.MaxNeighborsPerCell;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                var neighbor = grid.Neighbors[baseIndex + s];
                if (neighbor == SimulationGrid.NoNeighbor) continue;

                var dot = x * grid.UnitVector[neighbor * 3]
                    + y * grid.UnitVector[neighbor * 3 + 1]
                    + z * grid.UnitVector[neighbor * 3 + 2];
                if (dot <= bestDot) continue;
                bestDot = dot;
                best = neighbor;
            }

            _markerTarget[marker] = best;
            _markerCell[marker] = best;
        });

        // 2. 依所在格把標記分桶（CSR：先數、做前綴和、再填）。
        //    一格現在會有好幾個標記，不再是「先到先佔、後到的就撞上」。
        Array.Clear(_cellMarkerCount, 0, grid.CellCount);
        for (var marker = 0; marker < _markerCount; marker++)
        {
            if (_markerAlive[marker]) _cellMarkerCount[_markerTarget[marker]]++;
        }

        // **每格至少保留 MarkersPerCell 個位置**，不是剛好配現有的數量：
        // 張裂邊界要能「補到滿」，沒有預留空間的話新標記無處可放（2026-09-06 踩過）。
        var running = 0;
        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            _cellMarkerStart[cell] = running;
            running += Math.Max(_cellMarkerCount[cell], MarkersPerCell);
            _cellMarkerCount[cell] = 0; // 重新當成填入時的游標
        }

        for (var marker = 0; marker < _markerCount; marker++)
        {
            if (!_markerAlive[marker]) continue;
            var cell = _markerTarget[marker];
            _markersByCell[_cellMarkerStart[cell] + _cellMarkerCount[cell]++] = marker;
        }

        // 3. 太擠的格子：只有**真的在聚合邊界**才隱沒掉多出來的標記。
        //    不是聚合邊界的擁擠只是取樣的疏密不均，讓它擠著就好——格子的值是平均，
        //    多幾個標記不會讓地殼憑空變厚，反而是把資訊留著等下一輪自然散開。
        // 3a. 非聚合邊界的擁擠：那只是取樣錯配，不是隱沒。
        //     留下浮得最高的，其餘放進暫存區，稍後補給附近的錯配空格——
        //     同一塊地殼重新指派到正確的格子，天生守恆。
        //
        //     **多出來的標記不可以直接丟掉**（2026-09-06 踩過）：錯配的碰撞與錯配的空格
        //     其實是同一件事的兩面——球面旋轉映射到離散網格不是完美的排列，
        //     所以有些格子拿到兩個標記、有些拿到零個。「多的丟掉、少的憑空生」
        //     等於同時開了一個沒被計數的沉與一個沒被計數的源，而那個源實測注入
        //     50~351 km³/年，是整顆星球失控變成厚地殼的引擎。把兩者配對就天生守恆。
        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            if (state.Boundary[cell] == BoundaryType.Convergent) continue;
            var from0 = _cellMarkerStart[cell];
            while (_cellMarkerCount[cell] > MarkersPerCell)
            {
                var worst = 0;
                var worstLift = float.MaxValue;
                for (var i = 0; i < _cellMarkerCount[cell]; i++)
                {
                    var lift = SubductionBuoyancyOf(_markersByCell[from0 + i]);
                    if (lift >= worstLift) continue;
                    worstLift = lift;
                    worst = i;
                }

                var spare = _markersByCell[from0 + worst];
                if (_spareAtCell[cell] < 0) _spareAtCell[cell] = spare;
                else _markerAlive[spare] = false; // 同一格擠了三個以上，罕見

                _markersByCell[from0 + worst] = _markersByCell[from0 + _cellMarkerCount[cell] - 1];
                _cellMarkerCount[cell]--;
            }
        }

        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            if (state.Boundary[cell] != BoundaryType.Convergent) continue;

            var from = _cellMarkerStart[cell];

            // 聚合處的兩種結局，靠**浮力差**分辨（2026-09-06 第二次修正）：
            //
            // - 浮力差大 → **隱沒**：重的那塊沉下去（海洋潛入大陸、老海床潛入年輕的）
            // - 浮力差小 → **碰撞增厚**：誰都沉不下去，標記擠在一起就是造山
            //   （在「厚度＝總和÷N」的語意下，擁擠本身就是增厚，不需要另外寫規則）
            //
            // 中間一度只留「超過75公里上限才隱沒」，那等於把依浮力挑選的機制整個拿掉——
            // 重板塊因此不再優先被回收，只會累積標記、變厚、浮起來：實測重板塊的地殼
            // 從19.6公里長到32.9（一般海洋只有10.1），密度異常也從+250被稀釋到+27，
            // 「密度高沉得更低」的設定完全失效。
            // 只隱沒「超過目標數」的部分，而且要浮力差夠大才算隱沒。
            //
            // 曾經拿掉數量上限、改成純由浮力差決定，那會一路隱沒到只剩1個標記——
            // 因為混合的格子永遠找得到超過500公尺的浮力差（2026-09-06 實測，大陸厚度沒有改善，
            // 反而把標記掃光）。
            while (_cellMarkerCount[cell] > MarkersPerCell)
            {
                // 找浮得最低的（最容易隱沒）與最高的（上盤，接收增生物）。
                //
                // **判準是浮力（厚度），不是年齡；「厚的留下」這條不能省，
                // 島弧會不會保存下來全看它**（2026-09-05 踩過，當時還寫在 ResolveCollision 裡）：
                // 原本只比年齡、而且贏家會把自己的厚度直接蓋到目標格上，於是聚合邊界上
                // ApplyConvergence 好不容易增厚的島弧，只要碰到一塊比較年輕的普通海洋地殼
                // 就被打回7公里。增厚只發生在聚合邊界，那裡正好也是碰撞最密集的地方，
                // 結果是一邊長一邊被砍：跑5億年最大厚度只在10~14公里之間震盪、
                // 平均值完全不動、一塊陸地都長不出來，而且沒有任何錯誤訊息。
                //
                // 用厚度當判準不是為了讓測試過，是因為浮力本來就取決於厚度而不是年齡——
                // 「大陸不會沉」其實就是這條規則的特例，原本只是被寫死成型別判斷、沒有推廣到厚度。
                var lowest = -1;
                var highest = -1;
                var lowestLift = float.MaxValue;
                var highestLift = float.MinValue;
                for (var i = 0; i < _cellMarkerCount[cell]; i++)
                {
                    var m = _markersByCell[from + i];
                    var lift = SubductionBuoyancyOf(m);
                    if (lift < lowestLift) { lowestLift = lift; lowest = i; }
                    if (lift > highestLift) { highestLift = lift; highest = i; }
                }
                if (lowest < 0 || highest < 0 || lowest == highest) break;

                // 浮力差不夠大就不是隱沒，是對撞增厚——留著讓它變厚，
                // 除非已經厚到超過地殼的物理上限。
                if (highestLift - lowestLift < SubductionBuoyancyContrastMeters
                    && CellThicknessFromMarkers(cell) <= Isostasy.MaxCrustThicknessKm)
                {
                    LastCollisionThickeningCount++;
                    break;
                }

                var loser = _markersByCell[from + lowest];
                var winner = _markersByCell[from + highest];

                if (_markerCrust[loser] == CrustType.Continental
                    && _markerCrust[winner] == CrustType.Continental)
                {
                    // **兩塊大陸對撞：物質無處可去，只能往上下增厚（山脈＋山根）。**
                    //
                    // 這一段在改寫 Rasterize 時整個漏掉了（2026-09-06/07）：原本寫在
                    // ResolveCollision 裡，把擁擠處理內聯之後那個方法就沒人呼叫了
                    // （那個死方法已於 2026-09-09 移除，它的歷史註解搬到上面與步驟3a），
                    // 大陸對撞從「吃掉對方一半的厚度」退化成只有增生的3%——**少了16倍**。
                    // 症狀是大陸長不大也長不厚：2億年後只有168格、碎成116塊、最大10格。
                    // 喜馬拉雅型造山是大陸變厚最主要的機制，缺了它大陸就只能靠島弧慢慢增生。
                    _markerThickness[winner] = MathF.Min(
                        _markerThickness[winner] + _markerThickness[loser] * ContinentalCollisionTransfer,
                        Isostasy.MaxCrustThicknessKm);

                    LastContinentCollisionCount++;
                }
                else
                {
                    // 一邊是海洋地殼：隱沒下去，但一部分會被刮下來增生到上盤
                    LastAccretionCrustKm += _markerThickness[loser] * AccretedFractionOfSubductedCrust;
                    LastSubductedCrustKm += _markerThickness[loser];
                    LastOceanSubductionCount++;
                    Accrete(winner, _markerThickness[loser]);
                }

                _markerAlive[loser] = false;

                // 從桶裡移除：把最後一個搬過來補洞
                _markersByCell[from + lowest] = _markersByCell[from + _cellMarkerCount[cell] - 1];
                _cellMarkerCount[cell]--;
            }
        }

        // 4. 完全沒有標記的格子。標記多於格子之後這會少很多，但仍要分辨兩種情況：
        //    真張裂 → 中洋脊新生地殼；其餘 → 取樣錯配，跟鄰居借。
        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            var existing = _cellMarkerCount[cell];

            // **張裂邊界要「補到滿」，不是只補完全空的格子**（2026-09-06，改成多標記時踩到）：
            // 中洋脊原本只在 count==0 時才生成新地殼，而一格多個標記之後空格幾乎消失
            // （錯配空格從86/步降到3.9/步），等於把造地殼的機制關掉了，隱沒卻照常運作——
            // 標記數從23,048一路掉到12,900、平均地殼厚只剩8.1公里、陸地1.8%。
            // 板塊真的在分開的地方，標記密度本來就會低於目標值，補到滿才是正確的海底擴張。
            if (state.Boundary[cell] == BoundaryType.Divergent && existing < MarkersPerCell)
            {
                for (var k = existing; k < MarkersPerCell; k++)
                {
                    var fresh = AllocateMarker();
                    if (fresh < 0) break;
                    SeedRidgeMarker(grid, state, plates, fresh, cell);
                    AppendToCell(cell, fresh);
                }
                continue;
            }

            if (existing > 0) continue;

            // 先看附近有沒有因為擁擠而被擠出來的標記，有就搬過來（守恆）
            if (TryTakeSpare(grid, cell, out var moved))
            {
                LastPairedFillCount++;
                _markerX[moved] = grid.UnitVector[cell * 3];
                _markerY[moved] = grid.UnitVector[cell * 3 + 1];
                _markerZ[moved] = grid.UnitVector[cell * 3 + 2];
                _markerCell[moved] = cell;
                _markerTarget[moved] = cell;
                _markerPlate[moved] = plates.PlateIdByCell[cell];
                AppendToCell(cell, moved);
                continue;
            }

            var marker = AllocateMarker();
            if (marker < 0) continue; // 容量用盡（正常不會發生），這格沿用上一輪的內容

            _markerX[marker] = grid.UnitVector[cell * 3];
            _markerY[marker] = grid.UnitVector[cell * 3 + 1];
            _markerZ[marker] = grid.UnitVector[cell * 3 + 2];
            _markerCell[marker] = cell;
            _markerTarget[marker] = cell;
            _markerPlate[marker] = plates.PlateIdByCell[cell];

            if (state.Boundary[cell] == BoundaryType.Divergent)
            {
                // 走不到這裡：上面的「補到滿」已經處理過張裂邊界。保留是為了萬一。
                // 中洋脊**製造**的地殼就是這7公里。
                // 不可以寫成「7 − 這格原本的厚度」：空格裡那個舊值是已經隨標記漂走的地殼，
                // 不是被摧毀的東西。那樣量出來的中洋脊會是負的（實測 −6.02 km³/年），
                // 讓人以為中洋脊在吃掉地殼，整份地殼收支也跟著對不起來（2026-09-06 自己踩的）。
                // 一個標記只代表這一格 1/MarkersPerCell 的地殼柱，所以換算成「格子當量」要除以 N。
        // 不除的話中洋脊的產量會被高估4倍（2026-09-06）。
        LastRidgeCrustKm += Isostasy.OceanicCrustThicknessKm / MarkersPerCell;
                _markerThickness[marker] = Isostasy.OceanicCrustThicknessKm;
                _markerAge[marker] = 0f;
                _markerOrganic[marker] = 0f; // 剛從地函冒出來的玄武岩上面不會有礁
                // **新生地殼要繼承這個板塊的密度異常**，否則重板塊會被自己擴張出來的普通地殼稀釋掉：
                // 重地殼浮力低、每次碰撞都輸、優先被隱沒，補進來的又是普通密度，
                // 實測2億年內重地殼從428格衰減到只剩10格，「重板塊」實質上消失了（2026-09-06）。
                _markerDensity[marker] =
                    Isostasy.OceanicCrustDensity + plates.Plates[_markerPlate[marker]].DensityAnomalyKgPerM3;
                _markerCrust[marker] = CrustType.Oceanic;
                _markerOceanicOnly[marker] =
                    plates.Plates[_markerPlate[marker]].IsOceanicOnly;

                // 中洋脊火山：速率的分母是「上次取樣到現在」而不是單一步長，
                // 否則會得到比島弧大兩個數量級的尖峰，畫出來只剩中洋脊在一閃一閃。
                state.VolcanicActivity[cell] +=
                    Isostasy.OceanicCrustThicknessKm / MathF.Max((float)_myrSinceLastRaster, 1e-6f);
            }
            else
            {
                LastMisfitFillCount++;
                BorrowFromNeighbours(grid, marker, cell);
            }

            AppendToCell(cell, marker);
        }

        // 4b. 還沒被配對用掉的暫存標記才真的丟棄。
        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            if (_spareAtCell[cell] < 0) continue;
            _markerAlive[_spareAtCell[cell]] = false;
            _spareAtCell[cell] = -1;
        }

        // 5. 把每一格的標記平均起來寫進格子。
        //    平均而不是取其中一個：一格有好幾個標記，取單一個等於丟掉其他標記帶的資訊。
        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            var count = _cellMarkerCount[cell];
            if (count <= 0) continue;

            // **取浮得最高的那個標記，不是取平均**（2026-09-06 實測後改）：
            // 平均是擴散性的——一格裡1個40公里的大陸標記配3個7公里的海洋標記，
            // 平均只有15公里，大陸就被抹掉了。實測用平均時大陸地殼厚度只有17~23公里
            // （低於25公里的大陸門檻），陸地永遠停在1.5~2.8%。
            //
            // 取最高的則跟這個模型原本的物理一致：**誰浮得高誰留在地表**
            // （見上面步驟3的聚合格迴圈）。格子顯示的是「表面看得到的那一塊」，
            // 其餘標記仍然存在、仍然跟著板塊移動，等上面那塊離開就換它們露出來。
            var top = -1;
            var topLift = float.MinValue;
            var first = -1;

            for (var i = 0; i < count; i++)
            {
                var m = MarkerAt(cell, i);
                if (first < 0) first = m;
                var lift = SurfaceLiftOf(m);
                if (lift > topLift) { topLift = lift; top = m; }

                // 地殼歸屬於「現在覆蓋在它上面」的那個板塊——板塊邊界會掃過地殼，
                // 被掃到的那一塊就換了主人，這正是隱沒帶把地殼從一塊板塊轉交給另一塊的過程
                _markerPlate[m] = plates.PlateIdByCell[cell];
            }

            if (top < 0) continue;

            // **厚度 = 落在這一格的標記厚度總和 ÷ MarkersPerCell。**
            //
            // 每個標記代表這一格 1/MarkersPerCell 的地殼柱，所以擠進來的標記多就變厚
            // （聚合造山）、少就變薄（張裂）——增厚與變薄是**搬運本身的結果**，
            // 不需要另外寫規則，而且這是唯一質量一致的取法。
            //
            // 曾經改成「取浮得最高的那一塊」，那等於每個標記各自代表一整根地殼柱，
            // 一格4個標記在標記族群裡就是4倍的質量、格子卻只顯示1根——實測中洋脊與隱沒的
            // 通量雙雙變成單標記版的3~4倍，地殼被高速回收、什麼都累積不起來（2026-09-06）。
            var totalThickness = 0f;
            for (var i = 0; i < count; i++) totalThickness += _markerThickness[MarkerAt(cell, i)];
            state.CrustThickness[cell] = totalThickness / MarkersPerCell;

            // 年齡、密度、型別取浮得最高的那一塊——那是「表面看得到的」，
            // 跟厚度這種體積量不同，它們是表層性質。
            state.CrustAge[cell] = _markerAge[top];
            state.CrustDensity[cell] = _markerDensity[top];

            // **礁灰岩也取最高的那一塊，不可以像厚度那樣加總**（2026-09-07 踩過）：
            // 取樣用總和、而回寫是把格子的值**絕對地**寫給格子裡的**每一個**標記
            // （見 WriteGridBackToMarkers）。這個配對對厚度是刻意的——它有
            // MaxCrustThicknessKm 上限，而且擁擠處理會殺掉多餘的標記，所以收斂。
            // 礁體沒有上限，一格暫時擠進2個標記就翻倍，下一輪再翻，是**指數放大**：
            // 實測最大厚度從8公里衝到211公里。
            //
            // 物理上取最高的也才對：兩塊各帶著礁體的地殼撞在一起，礁體不會相加，
            // 沉下去的那一塊連同它的礁體一起被隱沒掉。
            state.OrganicThickness[cell] = _markerOrganic[top];
            state.Crust[cell] = _markerCrust[top];
            state.OceanicOnly[cell] = _markerOceanicOnly[top];

            // **不能變成大陸的地殼，也不能厚成大陸**（2026-09-07 踩過）。
            //
            // OceanicOnly 的本意是「這塊地殼太重，不會變成大陸」（見 SimulationState.OceanicOnly），
            // 但原本只在 TryMakeContinental 那道門擋下轉換、沒有擋住**變厚**本身。
            // 於是重板塊照樣被聚合處的標記堆疊加厚，過了25公里的大陸門檻卻不會被改類別，
            // 就一路長到42公里還掛在「海洋地殼」，變成站得比一般海洋還高的高原——
            // 實測重板塊平均水深589公尺、一般海洋908公尺，**密度最高的板塊反而浮得最高**，
            // 跟這整個設計的前提正好相反。
            //
            // 露餡的是厚度分布：一般海洋在25公里以上**一格都沒有**（過門檻就轉成大陸、
            // 離開這個統計群），重板塊卻有48%的格子在28公里以上。
            // 平均厚度25公里這個數字本身看不出異常，要跟一般海洋擺在一起比才看得出來。
            //
            // 超過門檻的物質在物理上是被隱沒／拆離回收掉的，不會長成山根。
            if (state.OceanicOnly[cell])
            {
                state.CrustThickness[cell] =
                    MathF.Min(state.CrustThickness[cell], Isostasy.ContinentalThresholdKm);
            }

            _sampledThickness[cell] = state.CrustThickness[cell];
            _sampledAge[cell] = state.CrustAge[cell];
            _sampledDensity[cell] = state.CrustDensity[cell];
            _ownerMarker[cell] = first;
        }

        CompactMarkers();
    }

    /// <summary>在中洋脊生成一個新的海洋地殼標記。</summary>
    private void SeedRidgeMarker(
        SimulationGrid grid, SimulationState state, PlateSet plates, int marker, int cell)
    {
        _markerX[marker] = grid.UnitVector[cell * 3];
        _markerY[marker] = grid.UnitVector[cell * 3 + 1];
        _markerZ[marker] = grid.UnitVector[cell * 3 + 2];
        _markerCell[marker] = cell;
        _markerTarget[marker] = cell;
        _markerPlate[marker] = plates.PlateIdByCell[cell];

        LastRidgeCrustKm += Isostasy.OceanicCrustThicknessKm;
        _markerThickness[marker] = Isostasy.OceanicCrustThicknessKm;
        _markerAge[marker] = 0f;
        _markerOrganic[marker] = 0f; // 剛從地函冒出來的玄武岩上面不會有礁
        // 新生地殼要繼承這個板塊的密度異常，否則重板塊會被自己擴張出來的普通地殼稀釋掉
        _markerDensity[marker] =
            Isostasy.OceanicCrustDensity + plates.Plates[_markerPlate[marker]].DensityAnomalyKgPerM3;
        _markerCrust[marker] = CrustType.Oceanic;
        _markerOceanicOnly[marker] = plates.Plates[_markerPlate[marker]].IsOceanicOnly;

        // 中洋脊火山：速率的分母是「上次取樣到現在」而不是單一步長，
        // 否則會得到比島弧大兩個數量級的尖峰，畫出來只剩中洋脊在一閃一閃。
        state.VolcanicActivity[cell] +=
            Isostasy.OceanicCrustThicknessKm / MathF.Max((float)_myrSinceLastRaster, 1e-6f);
    }

    /// <summary>
    /// 把新標記掛到某一格的清單上。
    ///
    /// 前綴和已經替每一格保留了至少 <see cref="MarkersPerCell"/> 個位置，所以直接接在桶尾即可。
    /// </summary>
    private void AppendToCell(int cell, int marker)
    {
        _markersByCell[_cellMarkerStart[cell] + _cellMarkerCount[cell]] = marker;
        _cellMarkerCount[cell]++;
    }

    /// <summary>
    /// 找附近（自己與鄰居）有沒有因為擁擠而被擠出來的暫存標記，有就取出來。
    /// 刻意只找一圈鄰居：搬得太遠等於把地殼瞬移，那比憑空生成更不合理。
    /// </summary>
    private bool TryTakeSpare(SimulationGrid grid, int cell, out int marker)
    {
        if (_spareAtCell[cell] >= 0)
        {
            marker = _spareAtCell[cell];
            _spareAtCell[cell] = -1;
            return true;
        }

        var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
        for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
        {
            var neighbor = grid.Neighbors[baseIndex + s];
            if (neighbor == SimulationGrid.NoNeighbor || _spareAtCell[neighbor] < 0) continue;
            marker = _spareAtCell[neighbor];
            _spareAtCell[neighbor] = -1;
            return true;
        }

        marker = -1;
        return false;
    }

    /// <summary>診斷用：有幾個錯配空格是靠「搬被擠出來的標記」補上的（守恆路徑）。</summary>
    public int LastPairedFillCount { get; private set; }

    /// <summary>由落在這一格的標記算出地殼厚度（總和÷目標標記數，見 Rasterize 第5段）。</summary>
    private float CellThicknessFromMarkers(int cell)
    {
        var total = 0f;
        var count = _cellMarkerCount[cell];
        for (var i = 0; i < count; i++) total += _markerThickness[MarkerAt(cell, i)];
        return total / MarkersPerCell;
    }

    /// <summary>取出某一格的第 <paramref name="index"/> 個標記。</summary>
    private int MarkerAt(int cell, int index) => _markersByCell[_cellMarkerStart[cell] + index];

    /// <summary>
    /// 把標記與跨步驟保留的狀態寫進存檔（見 <see cref="SimulationSnapshot"/>）。
    ///
    /// **為什麼標記一定要存**：一開始的存檔版本只存格子、讀回時從格子重建標記（每格一個、放在格心），
    /// 以為「地形都一樣，差別只在次格尺度」。實測讀回續跑**第 1 步完全相同、第 2 步就有 9 個欄位分歧**——
    /// 標記約每兩步重新取樣到格子上一次，一取樣就露餡。即時觀看時服務重啟要接得回去，
    /// 接回去的必須是同一顆星球，所以要存完整狀態。
    ///
    /// 只存跨步驟會被讀到的東西。每步開頭就重算的暫存（聚合速率、線段、造山帶…）不存——
    /// <c>SimulationSnapshotTests.RoundTrip_ResumeIsBitIdenticalToUninterruptedRun</c> 會抓到漏存的。
    /// 陣列容量由網格決定，讀回的實例容量一樣，所以整個陣列照存，不必另記長度。
    /// </summary>
    /// <param name="writer">目標。</param>
    internal void WriteTo(BinaryWriter writer)
    {
        writer.Write((byte)Transport);
        if (_frames is not null)
        {
            _frames.WriteTo(writer);
            // 劇本自己的狀態：B 碰到 C 的時間（決定之後 B 怎麼推），沒存的話讀檔續跑 B 的速度馬上不一樣
            writer.Write(_scenario?.BContactMyr is not null);
            writer.Write(_scenario?.BContactMyr ?? 0.0);
            return;
        }

        writer.Write(_markerCount);
        writer.Write(_driftSinceRasterMeters);
        writer.Write(_myrSinceLastRaster);

        SimulationSnapshot.WriteArray(writer, _markerX);
        SimulationSnapshot.WriteArray(writer, _markerY);
        SimulationSnapshot.WriteArray(writer, _markerZ);
        SimulationSnapshot.WriteArray(writer, _markerThickness);
        SimulationSnapshot.WriteArray(writer, _markerAge);
        SimulationSnapshot.WriteArray(writer, _markerDensity);
        SimulationSnapshot.WriteArray(writer, _markerOrganic);
        SimulationSnapshot.WriteArray(writer, _markerCrust);
        SimulationSnapshot.WriteArray(writer, _markerOceanicOnly);
        SimulationSnapshot.WriteArray(writer, _markerPlate);
        SimulationSnapshot.WriteArray(writer, _markerAlive);
        SimulationSnapshot.WriteArray(writer, _markerTarget);
        SimulationSnapshot.WriteArray(writer, _markerCell);

        SimulationSnapshot.WriteArray(writer, _cellMarkerStart);
        SimulationSnapshot.WriteArray(writer, _cellMarkerCount);
        SimulationSnapshot.WriteArray(writer, _markersByCell);
        SimulationSnapshot.WriteArray(writer, _sampledThickness);
        SimulationSnapshot.WriteArray(writer, _sampledAge);
        SimulationSnapshot.WriteArray(writer, _sampledDensity);
        SimulationSnapshot.WriteArray(writer, _ownerMarker);
        SimulationSnapshot.WriteArray(writer, _spareAtCell);
    }

    /// <summary>從存檔讀回標記與跨步驟狀態。順序見 <see cref="WriteTo"/>。</summary>
    /// <param name="reader">來源。</param>
    internal void ReadFrom(BinaryReader reader)
    {
        var transport = (CrustTransport)reader.ReadByte();
        if (transport != Transport)
        {
            throw new InvalidDataException(
                $"存檔的地殼搬運方式是 {transport}，這個引擎是 {Transport}——兩種的內部狀態完全不同，不能互讀。");
        }
        if (_frames is not null)
        {
            _frames.ReadFrom(reader);
            var hasContact = reader.ReadBoolean();
            var contactMyr = reader.ReadDouble();
            if (_scenario is not null) _scenario.BContactMyr = hasContact ? contactMyr : null;
            return;
        }

        _markerCount = reader.ReadInt32();
        _driftSinceRasterMeters = reader.ReadDouble();
        _myrSinceLastRaster = reader.ReadDouble();

        SimulationSnapshot.ReadArray(reader, _markerX);
        SimulationSnapshot.ReadArray(reader, _markerY);
        SimulationSnapshot.ReadArray(reader, _markerZ);
        SimulationSnapshot.ReadArray(reader, _markerThickness);
        SimulationSnapshot.ReadArray(reader, _markerAge);
        SimulationSnapshot.ReadArray(reader, _markerDensity);
        SimulationSnapshot.ReadArray(reader, _markerOrganic);
        SimulationSnapshot.ReadArray(reader, _markerCrust);
        SimulationSnapshot.ReadArray(reader, _markerOceanicOnly);
        SimulationSnapshot.ReadArray(reader, _markerPlate);
        SimulationSnapshot.ReadArray(reader, _markerAlive);
        SimulationSnapshot.ReadArray(reader, _markerTarget);
        SimulationSnapshot.ReadArray(reader, _markerCell);

        SimulationSnapshot.ReadArray(reader, _cellMarkerStart);
        SimulationSnapshot.ReadArray(reader, _cellMarkerCount);
        SimulationSnapshot.ReadArray(reader, _markersByCell);
        SimulationSnapshot.ReadArray(reader, _sampledThickness);
        SimulationSnapshot.ReadArray(reader, _sampledAge);
        SimulationSnapshot.ReadArray(reader, _sampledDensity);
        SimulationSnapshot.ReadArray(reader, _ownerMarker);
        SimulationSnapshot.ReadArray(reader, _spareAtCell);
    }

    /// <summary>
    /// 地質作用（增生、造山、重力擴展、火山、侵蝕）改的是格子上的值，要寫回標記，
    /// 否則下一次取樣就會用舊的標記內容把這些改動整個蓋掉。
    ///
    /// **一格一個標記時要寫「絕對值」，不能寫差值**（2026-09-07 追出來的）。
    ///
    /// 差值寫回（FLIP）是為了保留「同一格裡多個標記之間的差異」才加的，
    /// 在 <see cref="MarkersPerCell"/> = 1 時那個差異根本不存在，而副作用很嚴重：
    /// 負的差值會把標記一路壓到 <see cref="Isostasy.MinCrustThicknessKm"/> 的下限再也回不來。
    /// 實測改回絕對值之後，**大陸從168格變成461格、最大陸塊從10格變成45格、
    /// 大陸厚度從31.4變成38.6公里**（地球是35~40）。
    ///
    /// 之後若要再走多標記，這裡要跟著改回差值——兩者是綁在一起的。
    ///
    /// 板塊座標系（<see cref="CrustTransport.PlateFrame"/>）走另一條路：見 <c>PlateFrameCrust.WriteBack</c>。
    /// </summary>
    public void WriteGridBackToMarkers(SimulationState state)
    {
        if (_frames is not null)
        {
            _frames.WriteBack(_grid, state);
            return;
        }

        Parallel.For(0, state.CellCount, cell =>
        {
            var count = _cellMarkerCount[cell];
            if (count <= 0) return;

            var deltaThickness = state.CrustThickness[cell] - _sampledThickness[cell];
            var deltaAge = state.CrustAge[cell] - _sampledAge[cell];
            var deltaDensity = state.CrustDensity[cell] - _sampledDensity[cell];

            for (var i = 0; i < count; i++)
            {
                var marker = MarkerAt(cell, i);
                _markerThickness[marker] = state.CrustThickness[cell];
                _markerAge[marker] = state.CrustAge[cell];
                _markerDensity[marker] = state.CrustDensity[cell];
                _markerOrganic[marker] = state.OrganicThickness[cell];
                // 型別與旗標是類別值，沒有差值可言，直接跟著格子走
                _markerCrust[marker] = state.Crust[cell];
                _markerOceanicOnly[marker] = state.OceanicOnly[cell];
            }

            _sampledThickness[cell] = state.CrustThickness[cell];
            _sampledAge[cell] = state.CrustAge[cell];
            _sampledDensity[cell] = state.CrustDensity[cell];
        });
    }


    /// <summary>
    /// 取樣錯配的空格：從鄰居的標記借地殼，借多少就扣多少，總量守恆。
    ///
    /// 借不到（鄰居也都沒有標記，正常不會發生）就給最薄的海洋地殼，
    /// 那是唯一不會憑空製造大量地殼的退路。
    /// </summary>
    private void BorrowFromNeighbours(SimulationGrid grid, int marker, int cell)
    {
        var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
        var donors = 0;
        var thickness = 0f;
        var age = 0f;
        var density = 0f;
        var organic = 0f;

        for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
        {
            var neighbor = grid.Neighbors[baseIndex + s];
            if (neighbor == SimulationGrid.NoNeighbor) continue;
            // **要用分桶找標記，不能用 _ownerMarker**（2026-09-07 踩過）：
            // _ownerMarker 在 Rasterize 開頭就被清成 -1，要到第5段（寫進格子）才填回去，
            // 而這個方法是在第4段（補空格）呼叫的——那時它全部是 -1，
            // 於是 donors 永遠是 0、每一次都掉進下面「借不到就給最薄地殼」的退路。
            // 實測每步觸發14次、跟「錯配空格裡沒被配對走的數量」完全吻合（50×28%），
            // 4,000步下來把海洋地殼打成3公里的次數平均每格10次——
            // 中洋脊明明生出7公里，海洋地殼的中位數卻只有3.8。
            if (_cellMarkerCount[neighbor] <= 0) continue;
            var donor = MarkerAt(neighbor, 0);

            donors++;
            thickness += _markerThickness[donor];
            age += _markerAge[donor];
            density += _markerDensity[donor];
            organic += _markerOrganic[donor];
        }

        if (donors == 0)
        {
            LastNoDonorCount++;
            _markerThickness[marker] = Isostasy.MinCrustThicknessKm;
            _markerAge[marker] = 0f;
            _markerDensity[marker] = Isostasy.OceanicCrustDensity;
            _markerOrganic[marker] = 0f;
            _markerCrust[marker] = CrustType.Oceanic;
            _markerOceanicOnly[marker] = false;
            return;
        }

        var borrowed = thickness / donors;
        _markerThickness[marker] = borrowed;
        _markerAge[marker] = age / donors;
        _markerDensity[marker] = density / donors;
        _markerOrganic[marker] = organic / donors;

        var perDonor = borrowed / donors * MisfitDeductionFraction;
        var continental = 0;
        var oceanicOnly = 0;
        for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
        {
            var neighbor = grid.Neighbors[baseIndex + s];
            if (neighbor == SimulationGrid.NoNeighbor) continue;
            if (_cellMarkerCount[neighbor] <= 0) continue;
            var donor = MarkerAt(neighbor, 0);

            _markerThickness[donor] = MathF.Max(
                Isostasy.MinCrustThicknessKm, _markerThickness[donor] - perDonor);
            if (_markerCrust[donor] == CrustType.Continental) continental++;
            if (_markerOceanicOnly[donor]) oceanicOnly++;
        }

        // 型別與旗標跟多數鄰居一致
        _markerCrust[marker] = continental * 2 > donors ? CrustType.Continental : CrustType.Oceanic;
        _markerOceanicOnly[marker] = oceanicOnly * 2 > donors;
    }

    /// <summary>
    /// 找一個空的標記槽並標成存活。滿了回傳 -1。
    ///
    /// **一定要在這裡就設 <c>_markerAlive</c>**（2026-09-06 踩過）：
    /// 漏掉的話新生的標記一出生就是死的，<see cref="CompactMarkers"/> 立刻把它擠掉，
    /// 而 <c>_remap</c> 對死標記從來沒賦值，於是 <c>_ownerMarker</c> 會殘留上一輪的舊索引、
    /// 多個格子共用同一個標記——<see cref="WriteGridBackToMarkers"/> 是平行的，
    /// 那就變成真正的資料競爭。症狀是同種子跑出不同的星球，而且因為執行緒分割在同一台機器上
    /// 通常是固定的，**失敗位置每次都一樣、看起來完全不像競爭**。
    /// </summary>
    private int AllocateMarker()
    {
        if (_markerCount < _markerX.Length)
        {
            _markerAlive[_markerCount] = true;
            return _markerCount++;
        }

        for (var i = 0; i < _markerCount; i++)
        {
            if (_markerAlive[i]) continue;
            _markerAlive[i] = true;
            return i;
        }
        return -1;
    }

    /// <summary>
    /// 把死掉（隱沒掉）的標記從陣列中擠掉，讓標記總數不會隨時間單向增長。
    /// <c>_ownerMarker</c> 會跟著更新，因為它存的是索引。
    /// </summary>
    private void CompactMarkers()
    {
        var write = 0;
        for (var read = 0; read < _markerCount; read++)
        {
            // 死掉的標記要明確標成 -1，不能留著上一輪的殘值——那會讓 _ownerMarker 指到別人身上
            if (!_markerAlive[read])
            {
                _remap[read] = -1;
                continue;
            }
            if (write != read)
            {
                _markerX[write] = _markerX[read];
                _markerY[write] = _markerY[read];
                _markerZ[write] = _markerZ[read];
                _markerThickness[write] = _markerThickness[read];
                _markerAge[write] = _markerAge[read];
                _markerDensity[write] = _markerDensity[read];
                _markerOrganic[write] = _markerOrganic[read];
                _markerCrust[write] = _markerCrust[read];
                _markerOceanicOnly[write] = _markerOceanicOnly[read];
                _markerCell[write] = _markerCell[read];
                _markerPlate[write] = _markerPlate[read];
                _markerAlive[write] = true;
            }
            _remap[read] = write;
            write++;
        }

        for (var cell = 0; cell < _ownerMarker.Length; cell++)
        {
            var marker = _ownerMarker[cell];
            if (marker >= 0) _ownerMarker[cell] = _remap[marker];
        }

        // **分桶陣列也存的是索引，一定要一起重新對應**（2026-09-06 踩過）：
        // 漏掉的話，取樣之後每一格的標記清單都指到別人身上，
        // 地質作用的回寫就會寫到錯誤的標記。症狀是「同種子跑出不同的星球」——
        // 那個測試就是為了抓這類問題放的，而且它比任何統計量都早發現。
        for (var cell = 0; cell < _ownerMarker.Length; cell++)
        {
            var start = _cellMarkerStart[cell];
            var count = _cellMarkerCount[cell];
            var kept = 0;
            for (var i = 0; i < count; i++)
            {
                var remapped = _remap[_markersByCell[start + i]];
                if (remapped < 0) continue; // 這一輪被隱沒掉了
                _markersByCell[start + kept++] = remapped;
            }
            _cellMarkerCount[cell] = kept;
        }

        Array.Fill(_markerAlive, false, write, _markerCount - write);
        _markerCount = write;
    }

    /// <summary>這個標記的地殼浮得多高（公尺）。就是均衡高度，用來決定碰撞時誰隱沒。</summary>
    /// <summary>
    /// 這個標記的地表高度。用來決定**誰露在表面**（同一格擠了好幾個標記時看得到哪一個）。
    ///
    /// **不要拿它來判斷誰會隱沒**——那是另一件事，用 <see cref="SubductionBuoyancyOf"/>。
    /// 混用是 2026-09-07 修掉的一個概念錯誤：地表高度是整根柱子的均衡結果，
    /// 能不能隱沒則是整個岩石圈相對於軟流圈的淨密度，兩個不同的積分。
    /// </summary>
    private float SurfaceLiftOf(int marker) => Isostasy.BedrockElevation(
        _markerCrust[marker], _markerThickness[marker], _markerAge[marker], _markerDensity[marker]);

    /// <summary>
    /// 這個標記的岩石圈淨浮力（正值浮、負值沉）。**隱沒與碰撞的勝負一律用這個判**。
    ///
    /// 它跟地表高度最關鍵的差別是：海洋岩石圈有一個**符號翻轉的年齡**（約18百萬年）——
    /// 年輕的浮、老的沉。地表高度只是單調變深，表達不出「年輕洋殼抗拒隱沒」這件事。
    /// 見 <see cref="Isostasy.LithosphereBuoyancyMeters"/>。
    /// </summary>
    private float SubductionBuoyancyOf(int marker) => Isostasy.LithosphereBuoyancyMeters(
        _markerCrust[marker], _markerThickness[marker], _markerDensity[marker], _markerAge[marker]);

    /// <summary>
    /// 把隱沒掉的地殼的一部分堆到留在地表的那一格上。
    ///
    /// **這是大陸能「集中」而不是攤平成一層的關鍵**（2026-09-05 補上）：
    /// 在加這段之前，隱沒的地殼是整塊憑空消失的，於是增厚只能靠
    /// <see cref="ApplyConvergence"/> 均勻地灑在全部約1,400個聚合格上，
    /// 絕大多數永遠到不了25公里的大陸門檻，只是把海底從7公里墊到8.6公里。
    /// 質量平衡其實一直是對的（10億年產出0.059 km³/年，與地球按面積縮放後的0.152同一量級），
    /// 錯的是**分布**——10億年後陸地只佔0.14%，而同樣的地殼量若集中成35公里厚可以蓋掉5.7%。
    ///
    /// 增生是**質量轉移**而不是憑空產生（上盤拿到的正好是下盤失去的一部分），
    /// 所以天生就會集中在同一個邊界上反覆累積，這正是地球上大陸如何從島弧長起來的。
    /// </summary>
    /// <param name="winner">留在地表的標記。</param>
    /// <param name="subductedThicknessKm">被隱沒掉的那塊地殼有多厚（公里）。</param>
    private void Accrete(int winner, float subductedThicknessKm) =>
        _markerThickness[winner] = MathF.Min(
            _markerThickness[winner] + subductedThicknessKm * AccretedFractionOfSubductedCrust,
            Isostasy.MaxCrustThicknessKm);
}
