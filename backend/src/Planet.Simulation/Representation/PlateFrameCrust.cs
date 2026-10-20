namespace Planet.Simulation.Representation;

/// <summary>
/// 地殼存在各板塊自己的座標系裡（v2 P2 選定的表示法，取代 v1 的標記法；評比見
/// docs/planet-evolution-v2-plan.md 的 P2 結果）。
///
/// 每個板塊有一份「網格大小」的地殼欄位，索引是**板塊座標系裡的固定點**（借用網格的格心當這些點）；
/// 板塊只記錄從起點到現在轉了多少（四元數）。格子要顯示時，把格心反轉回板塊的座標系查那一點。
/// 地殼從來不被「搬」，所以板塊內部沒有取樣雜訊、誤差不累積。
///
/// ## 板塊就是它的地殼（P4，2026-10-17 使用者選定）
///
/// 一格屬於「地殼在那裡的」那個板塊，結果寫進 <see cref="PlateSet.OwnerByCell"/>——邊界分類、動力學
/// （拖曳與驅動力）、火山都看它。種子的 Voronoi（<see cref="PlateSet.PlateIdByCell"/>）只在起始時決定
/// 每個板塊一開始有哪些地殼，之後不再參與。
///
/// 為什麼不用 Voronoi：它的平分線以兩側的平均速度移動，在隱沒帶上會離開上盤真正的邊緣，海溝在上盤上滑動、
/// 島弧被抹成一大片薄薄的增厚帶（P4 第一次在 n=150 跑 50 Myr：陸地 7.8% 幾乎都是這種）。中途試過
/// 「地殼決定歸屬、Voronoi 決定拖曳與空缺」的混合做法，兩個範圍一跑就差一半，力跟阻力算在不同的範圍上、
/// Voronoi 裡的舊地殼在板塊內部冒出來重疊，結果是滿地漩渦狀的條紋與碎陸地。
///
/// ## 每一步
///
/// 1. 旋轉往前推一步、地殼老化（年齡要在座標系裡長，見步驟 0 的說明）。
/// 2. 每一格看「上一步在這裡的板塊」與「鄰居的板塊」哪些在這一點有地殼：
///    - 一個都沒有 → 張裂，長新的海洋地殼給鄰居裡最多的板塊（<see cref="GapPlate"/>）；
///    - 只有一個 → 就是它；
///    - 兩個以上 → **重疊＝聚合**，用岩石圈浮力決定誰隱沒（<see cref="Resolve"/>）。
/// 3. 保險：每個點往前算出自己在哪一格，若整個落在別的板塊的地殼底下（那一格和鄰居都不是自己）
///    又沒被顯示，也用浮力解決——第 2 步只看得到邊界上相鄰的板塊。
/// 4. 把看得到的地殼寫進 <see cref="SimulationState"/>，歸屬寫進 <see cref="PlateSet.OwnerByCell"/>。
///
/// 地質作用改完格子之後呼叫 <see cref="WriteBack"/>，用「拉差值」寫回（見那裡的說明）。
/// </summary>
internal sealed class PlateFrameCrust
{
    private readonly int _cellCount;
    private readonly (double W, double X, double Y, double Z)[] _rotation;
    private readonly float[][] _thickness, _age, _density, _organic, _relief, _sediment;
    private readonly CrustType[][] _crust;
    private readonly bool[][] _oceanicOnly, _valid;
    private readonly int[][] _usedStamp, _forward;

    /// <summary>
    /// 每一點地殼的「來源編號」：起始時是格子索引，新生地殼拿新的號碼。兩點相遇時編號相同，
    /// 代表是同一塊地殼在兩個座標系各留了一份（網格混疊），不能當碰撞處理——
    /// P4 第一次加碰撞規則時，整顆球同一個旋轉（沒有真的邊界）也開始增生、地殼一路變多。
    /// </summary>
    private readonly int[][] _origin;
    private int _nextOrigin;

    /// <summary>從開始到現在經過的時間（百萬年），給焊接後的休眠計時用。</summary>
    private double _elapsedMyr;

    /// <summary>每個板塊編號休眠到什麼時候（百萬年）：被焊接併掉的板塊在這之前不重生（見 <see cref="WeldSuturedPlates"/>）。</summary>
    private readonly double[] _dormantUntilMyr;

    /// <summary>這一步有發生大陸碰撞（兩邊都是大陸的縮短增厚）的板塊對，小的編號在前。焊接只考慮這些（見 <see cref="WeldSuturedPlates"/>）。</summary>
    private readonly HashSet<(int, int)> _collidingPairs = [];

    /// <summary>每一格現在顯示的是哪個板塊、它座標系裡的哪一點。</summary>
    private int[] _owner, _bodyCell, _newOwner, _newBodyCell;


    private readonly float[] _dThickness, _dAge, _dDensity, _dOrganic, _dRelief;
    private readonly bool[] _categoryChanged;
    private readonly bool[] _hidden;

    /// <summary>歸屬平滑用的連通塊編號（暫存）。</summary>
    private readonly int[] _component;

    /// <summary>這一步大陸碰撞縮短下來、還沒攤開的地殼（公里·格），記在發生碰撞的那一格（見 <see cref="DistributeCollisionThickening"/>）。</summary>
    private readonly float[] _collisionBudget;
    private readonly bool[] _collisionFromWall;

    /// <summary>正在判定的是哪一格（<see cref="Resolve"/> 要知道碰撞發生在哪裡）。</summary>
    private int _resolveCell;
    private int _stamp;

    /// <summary>建立空的板塊座標系地殼。</summary>
    /// <param name="cellCount">格數。</param>
    /// <param name="plateCount">板塊數（固定，不分裂也不合併）。</param>
    internal PlateFrameCrust(int cellCount, int plateCount)
    {
        _cellCount = cellCount;
        _rotation = Enumerable.Repeat((1.0, 0.0, 0.0, 0.0), plateCount).ToArray();
        _thickness = Allocate<float>(plateCount); _age = Allocate<float>(plateCount);
        _density = Allocate<float>(plateCount); _organic = Allocate<float>(plateCount); _relief = Allocate<float>(plateCount);
        _sediment = Allocate<float>(plateCount);
        _crust = Allocate<CrustType>(plateCount);
        _oceanicOnly = Allocate<bool>(plateCount); _valid = Allocate<bool>(plateCount);
        _usedStamp = Allocate<int>(plateCount);
        _origin = Allocate<int>(plateCount);
        _forward = new int[plateCount][];
        for (var p = 0; p < plateCount; p++) _forward[p] = Enumerable.Range(0, cellCount).ToArray();
        _owner = new int[cellCount]; _bodyCell = new int[cellCount];
        _newOwner = new int[cellCount]; _newBodyCell = new int[cellCount];
        _dThickness = new float[cellCount]; _dAge = new float[cellCount];
        _dDensity = new float[cellCount]; _dOrganic = new float[cellCount]; _dRelief = new float[cellCount];
        _categoryChanged = new bool[cellCount];
        _hidden = new bool[cellCount];
        _component = new int[cellCount];
        _dormantUntilMyr = new double[plateCount];
        _collisionBudget = new float[cellCount];
        _collisionFromWall = new bool[cellCount];
        _collisionUpper = new int[cellCount];
    }

    /// <summary>這個板塊編號是不是焊接之後在休眠（0 格是正常的，見 <see cref="WeldSuturedPlates"/>）。</summary>
    internal bool IsDormant(int plate) => _elapsedMyr < _dormantUntilMyr[plate];

    /// <summary>每格的地殼屬於哪個板塊（唯讀用途）。</summary>
    internal int[] Owners => _owner;

    /// <summary>每格顯示的地殼點，編成一個整數鍵（板塊 × 格數 ＋ 點），寫進 <paramref name="keys"/>。</summary>
    internal void WritePointKeys(int[] keys)
    {
        for (var c = 0; c < _cellCount; c++) keys[c] = _owner[c] * _cellCount + _bodyCell[c];
    }

    /// <summary>上一步長出新海洋地殼的格數（張裂）。</summary>
    internal int LastNewCrustCells { get; private set; }


    /// <summary>上一步消耗掉的點數（隱沒、對撞被擠掉、重複拷貝不算）。</summary>
    internal int LastConsumedPoints { get; private set; }

    /// <summary>上一步消耗掉的地殼厚度總和（公里·格）。</summary>
    internal double LastConsumedCrustKm { get; private set; }

    /// <summary>上一步消耗掉的大陸地殼點數（只會發生在大陸對撞時被擠掉的那一塊）。</summary>
    internal int LastConsumedContinentalPoints { get; private set; }

    /// <summary>上一步隱沒的次數（浮力低的一方沉下去，至少一邊是海洋地殼）。</summary>
    internal int LastSubductions { get; private set; }

    /// <summary>上一步大陸對撞的次數（兩邊都是大陸，誰都沉不下去）。</summary>
    internal int LastContinentCollisions { get; private set; }

    /// <summary>上一步縮短增厚的次數（浮力差不多、誰都沉不下去，厚度整個疊上去）。</summary>
    internal int LastShortenings { get; private set; }

    /// <summary>上一步因為增厚而變成大陸地殼的點數。</summary>
    internal int LastContinentsFormed { get; private set; }

    /// <summary>上一步大陸碰上海洋、由海洋那一方隱沒的次數（大陸不隱沒）。</summary>
    internal int LastContinentOverrides { get; private set; }

    /// <summary>上一步增生到留下那一方的地殼（公里·格）。</summary>
    internal double LastAccretedCrustKm { get; private set; }

    /// <summary>診斷用（P8，累計、不歸零）：各種增厚來源加起來的地殼（公里×點）：[0] 大陸碰撞、[1] 撞不會被消耗的板塊、[2] 安地斯型造山、[3] 隱沒時增生、[4] 增積楔。</summary>
    internal double[] UpliftSourcesKm { get; } = new double[5];

    /// <summary>這一步在隱沒帶刮進增積楔的沉積物（換成地殼的公里數，所有點加總），見 <see cref="ScrapeSediment"/>。</summary>
    internal double LastScrapedSedimentKm { get; private set; }

    /// <summary>這一步刮進增積楔時地殼已經頂到上限、放不下的沉積物（公里，所有點加總），應該是 0 或極少。</summary>
    internal double LastSedimentOverflowKm { get; private set; }

    /// <summary>上一步丟掉的重複拷貝（同一塊地殼在兩個板塊座標系各留一份，不是碰撞）。</summary>
    internal int LastDuplicatesDropped { get; private set; }

    /// <summary>上一步從最大的板塊裂出新板塊的次數（見 <see cref="RebirthDeadPlates"/>）。</summary>
    internal int LastPlatesReborn { get; private set; }

    /// <summary>上一步焊接（兩塊板塊沿著大陸碰撞帶合併成一塊）的次數，見 <see cref="WeldSuturedPlates"/>。</summary>
    internal int LastPlatesWelded { get; private set; }

    /// <summary>診斷用：上一步保險段（被別的板塊蓋住、沒有顯示的點）解決了幾次重疊。</summary>
    internal int LastHiddenResolves { get; private set; }

    /// <summary>診斷用：其中幾次是縮短增厚（整層疊上去）。</summary>
    internal int LastHiddenShortenings { get; private set; }

    /// <summary>上一步併進周圍板塊的碎片塊數（見 <see cref="AbsorbFragments"/>）。</summary>
    internal int LastFragmentsAbsorbed { get; private set; }

    /// <summary>上一步併進周圍板塊的碎片格數。</summary>
    internal int LastFragmentCellsAbsorbed { get; private set; }

    /// <summary>所有板塊座標系裡有地殼的點數總和。</summary>
    internal long ValidPoints
    {
        get
        {
            long total = 0;
            foreach (var valid in _valid) foreach (var v in valid) if (v) total++;
            return total;
        }
    }

    /// <summary>用目前格子上的地殼初始化：每格的地殼放進它（起始的 Voronoi）板塊的座標系、旋轉歸零。</summary>
    internal void Initialize(SimulationState state, PlateSet plates)
    {
        for (var p = 0; p < _rotation.Length; p++)
        {
            _rotation[p] = (1.0, 0.0, 0.0, 0.0);
            Array.Clear(_valid[p]);
            Array.Clear(_usedStamp[p]);
            for (var b = 0; b < _cellCount; b++) _forward[p][b] = b;
        }
        _stamp = 0;

        for (var c = 0; c < _cellCount; c++)
        {
            var p = plates.PlateIdByCell[c];
            _owner[c] = p;
            _bodyCell[c] = c;
            _thickness[p][c] = state.CrustThickness[c];
            _age[p][c] = state.CrustAge[c];
            _density[p][c] = state.CrustDensity[c];
            _organic[p][c] = state.OrganicThickness[c];
            _relief[p][c] = state.StructuralRelief[c];
            _sediment[p][c] = state.SedimentThickness[c];
            _crust[p][c] = state.Crust[c];
            _oceanicOnly[p][c] = state.OceanicOnly[c];
            _origin[p][c] = c;
            _valid[p][c] = true;
        }
        _nextOrigin = _cellCount;
        _elapsedMyr = 0;
        Array.Clear(_dormantUntilMyr);
        PublishOwnership(plates);
    }

    /// <summary>把每格實際的歸屬寫進 <see cref="PlateSet.OwnerByCell"/>（讀檔之後也要呼叫）。</summary>
    internal void PublishOwnership(PlateSet plates)
    {
        plates.SeparateOwnership();
        Array.Copy(_owner, plates.OwnerByCell, _cellCount);
    }

    /// <summary>
    /// 推進一步並把看得到的地殼寫進 <paramref name="state"/>。呼叫前板塊已經 <see cref="PlateSet.Advance"/> 過。
    /// </summary>
    /// <param name="grid">網格。</param>
    /// <param name="state">模擬狀態（地殼欄位會被覆寫）。</param>
    /// <param name="plates">板塊（<see cref="PlateSet.OwnerByCell"/> 會被更新）。</param>
    /// <param name="deltaMyr">這一步的時間（百萬年）。</param>
    /// <param name="newCrustCells">選填：長出新海洋地殼的格子會被設成 true（給中洋脊火山用）。</param>
    internal void Step(SimulationGrid grid, SimulationState state, PlateSet plates, double deltaMyr, bool[]? newCrustCells = null)
    {
        for (var p = 0; p < _rotation.Length; p++)
        {
            _rotation[p] = SphereMath.Normalize(SphereMath.Multiply(SphereMath.StepRotation(plates.Plates[p], deltaMyr), _rotation[p]));
        }

        _stamp++;
        _elapsedMyr += deltaMyr;
        var u = grid.UnitVector;
        ResetCounters();
        if (_plateSizes.Length != _rotation.Length) _plateSizes = new int[_rotation.Length];
        Array.Clear(_plateSizes);
        for (var c = 0; c < _cellCount; c++) _plateSizes[_owner[c]]++;
        if (newCrustCells is not null) Array.Clear(newCrustCells);

        // 0. 老化：年齡是地殼自己的性質，要在板塊座標系裡長。
        //    引擎的老化在回寫**之後**（ApplyIsostasyAndFillWater），標記法每兩步才重新取樣一次、頂多少一步，
        //    這裡每步都重新讀，靠引擎老化的話年齡每一步都被讀回原值——P3 第一次跑，300 步後全球地殼年齡都是 0。
        var ageStep = (float)deltaMyr;
        // 構造起伏被侵蝕慢慢削掉（P7 有河流侵蝕之後由它接手，見 ReliefDecayMyr）
        var reliefKeep = (float)Math.Exp(-deltaMyr / ReliefDecayMyr);
        for (var p = 0; p < _rotation.Length; p++)
        {
            var age = _age[p];
            var relief = _relief[p];
            var valid = _valid[p];
            Parallel.For(0, _cellCount, b => { if (valid[b]) { age[b] += ageStep; relief[b] *= reliefKeep; } });
        }

        // 1. 上一步在這裡的板塊，在這一點的對應點（彼此無相依，平行）
        Parallel.For(0, _cellCount, c =>
        {
            var d = _owner[c];
            var (bx, by, bz) = SphereMath.Rotate(_rotation[d], u[c * 3], u[c * 3 + 1], u[c * 3 + 2], inverse: true);
            _newBodyCell[c] = SphereMath.ClimbToNearest(grid, _bodyCell[c], bx, by, bz);
        });

        // 2. 決定每一格顯示誰（依序：重疊時的浮力判定會改到別格看得到的點）
        Span<int> candidates = stackalloc int[SimulationGrid.MaxNeighborsPerCell + 2];
        Span<int> bodies = stackalloc int[SimulationGrid.MaxNeighborsPerCell + 2];
        for (var c = 0; c < _cellCount; c++)
        {
            var d = _owner[c];
            var count = 0;
            candidates[count] = d; bodies[count++] = _newBodyCell[c];

            // 鄰居的板塊只在邊界上才可能不同；內部的格子這一段什麼都不做
            var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                var n = grid.Neighbors[baseIndex + s];
                if (n == SimulationGrid.NoNeighbor) continue;
                var q = _owner[n];
                if (candidates[..count].Contains(q)) continue;
                var (qx, qy, qz) = SphereMath.Rotate(_rotation[q], u[c * 3], u[c * 3 + 1], u[c * 3 + 2], inverse: true);
                candidates[count] = q;
                bodies[count++] = grid.FindNearestCell(qx, qy, qz);
            }

            // 空缺（張裂）歸鄰居裡最多的板塊。試過「歸上一步在這裡的板塊」，在聚合邊界上會一直替輸掉的一方
            // 長新地殼、立刻又被吃掉：50 Myr 時每步新生 7,512、隱沒 5,742，歸屬碎成 1,324 塊（P4，2026-10-17）。
            DecideCell(c, candidates[..count], bodies[..count], GapPlate(grid, c, _owner, d), plates, newCrustCells);
        }

        // 3. 保險：整個落在別的板塊地殼底下、又沒被顯示的點
        for (var c = 0; c < _cellCount; c++) _usedStamp[_newOwner[c]][_newBodyCell[c]] = _stamp;
        for (var p = 0; p < _rotation.Length; p++)
        {
            var plate = p;
            Parallel.For(0, _cellCount, b =>
            {
                _hidden[b] = false;
                if (!_valid[plate][b]) return;
                var (x, y, z) = SphereMath.Rotate(_rotation[plate], u[b * 3], u[b * 3 + 1], u[b * 3 + 2]);
                var g = SphereMath.ClimbToNearest(grid, _forward[plate][b], x, y, z);
                _forward[plate][b] = g;
                if (_newOwner[g] == plate || _usedStamp[plate][b] == _stamp) return;

                // 要那一格和鄰居都不是自己才算（網格的正反兩個對應在邊界上會差一格，見 P3 結果）
                var baseIndex = g * SimulationGrid.MaxNeighborsPerCell;
                for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
                {
                    var n = grid.Neighbors[baseIndex + s];
                    if (n != SimulationGrid.NoNeighbor && _newOwner[n] == plate) return;
                }
                _hidden[b] = true;
            });
            for (var b = 0; b < _cellCount; b++)
            {
                if (!_hidden[b] || !_valid[plate][b]) continue;
                var g = _forward[plate][b];
                var other = _newOwner[g];
                var otherBody = _newBodyCell[g];
                if (!_valid[other][otherBody]) continue;
                // 顯示中的那一個是在位者，放在前面（見 Resolve 的縮短增厚規則）
                var shortBefore = LastShortenings;
                _resolveCell = g;
                var winner = Resolve(other, otherBody, plate, b);
                LastHiddenResolves++;
                if (LastShortenings > shortBefore) LastHiddenShortenings++;
                if (winner == plate) { _newOwner[g] = plate; _newBodyCell[g] = b; }
            }
        }

        // 4. 依序判定時，有些格子顯示的點後來在別格輸掉了——重新決定這些格子
        for (var c = 0; c < _cellCount; c++)
        {
            if (_valid[_newOwner[c]][_newBodyCell[c]]) continue;
            var g = GapPlate(grid, c, _newOwner, _newOwner[c]);
            var (gx, gy, gz) = SphereMath.Rotate(_rotation[g], u[c * 3], u[c * 3 + 1], u[c * 3 + 2], inverse: true);
            var gBody = grid.FindNearestCell(gx, gy, gz);
            DecideCell(c, [g], [gBody], g, plates, newCrustCells);
        }

        // 5. 歸屬平滑：每個板塊只留一個本體，脫離本體的小塊整塊併進周圍的板塊（見 AbsorbFragments）
        AbsorbFragments(grid);

        (_owner, _newOwner) = (_newOwner, _owner);
        (_bodyCell, _newBodyCell) = (_newBodyCell, _bodyCell);
        DistributeCollisionThickening(grid);
        WeldSuturedPlates(grid, plates);
        RebirthDeadPlates(grid, plates);
        Array.Copy(_owner, plates.OwnerByCell, _cellCount);
        Read(state);
    }

    /// <summary>
    /// 清掉一個死掉的板塊：它殘留的碎片格交給周圍的板塊（那裡沒有地殼就長新的），座標系裡的舊點全部作廢——
    /// 這個編號接著要拿去裂出新板塊、換一個新的座標系，舊點留著會被解讀到錯的位置上。
    /// </summary>
    private void RetirePlate(SimulationGrid grid, PlateSet plates, int dead)
    {
        var u = grid.UnitVector;
        for (var c = 0; c < _cellCount; c++)
        {
            if (_owner[c] != dead) continue;
            var target = GapPlate(grid, c, _owner, dead);
            if (target == dead) continue; // 四周也都是它（不會發生在公開歸屬為 0 格的板塊上）
            var (x, y, z) = SphereMath.Rotate(_rotation[target], u[c * 3], u[c * 3 + 1], u[c * 3 + 2], inverse: true);
            var body = grid.FindNearestCell(x, y, z);
            if (!_valid[target][body]) CreateOceanicCrust(plates, target, body);
            _owner[c] = target;
            _bodyCell[c] = body;
        }
        Array.Clear(_valid[dead]);
    }

    /// <summary>在板塊 p 的座標系 body 那一點長出新的海洋地殼（張裂）。</summary>
    private void CreateOceanicCrust(PlateSet plates, int p, int body)
    {
        var plate = plates.Plates[p];
        _thickness[p][body] = Isostasy.OceanicCrustThicknessKm;
        _age[p][body] = 0f;
        // 新生地殼繼承板塊的密度異常（否則重板塊會被自己擴張出來的普通地殼稀釋掉，見 v1 的 SeedRidgeMarker）
        _density[p][body] = Isostasy.OceanicCrustDensity + plate.DensityAnomalyKgPerM3;
        _organic[p][body] = 0f;
        _relief[p][body] = 0f;
        _sediment[p][body] = 0f;
        _crust[p][body] = CrustType.Oceanic;
        _oceanicOnly[p][body] = plate.IsOceanicOnly;
        _origin[p][body] = _nextOrigin++;
        _valid[p][body] = true;
        LastNewCrustCells++;
    }

    /// <summary>
    /// 隱沒的地殼有多少比例刮下來增生到上盤。地球的增生楔大約捕獲一到三成。
    ///
    /// **跟 v1 的 <see cref="PlateTectonics.AccretedFractionOfSubductedCrust"/>（3%）不同**：v1 原本也是 10%，
    /// 因為標記法的取樣把增生放大了約 35 倍才調到 3%（見那裡的說明）。板塊座標系沒有那個放大，
    /// 照搬 3% 的話大陸幾乎不長（P4 實測 50 Myr：大陸地殼 1.0%、陸地 5.5%）。
    /// </summary>
    private const float AccretedFractionOfSubductedCrust = 0.10f;

    /// <summary>新板塊一開始跟母板塊分開的角速度（度／百萬年），約每邊 3 公分／年的擴張。</summary>
    private const double RiftAngularVelocityDegPerMyr = 1.0;

    /// <summary>
    /// 重生要從哪個板塊裂出來：海洋地殼最多、而且大陸地殼不超過 <see cref="MaxRebirthParentContinentalShare"/> 的那個
    /// （新板塊從大洋裡裂出來，像中洋脊上的板塊重組）；沒有這種板塊才退而取海洋地殼最多的。
    ///
    /// 原本取「格數最多的」：大陸配置起始時最大的板塊就是焊起來的大陸，重生直接把它從中間切開，
    /// 兩半各自漂走又撞回來（方向 2 劇本第二次跑：4,000 萬年 → 4,500 萬年大陸互撞每步 2.7 → 21 次、
    /// 合併後的大陸裂成網狀、破碎度 2.7 → 9.0）。大陸要裂開是劇本的事（見 ContinentScenario）。
    /// </summary>
    private static int RebirthParent(int[] sizes, int[] oceanic, int[] continental)
    {
        var best = -1;
        for (var p = 0; p < sizes.Length; p++)
        {
            if (sizes[p] == 0 || continental[p] > MaxRebirthParentContinentalShare * sizes[p]) continue;
            if (best < 0 || oceanic[p] > oceanic[best]) best = p;
        }
        if (best >= 0) return best;
        for (var p = 0; p < sizes.Length; p++) if (best < 0 || oceanic[p] > oceanic[best]) best = p;
        return best;
    }

    /// <summary>重生的母板塊最多可以有多少比例的大陸地殼（小島弧、碎片不算大陸）。</summary>
    private const double MaxRebirthParentContinentalShare = 0.02;

    /// <summary>
    /// 地殼歸零的板塊，用它的編號從最大的板塊裂出一塊新的（大陸裂谷，例如東非）。
    ///
    /// 板塊就是它的地殼之後，一個被吃光的板塊不會再生（沒有種子了），板塊只會越來越少，
    /// 長期下去就不會有新的海盆、也就不會有威爾遜旋迴（P4 實測 n=24：一個小板塊 400 步就被吃光）。
    ///
    /// 切法是確定性的：大圓通過最大板塊地殼的重心，法線取「重心 × 它的歐拉極」（沒有在動就用 z 軸），
    /// 法線那一側給新板塊，地殼原封不動地搬進新板塊的座標系（新座標系跟母板塊的一樣，點的編號不用換）。
    /// 新板塊的角速度＝母板塊的 ＋ 一個讓兩半沿切線分開的旋轉，之後交給動力學。
    /// </summary>
    private void RebirthDeadPlates(SimulationGrid grid, PlateSet plates)
    {
        var sizes = new int[_rotation.Length];
        var oceanic = new int[_rotation.Length];
        var continental = new int[_rotation.Length];
        for (var c = 0; c < _cellCount; c++)
        {
            var o = _owner[c];
            sizes[o]++;
            if (_crust[o][_bodyCell[c]] == CrustType.Continental) continental[o]++;
            else oceanic[o]++;
        }

        for (var dead = 0; dead < sizes.Length; dead++)
        {
            if (sizes[dead] > 0 || _elapsedMyr < _dormantUntilMyr[dead]) continue;
            RetirePlate(grid, plates, dead);
            var parent = RebirthParent(sizes, oceanic, continental);
            if (sizes[parent] < 2) return;

            var u = grid.UnitVector;
            double cx = 0, cy = 0, cz = 0;
            for (var c = 0; c < _cellCount; c++)
            {
                if (_owner[c] != parent) continue;
                cx += u[c * 3]; cy += u[c * 3 + 1]; cz += u[c * 3 + 2];
            }
            var cl = Math.Sqrt(cx * cx + cy * cy + cz * cz);
            if (cl < 1e-9) continue;
            cx /= cl; cy /= cl; cz /= cl;

            var plate = plates.Plates[parent];
            double ax = plate.PoleX, ay = plate.PoleY, az = plate.PoleZ;
            if (plate.AngularVelocityDegPerMyr < 1e-6) (ax, ay, az) = (0, 0, 1);
            double nx = cy * az - cz * ay, ny = cz * ax - cx * az, nz = cx * ay - cy * ax;
            var nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nl < 1e-9) { (nx, ny, nz) = (-cy, cx, 0); nl = Math.Sqrt(nx * nx + ny * ny); }
            if (nl < 1e-9) continue;
            nx /= nl; ny /= nl; nz /= nl;

            _rotation[dead] = _rotation[parent];
            var moved = 0;
            for (var c = 0; c < _cellCount; c++)
            {
                if (_owner[c] != parent) continue;
                if (u[c * 3] * nx + u[c * 3 + 1] * ny + u[c * 3 + 2] * nz <= 0) continue;
                var b = _bodyCell[c];
                if (!_valid[parent][b]) continue;
                CopyPoint(parent, b, dead, b);
                _valid[dead][b] = true;
                _valid[parent][b] = false;
                _owner[c] = dead;
                moved++;
            }
            if (moved == 0) continue;

            // 母板塊的角速度向量 ＋ 繞「重心 × 法線」的分離旋轉（在切線上的速度方向就是法線，往新板塊那一側）
            var w = plate.AngularVelocityDegPerMyr;
            double kx = cy * nz - cz * ny, ky = cz * nx - cx * nz, kz = cx * ny - cy * nx;
            double wx = plate.PoleX * w + kx * RiftAngularVelocityDegPerMyr;
            double wy = plate.PoleY * w + ky * RiftAngularVelocityDegPerMyr;
            double wz = plate.PoleZ * w + kz * RiftAngularVelocityDegPerMyr;
            var wl = Math.Sqrt(wx * wx + wy * wy + wz * wz);
            plates.Plates[dead] = plates.Plates[dead] with
            {
                PoleX = wx / wl, PoleY = wy / wl, PoleZ = wz / wl,
                AngularVelocityDegPerMyr = wl,
                DensityAnomalyKgPerM3 = 0f,
                IsOceanicOnly = false,
            };
            sizes[dead] = moved;
            sizes[parent] -= moved;
            oceanic[parent] = Math.Max(0, oceanic[parent] - moved);
            LastPlatesReborn++;
        }
    }

    /// <summary>
    /// 兩塊板塊之間「兩側都是大陸」的邊界長到這裡就焊接成一塊（公里）。
    /// 印度與歐亞的碰撞帶約 2,500 公里、阿帕拉契（勞倫西亞與岡瓦納）約 3,000 公里；取 1,000 公里，
    /// 小島弧或大陸角擦過不算，真的有一段大陸迎面撞上才算。
    /// </summary>
    private const double WeldSutureLengthKm = 1_000;

    /// <summary>
    /// 被焊接併掉的板塊編號休眠多久才重生（百萬年）。超大陸聚合之後大約維持一億年才裂開（盤古約 3.35 億到 1.75 億年前）；
    /// 不休眠的話，下一步重生就從最大的板塊切一塊出來，剛焊起來的大陸馬上又被拆開。
    /// </summary>
    private const double WeldedPlateDormancyMyr = 100;

    /// <summary>焊接之後至少要留幾個還活著的板塊——全部焊成一塊就沒有邊界、沒有構造活動了。</summary>
    private const int MinLivePlates = 6;

    /// <summary>
    /// **板塊焊接**（v2 P4，2026-10-18）：兩塊板塊沿著夠長的大陸碰撞帶撞上之後合併成一塊（印度併進歐亞、
    /// 勞倫西亞與岡瓦納焊成盤古）。小的那一塊把**目前顯示的地殼**整片抄進大的那一塊的座標系，之後跟著它走；
    /// 它的編號休眠 <see cref="WeldedPlateDormancyMyr"/>，期滿再由 <see cref="RebirthDeadPlates"/> 從最大的板塊裂出來。
    ///
    /// 為什麼要有：板塊數固定、碰撞之後兩塊板塊永遠各走各的，大陸只長在板塊邊緣、而碰撞帶永遠在互相吃
    /// （碰撞阻力擋不住），陸地的形狀就是板塊邊界的形狀——3 億年長跑：陸地 22.9%，但最大兩塊只佔陸地 9%＋8%、
    /// 一圈一圈的環狀島弧圍著海盆。超大陸旋迴需要「撞上 → 焊成一塊 → 之後裂開」這一步。
    ///
    /// 被別的板塊蓋在下面、沒有顯示的點不搬（它們本來就在隱沒或等著被保險段解決）。
    /// </summary>
    private void WeldSuturedPlates(SimulationGrid grid, PlateSet plates)
    {
        var plateCount = _rotation.Length;
        var sizes = new int[plateCount];
        for (var c = 0; c < _cellCount; c++) sizes[_owner[c]]++;
        var live = sizes.Count(s => s > 0);
        if (live <= MinLivePlates) return;

        // 每一對板塊之間兩側都是大陸的邊界格數（兩側各算一次，所以門檻要乘 2）
        var suture = new Dictionary<(int, int), int>();
        Span<int> seen = stackalloc int[SimulationGrid.MaxNeighborsPerCell];
        for (var c = 0; c < _cellCount; c++)
        {
            var p = _owner[c];
            if (_crust[p][_bodyCell[c]] != CrustType.Continental) continue;
            var count = 0;
            var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                var n = grid.Neighbors[baseIndex + s];
                if (n == SimulationGrid.NoNeighbor) continue;
                var q = _owner[n];
                if (q == p || seen[..count].Contains(q)) continue;
                if (_crust[q][_bodyCell[n]] != CrustType.Continental) continue;
                seen[count++] = q;
                var key = p < q ? (p, q) : (q, p);
                suture[key] = suture.TryGetValue(key, out var k) ? k + 1 : 1;
            }
        }

        var threshold = 2.0 * WeldSutureLengthKm * 1000.0 / grid.AverageCellSpacingMeters;
        var u = grid.UnitVector;
        var welded = new bool[plateCount];
        // 最長的碰撞帶先焊；同樣長取編號小的，結果才是決定性的
        foreach (var ((a, b), length) in suture.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key))
        {
            if (length < threshold) break;
            // 要這一步真的在碰撞才焊：只是並排、沒有在聚合的兩塊大陸不算（例如兩塊板塊剛好用同一個旋轉在走）
            if (!_collidingPairs.Contains((a, b))) continue;
            if (WeldBlocked?.Invoke(a, b) == true) continue;
            if (welded[a] || welded[b] || live <= MinLivePlates) continue;
            var (keep, gone) = sizes[a] >= sizes[b] ? (a, b) : (b, a);
            MergePlate(grid, plates, keep, gone);

            sizes[keep] += sizes[gone];
            sizes[gone] = 0;
            welded[keep] = welded[gone] = true;
            live--;
            LastPlatesWelded++;
        }
    }

/// <summary>
    /// 把板塊 gone 整塊併進 keep：gone 目前顯示的地殼抄進 keep 的座標系（先推、再拉補洞），gone 的編號休眠
    /// <see cref="WeldedPlateDormancyMyr"/>。焊接（<see cref="WeldSuturedPlates"/>）與劇本的強制焊接（<see cref="ForceWeld"/>）共用。
    /// </summary>
    private void MergePlate(SimulationGrid grid, PlateSet plates, int keep, int gone)
    {
        var u = grid.UnitVector;
        var wasGone = new bool[_cellCount];
        var oldBody = new int[_cellCount];
        for (var c = 0; c < _cellCount; c++)
        {
            wasGone[c] = _owner[c] == gone;
            oldBody[c] = _bodyCell[c];
        }

        for (var c = 0; c < _cellCount; c++)
        {
            if (!wasGone[c]) continue;
            var from = _bodyCell[c];
            var (x, y, z) = SphereMath.Rotate(_rotation[keep], u[c * 3], u[c * 3 + 1], u[c * 3 + 2], inverse: true);
            var to = grid.FindNearestCell(x, y, z);
            if (_valid[gone][from])
            {
                CopyPoint(gone, from, keep, to);
                _valid[keep][to] = true;
            }
            else if (!_valid[keep][to]) continue; // 不會發生：顯示中的點都有地殼
            _owner[c] = keep;
            _bodyCell[c] = to;
        }
        // 再「拉」一次：只推的話，兩個座標系的網格對不齊，保留板塊的座標系裡有些點被寫兩次、有些點沒寫到，
        // 之後從它讀回來時那些點是空缺、長出新的海洋地殼——焊起來的大陸中間一顆顆胡椒
        // （方向 2 第一次跑：焊接那一步胡椒 253 → 1,105、最大一塊的破碎度 3.6 → 6.1）。
        // 所以保留板塊座標系裡還沒有地殼的點，看它現在落在哪一格，那一格原本是被併掉的板塊的就抄過來。
        var keepRotation = _rotation[keep];
        Parallel.For(0, _cellCount, body =>
        {
            if (_valid[keep][body]) return;
            var (x, y, z) = SphereMath.Rotate(keepRotation, u[body * 3], u[body * 3 + 1], u[body * 3 + 2]);
            var g = grid.FindNearestCell(x, y, z);
            if (!wasGone[g] || !_valid[gone][oldBody[g]]) return;
            CopyPoint(gone, oldBody[g], keep, body);
            _valid[keep][body] = true;
        });
        Array.Clear(_valid[gone]);
        plates.Plates[gone] = plates.Plates[gone] with { AngularVelocityDegPerMyr = 0 };
        _dormantUntilMyr[gone] = _elapsedMyr + WeldedPlateDormancyMyr;
    }

    /// <summary>
    /// 板塊座標系裡 region 的點沉成新生的海洋地殼（劇本用：小島之間的段落在裂開時變成海床）。在兩步之間呼叫。
    /// </summary>
    /// <param name="plate">板塊編號。</param>
    /// <param name="region">哪些點（座標系的點編號）。</param>
    internal void SinkToOcean(int plate, Func<int, bool> region)
    {
        for (var b = 0; b < _cellCount; b++)
        {
            if (!_valid[plate][b] || !region(b)) continue;
            _crust[plate][b] = CrustType.Oceanic;
            _thickness[plate][b] = Isostasy.OceanicCrustThicknessKm;
            _density[plate][b] = Isostasy.OceanicCrustDensity;
            _age[plate][b] = 0f;
            _organic[plate][b] = 0f;
            _relief[plate][b] = 0f;
        }
    }

    /// <summary>板塊編號休眠到什麼時候（百萬年）：休眠中的板塊 0 格是正常的，不會被重生拿去用。</summary>
    /// <param name="plate">板塊編號。</param>
    /// <param name="untilMyr">休眠到幾百萬年。</param>
    internal void SetDormantUntil(int plate, double untilMyr) => _dormantUntilMyr[plate] = untilMyr;

    /// <summary>
    /// 劇本的裂開（見 <see cref="ContinentScenario.DueSplits"/>）：母板塊座標系裡 region 的點整塊搬到新板塊，
    /// 新板塊的旋轉照抄母板塊（點的編號不用換，跟重生同一個做法），格子的歸屬跟著改。速度之後由劇本給。
    /// 在兩步之間呼叫。
    /// </summary>
    internal void SplitOff(PlateSet plates, int parent, int child, Func<int, bool> region)
    {
        _rotation[child] = _rotation[parent];
        Array.Clear(_valid[child]);
        for (var b = 0; b < _cellCount; b++)
        {
            if (!_valid[parent][b] || !region(b)) continue;
            CopyPoint(parent, b, child, b);
            _valid[child][b] = true;
            _valid[parent][b] = false;
        }
        for (var c = 0; c < _cellCount; c++)
            if (_owner[c] == parent && _valid[child][_bodyCell[c]]) _owner[c] = child;
        _dormantUntilMyr[child] = 0;
        plates.Plates[child] = plates.Plates[child] with
        {
            PoleX = plates.Plates[parent].PoleX, PoleY = plates.Plates[parent].PoleY, PoleZ = plates.Plates[parent].PoleZ,
            AngularVelocityDegPerMyr = plates.Plates[parent].AngularVelocityDegPerMyr,
            DensityAnomalyKgPerM3 = 0f, IsOceanicOnly = false,
        };
        Array.Copy(_owner, plates.OwnerByCell, _cellCount);
    }

    /// <summary>
    /// 劇本的強制焊接（見 <see cref="ContinentScenario"/>）：不看碰撞帶多長，直接把 gone 併進 keep。
    /// gone 已經沒有任何一格（例如續跑時已經焊過）就什麼都不做。在兩步之間呼叫。
    /// </summary>
    internal void ForceWeld(SimulationGrid grid, PlateSet plates, int keep, int gone)
    {
        if (Array.IndexOf(_owner, gone) < 0) return;
        MergePlate(grid, plates, keep, gone);
        Array.Copy(_owner, plates.OwnerByCell, _cellCount);
    }

    /// <summary>上一步這兩塊板塊之間有沒有發生大陸碰撞（劇本用來偵測「撞上了」）。在下一步開始之前呼叫才有意義。</summary>
    internal bool WereColliding(int a, int b) => _collidingPairs.Contains(a < b ? (a, b) : (b, a));

    /// <summary>選填：哪些板塊對不准自動焊接（劇本用：碰撞還在推的兩塊、大島與母大陸）。</summary>
    internal Func<int, int, bool>? WeldBlocked { get; set; }

    /// <summary>
    /// 選填：哪個板塊重疊時永遠留下、不會被消耗（劇本用：固定不動、面積不減的太平洋）。
    /// 擠進它的大陸地殼被吃掉，厚度記進碰撞增厚、攤到撞上來的那塊大陸的前緣（見 <see cref="Resolve"/>）。
    /// </summary>
    /// <remarks>參數是（這個板塊、碰上的板塊）：同一塊板塊可以只對某些板塊不會被消耗（劇本的太平洋讓 A 南隱沒進去）。</remarks>
    internal Func<int, int, bool>? Unconsumable { get; set; }

    /// <summary>
    /// 安地斯型造山（v2 P6，2026-10-19 使用者指定；只在大陸配置的劇本模式開）：這裡回傳 true 的板塊隱沒到大陸底下時，
    /// 大陸前緣被擠壓縮短——每隱沒一點，就把那一格大陸地殼厚度的 <see cref="AndeanShorteningFraction"/> 記進碰撞增厚
    /// （照山脈的方式攤開、疊上撞牆那一級的褶皺），但大陸不少掉面積。地球上安地斯的高度主要來自大陸邊緣的縮短。
    ///
    /// 太平洋改成隱沒到 A 南底下之後（A 南不再被那道牆吃掉），A 南西岸的山整排不見了。第一版用「隱沒的海洋地殼的 50%」，
    /// 洋殼只有 7 公里厚、A 南那一段又只有 0.53 公分／年，每格的量只有撞牆的十分之一，A 南還是沒有山；
    /// 反而套用在所有海洋隱沒到大陸底下的地方，極區板塊（1~2 公分／年）在 A 北、C 的北岸堆出了山。
    /// 所以只限指定的板塊（劇本的太平洋），量比照撞牆時被吃掉的大陸厚度。
    ///
    /// 這份增厚沒有從別處扣，質量不守恆——是劇本為了外觀做的調整。沒有劇本的隨機起始不開。
    /// </summary>
    internal Func<int, bool>? AndeanSubductingPlate { get; set; }

    /// <summary>安地斯型造山：每隱沒一點，大陸前緣地殼厚度（<see cref="AndeanReferenceCrustKm"/>）的多少比例變成造山增厚（見 <see cref="AndeanSubductingPlate"/>）。</summary>
    private const float AndeanShorteningFraction = 0.5f;

    /// <summary>
    /// 安地斯型造山用的大陸前緣厚度（公里）。接觸的常常是大陸棚外側自己的海洋地殼，不能用接觸那一點的厚度，
    /// 取一般大陸的厚度——比照撞牆時被吃掉的大陸。
    /// </summary>
    private const float AndeanReferenceCrustKm = 35f;

    /// <summary>
    /// 碰撞處不在大陸地殼上時（安地斯型造山接觸的是大陸前緣自己的海洋地殼），造山量往同一個板塊最近的大陸格找，最遠幾公里。
    /// 原本攤開只沿著大陸格走、從海洋格出發走不到大陸上：A 南西岸跟太平洋之間大多隔著一條自己的海洋地殼，只有中段一小塊長出山。
    /// </summary>
    private const double CollisionToContinentReachKm = 150;

    /// <summary>不在大陸地殼上的碰撞處，把造山量搬到同一個板塊、<see cref="CollisionToContinentReachKm"/> 以內最近的大陸格（找不到就留著，照舊攤不出去）。</summary>
    private void MoveBudgetToContinent(SimulationGrid grid)
    {
        var maxHops = Math.Max(1, (int)(CollisionToContinentReachKm * 1000.0 / grid.AverageCellSpacingMeters));
        var hops = new Dictionary<int, int>();
        var queue = new Queue<int>();
        for (var start = 0; start < _cellCount; start++)
        {
            if (_collisionBudget[start] <= 0f || IsContinentalCell(start)) continue;
            var plate = _owner[start];
            hops.Clear();
            queue.Clear();
            hops[start] = 0;
            queue.Enqueue(start);
            var target = -1;
            while (queue.Count > 0 && target < 0)
            {
                var c = queue.Dequeue();
                if (hops[c] >= maxHops) continue;
                var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
                for (var k = 0; k < SimulationGrid.MaxNeighborsPerCell && target < 0; k++)
                {
                    var nb = grid.Neighbors[baseIndex + k];
                    if (nb == SimulationGrid.NoNeighbor || _owner[nb] != plate || hops.ContainsKey(nb)) continue;
                    hops[nb] = hops[c] + 1;
                    if (IsContinentalCell(nb)) target = nb;
                    else queue.Enqueue(nb);
                }
            }
            if (target < 0) continue;
            _collisionBudget[target] += _collisionBudget[start];
            _collisionFromWall[target] |= _collisionFromWall[start];
            _collisionUpper[target] = _collisionUpper[start];
            _collisionBudget[start] = 0f;
        }
    }

    /// <summary>
    /// 大陸碰撞的增厚分成兩份（2026-10-18 使用者指定：碰撞線上是高山山脈，山脈後方是高原，像喜馬拉雅與青藏高原）：
    ///
    /// 2026-10-18 使用者改成只要高山、不要高原，所以現在取 1（高原那一份是 0，程式留著以後要高原時調回來）。
    ///
    /// - **山脈**：<see cref="CollisionMountainFraction"/> 集中在碰撞處 <see cref="CollisionMountainWidthKm"/> 以內（兩側都算），
    ///   越近分得越多——窄而高。
    /// - **高原**：其餘平均攤到**上盤**碰撞處後方 <see cref="CollisionPlateauWidthKm"/> 以內的大陸——寬而平。
    ///   上盤取兩塊裡比較大的那塊（印度插到亞洲底下，高原長在亞洲那一側）。
    ///
    /// 第一版整份按線性權重攤在 400 公里內、兩側對稱：長出來的是一道以接縫為中心的隆起，沒有山脈與高原之分。
    /// 更早的版本整份疊在前緣那一格，撞出來的是一道一格寬、約 70 公里厚的牆。
    /// </summary>
    private const double CollisionMountainFraction = 1.0;

    /// <summary>
    /// 山脈的寬度（公里，從碰撞處往兩側）。喜馬拉雅約 200~300 公里寬。
    /// 原本取 120：只堆得出一道窄窄的山脊。2026-10-19 使用者指出地球的造山帶是一整片被擠皺的地殼、不是只有一排山，改成 300，褶皺見 <see cref="FoldWavelengthKm"/>。
    /// </summary>
    private const double CollisionMountainWidthKm = 300;

    /// <summary>
    /// 褶皺的波長（公里）：碰撞帶裡平行於碰撞處的山脊每隔這麼遠一排，中間是谷地（疊在構造起伏上，見 SimulationState.StructuralRelief）。
    /// 喜馬拉雅從南到北是西瓦利克、小喜馬拉雅、大喜馬拉雅幾排，間距幾十到一百公里；n=150 一格約 15 公里，75 公里是 5 格，解析得出來。
    /// </summary>
    private const double FoldWavelengthKm = 75;


    /// <summary>高原的寬度（公里，從碰撞處往上盤內側）。青藏高原約 1,000 公里。</summary>
    private const double CollisionPlateauWidthKm = 500;

    /// <summary>上一步大陸碰撞攤開的增厚（公里·格），診斷用。</summary>
    internal double LastCollisionThickeningKm { get; private set; }

    /// <summary>這一步每個碰撞處的上盤（兩塊裡較大的那塊），高原長在它上面。</summary>
    private readonly int[] _collisionUpper;

    /// <summary>每個板塊這一步開始時有幾格（判斷上盤用）。</summary>
    private int[] _plateSizes = [];

    /// <summary>
    /// 把這一步大陸碰撞縮短下來的地殼攤到碰撞帶上（山脈＋高原，見 <see cref="CollisionMountainFraction"/>）。
    /// 每一格歸最近的那個碰撞處（多源廣度優先，依格子索引排序起步，結果與執行緒無關）。
    /// 質量守恆：高原那份找不到上盤的格子就併進山脈；只有頂到厚度上限的部分會丟掉。
    /// </summary>
    private void DistributeCollisionThickening(SimulationGrid grid)
    {
        LastCollisionThickeningKm = 0;
        var any = false;
        for (var c = 0; c < _cellCount && !any; c++) any = _collisionBudget[c] > 0f;
        if (!any) return;
        MoveBudgetToContinent(grid);

        var spacing = grid.AverageCellSpacingMeters;
        var mountainHops = Math.Max(1, (int)(CollisionMountainWidthKm * 1000.0 / spacing));
        var plateauHops = Math.Max(1, (int)(CollisionPlateauWidthKm * 1000.0 / spacing));

        // 高原：沿大陸地殼走，只有上盤的格子拿得到（權重相同）
        var (pSource, _) = NearestCollision(grid, plateauHops);
        var plateauCount = new int[_cellCount];
        for (var c = 0; c < _cellCount; c++)
            if (pSource[c] >= 0 && _owner[c] == _collisionUpper[pSource[c]] && IsContinentalCell(c)) plateauCount[pSource[c]]++;

        var mountainBudget = new float[_cellCount];
        for (var c = 0; c < _cellCount; c++)
        {
            if (_collisionBudget[c] <= 0f) continue;
            mountainBudget[c] = plateauCount[c] > 0 ? _collisionBudget[c] * (float)CollisionMountainFraction : _collisionBudget[c];
        }
        for (var c = 0; c < _cellCount; c++)
        {
            var src = pSource[c];
            if (src < 0 || plateauCount[src] == 0 || _owner[c] != _collisionUpper[src] || !IsContinentalCell(c)) continue;
            AddThickness(c, (_collisionBudget[src] - mountainBudget[src]) / plateauCount[src]);
        }

        // 山脈：兩側都走，越近分得越多（地殼厚度，平滑的包絡）；褶皺另外疊在構造起伏上（見 FoldWavelengthKm）
        var (mSource, mHops) = NearestCollision(grid, mountainHops);
        var foldHops = FoldWavelengthKm * 1000.0 / spacing;
        double Weight(int h) => 1.0 - (double)h / (mountainHops + 1);
        var weightSum = new double[_cellCount];
        for (var c = 0; c < _cellCount; c++)
            if (mSource[c] >= 0 && IsContinentalCell(c)) weightSum[mSource[c]] += Weight(mHops[c]);
        for (var c = 0; c < _cellCount; c++)
        {
            var src = mSource[c];
            if (src < 0 || weightSum[src] <= 0 || !IsContinentalCell(c)) continue;
            var added = (float)(mountainBudget[src] * Weight(mHops[c]) / weightSum[src]);
            AddThickness(c, added);
            var reliefPerKm = _collisionFromWall[src] ? WallReliefPerKmOfThickening : ReliefPerKmOfThickening;
            AddRelief(c, (float)(added * reliefPerKm * Math.Cos(2 * Math.PI * mHops[c] / foldHops)));
        }
    }

    /// <summary>
    /// 這一格現在顯示的是不是大陸地殼。碰撞增厚只加在大陸格上：一般的大陸碰撞，碰撞處本身就是大陸；
    /// 撞上不會被消耗的板塊（見 <see cref="Unconsumable"/>）時，碰撞處已經歸那塊海洋板塊，增厚要全部落在撞上來的大陸那一側。
    /// </summary>
    private bool IsContinentalCell(int cell) => _crust[_owner[cell]][_bodyCell[cell]] == CrustType.Continental;

    /// <summary>在這一格顯示的那一點疊上構造起伏，夾在 ±<see cref="MaxReliefMeters"/>。</summary>
    private void AddRelief(int cell, float add)
    {
        var p = _owner[cell];
        var b = _bodyCell[cell];
        _relief[p][b] = Math.Clamp(_relief[p][b] + add, -MaxReliefMeters, MaxReliefMeters);
    }

    /// <summary>
    /// 碰撞增厚每 1 公里，褶皺的起伏長多少公尺（山脊加、谷地減，平均起來是 0，不改變山脈的總高度）。
    /// 碰撞帶前緣累積增厚幾十公里時，山脊與谷地相差約兩三公里，像喜馬拉雅幾排山之間的谷地。
    /// </summary>
    private const float ReliefPerKmOfThickening = 100f;

    /// <summary>
    /// 大陸撞上不會被消耗的板塊（劇本的太平洋）時，每增厚 1 公里長多少構造起伏。
    /// A 往西撞太平洋的速度只有 0.5 公分／年，增厚量小，照 <see cref="ReliefPerKmOfThickening"/> 的話西岸看不出褶皺；
    /// 2026-10-19 使用者要求加大 A 西岸的褶皺，取 4 倍。
    /// </summary>
    private const float WallReliefPerKmOfThickening = 400f;

    /// <summary>構造起伏的上限（公尺，正負）。</summary>
    private const float MaxReliefMeters = 2000f;

    /// <summary>構造起伏被侵蝕削掉的特徵時間（百萬年）。造山帶的地形在停止擠壓之後幾千萬年才被夷平。</summary>
    private const double ReliefDecayMyr = 20;

    private void AddThickness(int cell, float add)
    {
        var p = _owner[cell];
        var b = _bodyCell[cell];
        var before = _thickness[p][b];
        _thickness[p][b] = MathF.Min(before + add, Isostasy.MaxCrustThicknessKm);
        LastCollisionThickeningKm += _thickness[p][b] - before;
    }

    /// <summary>從每個碰撞處沿著大陸地殼往外走 maxHops 格，每一格歸最近的碰撞處。</summary>
    private (int[] Source, int[] Hops) NearestCollision(SimulationGrid grid, int maxHops)
    {
        var source = new int[_cellCount];
        var hops = new int[_cellCount];
        Array.Fill(source, -1);
        var queue = new Queue<int>();
        for (var c = 0; c < _cellCount; c++)
        {
            if (_collisionBudget[c] <= 0f) continue;
            source[c] = c;
            queue.Enqueue(c);
        }
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            if (hops[c] >= maxHops) continue;
            var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                var n = grid.Neighbors[baseIndex + s];
                if (n == SimulationGrid.NoNeighbor || source[n] >= 0) continue;
                if (_crust[_owner[n]][_bodyCell[n]] != CrustType.Continental) continue;
                source[n] = source[c];
                hops[n] = hops[c] + 1;
                queue.Enqueue(n);
            }
        }
        return (source, hops);
    }

    /// <summary>
    /// 脫離板塊本體的小塊最大可以多大（格），超過的保留——那可能是真的大陸碎塊。
    /// n=150 一格約 192 平方公里，1,000 格約 19 萬平方公里，比馬達加斯加（58.7 萬）小。
    /// </summary>
    private const int MaxFragmentCells = 1_000;

    /// <summary>
    /// 歸屬平滑：每個板塊只留一個本體（它最大的連通塊），其他脫離本體、不超過 <see cref="MaxFragmentCells"/>
    /// 的小塊整塊併進跟它共用邊界最長的板塊。周圍的板塊在那裡有地殼就顯示它的；沒有就把碎片的地殼抄過去
    /// （地體增生）。原本那一份留在原板塊的座標系，下一步的保險段會以重複拷貝的規則丟掉、或當作真的重疊解決。
    ///
    /// 為什麼要有：真實的板塊是剛體、邊界是連續的線，但逐格的邊界規則在離散網格上沒有任何「保持連續」的機制，
    /// 碎片會一直累積。P4 設計 C 試過約十種逐格規則，歸屬一直碎成約 200 塊（板塊只有 15 個）、邊界格佔全球 10% 以上，
    /// 到處都在聚合、縮短、長出碎陸地。
    ///
    /// 逐格處理碎片（一次只看一格）試過三種，都留下條紋：把碎片抄過去（厚地殼條紋）、給周圍長新地殼（年輕地殼條紋）、
    /// 只改公開的歸屬（地殼歸屬照樣碎）。這裡改成整塊處理。
    /// </summary>
    private void AbsorbFragments(SimulationGrid grid)
    {
        Array.Fill(_component, -1);
        var sizes = new List<int>();
        var plateOf = new List<int>();
        var stack = new Stack<int>();
        for (var start = 0; start < _cellCount; start++)
        {
            if (_component[start] >= 0) continue;
            var id = sizes.Count;
            var plate = _newOwner[start];
            var size = 0;
            _component[start] = id;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                size++;
                var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
                for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
                {
                    var n = grid.Neighbors[baseIndex + s];
                    if (n == SimulationGrid.NoNeighbor || _component[n] >= 0 || _newOwner[n] != plate) continue;
                    _component[n] = id;
                    stack.Push(n);
                }
            }
            sizes.Add(size);
            plateOf.Add(plate);
        }

        // 每個板塊的本體：最大的那塊（同樣大取編號小的，結果才是決定性的）
        var body = Enumerable.Repeat(-1, _rotation.Length).ToArray();
        for (var id = 0; id < sizes.Count; id++)
        {
            var p = plateOf[id];
            if (body[p] < 0 || sizes[id] > sizes[body[p]]) body[p] = id;
        }

        // 每一塊碎片的邊界：跟哪個板塊共用最長
        var target = new int[sizes.Count];
        var border = new Dictionary<int, int>();
        var cellsOf = new List<int>[sizes.Count];
        for (var c = 0; c < _cellCount; c++)
        {
            var id = _component[c];
            if (body[plateOf[id]] == id || sizes[id] > MaxFragmentCells) continue;
            (cellsOf[id] ??= []).Add(c);
        }
        for (var id = 0; id < sizes.Count; id++)
        {
            target[id] = -1;
            if (cellsOf[id] is not { } cells) continue;
            border.Clear();
            foreach (var c in cells)
            {
                var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
                for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
                {
                    var n = grid.Neighbors[baseIndex + s];
                    if (n == SimulationGrid.NoNeighbor || _component[n] == id) continue;
                    var q = _newOwner[n];
                    border[q] = border.TryGetValue(q, out var k) ? k + 1 : 1;
                }
            }
            var best = -1;
            foreach (var (q, k) in border.OrderBy(kv => kv.Key)) if (best < 0 || k > border[best]) best = q;
            target[id] = best;
        }

        var u = grid.UnitVector;
        for (var id = 0; id < sizes.Count; id++)
        {
            if (cellsOf[id] is not { } cells || target[id] < 0) continue;
            var t = target[id];
            foreach (var c in cells)
            {
                var owner = _newOwner[c];
                var from = _newBodyCell[c];
                var (x, y, z) = SphereMath.Rotate(_rotation[t], u[c * 3], u[c * 3 + 1], u[c * 3 + 2], inverse: true);
                var tBody = grid.FindNearestCell(x, y, z);
                if (!_valid[t][tBody] && _valid[owner][from])
                {
                    CopyPoint(owner, from, t, tBody);
                    _valid[t][tBody] = true;
                }
                if (!_valid[t][tBody]) continue; // 兩邊都沒有地殼（不會發生：碎片的格子都有顯示中的地殼）
                _newOwner[c] = t;
                _newBodyCell[c] = tBody;
            }
            LastFragmentsAbsorbed++;
            LastFragmentCellsAbsorbed += cells.Count;
        }
    }

    private void CopyPoint(int from, int fromBody, int to, int toBody)
    {
        _thickness[to][toBody] = _thickness[from][fromBody];
        _age[to][toBody] = _age[from][fromBody];
        _density[to][toBody] = _density[from][fromBody];
        _organic[to][toBody] = _organic[from][fromBody];
        _relief[to][toBody] = _relief[from][fromBody];
        _sediment[to][toBody] = _sediment[from][fromBody];
        _crust[to][toBody] = _crust[from][fromBody];
        _oceanicOnly[to][toBody] = _oceanicOnly[from][fromBody];
        _origin[to][toBody] = _origin[from][fromBody];
    }

    /// <summary>
    /// 決定一格顯示誰：候選板塊裡在這一點有地殼的，一個都沒有就長新地殼給 gapPlate；
    /// 兩個以上就是重疊，兩兩用浮力解決。結果寫進 <c>_newOwner</c>／<c>_newBodyCell</c>。
    /// </summary>
    private void DecideCell(int c, ReadOnlySpan<int> candidates, ReadOnlySpan<int> bodies, int gapPlate,
        PlateSet plates, bool[]? newCrustCells)
    {
        var winner = -1;
        var winnerBody = -1;
        _resolveCell = c;
        for (var i = 0; i < candidates.Length; i++)
        {
            var q = candidates[i];
            var b = bodies[i];
            if (!_valid[q][b]) continue;
            if (winner < 0) { winner = q; winnerBody = b; continue; }
            if (q == winner) continue;

            var survivor = Resolve(winner, winnerBody, q, b);
            if (survivor == q) { winner = q; winnerBody = b; }
        }

        if (winner < 0)
        {
            // 張裂：新的海洋地殼長給 gapPlate（鄰居裡最多的那個板塊，見 GapPlate）
            winner = gapPlate;
            var index = candidates.IndexOf(gapPlate);
            if (index < 0) throw new InvalidOperationException("空缺要長給的板塊不在候選裡——呼叫端要把它的對應點一起傳進來。");
            winnerBody = bodies[index];
            CreateOceanicCrust(plates, winner, winnerBody);
            if (newCrustCells is not null) newCrustCells[c] = true;
        }

        _newOwner[c] = winner;
        _newBodyCell[c] = winnerBody;
    }

    /// <summary>
    /// 空缺（張裂）的新地殼要長給哪個板塊：鄰居裡最多的那個，平手時優先 <paramref name="preferred"/>。
    /// </summary>
    private static int GapPlate(SimulationGrid grid, int c, int[] owners, int preferred)
    {
        Span<int> plates = stackalloc int[SimulationGrid.MaxNeighborsPerCell];
        Span<int> counts = stackalloc int[SimulationGrid.MaxNeighborsPerCell];
        var distinct = 0;
        var best = preferred;
        var bestCount = 0;
        var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
        for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
        {
            var n = grid.Neighbors[baseIndex + s];
            if (n == SimulationGrid.NoNeighbor) continue;
            var q = owners[n];
            var index = plates[..distinct].IndexOf(q);
            if (index < 0) { index = distinct++; plates[index] = q; counts[index] = 0; }
            counts[index]++;
            if (counts[index] > bestCount || (counts[index] == bestCount && q == preferred))
            {
                bestCount = counts[index];
                best = q;
            }
        }
        return best;
    }

    /// <summary>
    /// 兩塊地殼在同一個地方重疊（聚合）——用岩石圈淨浮力決定誰沉下去，回傳留下的那一個板塊。
    /// v1 的碰撞規則，常數共用 <see cref="PlateTectonics"/> 的：
    ///
    /// - 兩邊都是大陸或夠厚的島弧（<see cref="IsSuturable"/>）：誰都沉不下去，**縮短增厚**（大陸碰撞、島弧拼接），厚度整個疊上去、在位者留下（見方法內的說明）。
    /// - 否則浮得低的隱沒（差距在容差內就是比較老的那塊），一部分刮下來增生到留下的那一塊。
    ///   大陸碰上海洋時一定是海洋那一方沉——大陸跟著自己的板塊繼續往前，海洋在它前面消失，
    ///   直到另一邊的大陸撞上來。這就是海洋關閉、大陸碰撞的那一段威爾遜旋迴。
    ///
    /// 留下的一方**留在自己的座標系**，不轉交：轉交會讓大陸改跟著對方的板塊走。
    /// </summary>
    /// <param name="a">原本就佔著這一格的板塊（在位者）。</param>
    /// <param name="aBody">它在這一點的地殼點。</param>
    /// <param name="b">擠進來的板塊。</param>
    /// <param name="bBody">它在這一點的地殼點。</param>
    private int Resolve(int a, int aBody, int b, int bBody)
    {
        if (_origin[a][aBody] == _origin[b][bBody])
        {
            // 同一塊地殼的兩份拷貝（見 _origin 的說明）：留 a，丟掉 b
            _valid[b][bBody] = false;
            LastDuplicatesDropped++;
            return a;
        }

        // 不會被消耗的板塊（劇本的太平洋，2026-10-18 使用者指定：固定、演化中面積也不減）永遠留下。
        // 擠進來的洋殼照常隱沒到它底下（不增生，它保持原樣）；擠進來的大陸地殼沉不下去，等於撞上一道牆——
        // 前緣被吃掉的厚度記進碰撞增厚，攤到那塊大陸的前緣（DistributeCollisionThickening 只加在大陸格上），西岸因此長山。
        if (Unconsumable is { } fixedPlate && fixedPlate(a, b) != fixedPlate(b, a))
        {
            var (keep, gone, goneBody) = fixedPlate(a, b) ? (a, b, bBody) : (b, a, aBody);
            var goneThickness = _thickness[gone][goneBody];
            if (_crust[gone][goneBody] == CrustType.Continental)
            {
                LastShortenings++;
                _collisionBudget[_resolveCell] += goneThickness;
                UpliftSourcesKm[1] += goneThickness;
                _collisionUpper[_resolveCell] = gone;
                _collisionFromWall[_resolveCell] = true;
            }
            else
            {
                LastSubductions++;
            }
            ScrapeSediment(gone, goneBody, keep, fixedPlate(a, b) ? aBody : bBody);
            _valid[gone][goneBody] = false;
            LastConsumedPoints++;
            LastConsumedCrustKm += goneThickness;
            return keep;
        }

        var liftA = Isostasy.LithosphereBuoyancyMeters(_crust[a][aBody], _thickness[a][aBody], _density[a][aBody], _age[a][aBody]);
        var liftB = Isostasy.LithosphereBuoyancyMeters(_crust[b][bBody], _thickness[b][bBody], _density[b][bBody], _age[b][bBody]);
        // 縮短增厚只發生在**兩邊都浮得住**的時候：大陸碰大陸，或夠厚的島弧（見下）。一般洋殼一律由浮力低的那一方隱沒——
        // 隱沒是被聚合的力推著走的，浮力只決定誰在下面，連年輕的洋殼也照樣隱沒（例如 Cascadia）；
        // 洋殼被擠到增厚在地球上很少見。試過兩種更寬的條件（P4，2026-10-17），都讓洋殼大片增厚、浮到海平面附近成碎斑：
        // - 浮力差 < 500 公尺：年齡相近的老洋殼浮力差也很小，50 Myr 時 10~20 公里的增厚洋殼 3.7 萬格、海床中位深度只有 2,060 公尺
        // - 兩邊淨浮力都 ≥ 0：洋殼很年輕時幾乎都成立，縮短每步 790 次、隱沒反而只剩 130，胡椒 1,644
        //
        // **島弧拼接**（P4，2026-10-18）：夠厚的島弧也算——跟大陸或另一條島弧碰撞時一樣縮短拼接，不隱沒。
        // 少了這一條，島弧多半停在 15~25 公里、還不是大陸地殼，碰撞時照洋殼的規則被吃掉；3 億年長跑
        // 陸地停在 14%、而且是一條條細長的島弧，從來沒有拼成大陸（地球的大陸大多是島弧拼接長大的）。
        var shortening = IsSuturable(a, aBody) && IsSuturable(b, bBody);

        // 縮短增厚時**在位者留下**（a 是原本就佔著這一格的那個，呼叫端保證）：浮力差不多時，看浮力
        // 決定誰留下等於看極小的差異擲骰子，沿著同一條邊界這一格 p 贏、隔壁格 q 贏，兩塊板塊的地殼交錯咬合、
        // 切出一堆碎片（P4 設計 C 第一次跑 50 Myr：地殼歸屬 173 塊、邊界格佔全球 10%、滿地碎陸地）。
        // 隱沒與大陸壓海洋仍然看浮力——那裡浮力差大，較輕的那一方永遠贏，不會來回跳。
        var aWins = shortening
            || liftA > liftB + PlateTectonics.BuoyancyToleranceMeters
            || (MathF.Abs(liftA - liftB) <= PlateTectonics.BuoyancyToleranceMeters && _age[a][aBody] <= _age[b][bBody]);

        var (winner, winnerBody, loser, loserBody) = aWins ? (a, aBody, b, bBody) : (b, bBody, a, aBody);
        var loserThickness = _thickness[loser][loserBody];
        var winnerContinental = _crust[winner][winnerBody] == CrustType.Continental;
        var loserContinental = _crust[loser][loserBody] == CrustType.Continental;

        float gain;
        if (shortening)
        {
            // **縮短增厚**（2026-10-17，使用者提出）：浮力差不多、誰都沉不下去——地殼在原地被擠壓、
            // 褶皺、逆衝疊起來，面積變小、厚度增加，質量守恆，均衡再把它抬高。縮掉的面積由板塊運動在
            // 張裂處打開的空缺補回來（空缺會自動長新的海洋地殼）。
            //
            // 少了這一條，兩塊都浮著的地殼（大陸碰大陸、年輕洋殼、島弧碰島弧）照樣有一塊被整個消耗、
            // 贏家只拿 3%，地殼增厚不起來，大陸也長不大。有負浮力的（老而重的洋殼）仍然照常隱沒——
            // 要是所有聚合都變成增厚，就是 P2 量到的那種大陸棘輪。
            gain = loserThickness;
            LastShortenings++;
            if (winnerContinental && loserContinental)
            {
                LastContinentCollisions++;
                _collidingPairs.Add(winner < loser ? (winner, loser) : (loser, winner));
                // 大陸碰撞的增厚不疊在前緣這一格，先記下來、這一步的歸屬決定之後攤到整條碰撞帶（見 DistributeCollisionThickening）
                _collisionBudget[_resolveCell] += loserThickness;
                UpliftSourcesKm[0] += loserThickness;
                _collisionUpper[_resolveCell] = _plateSizes[winner] >= _plateSizes[loser] ? winner : loser;
                gain = 0f;
            }
        }
        else
        {
            gain = loserThickness * AccretedFractionOfSubductedCrust;
            UpliftSourcesKm[3] += gain;
            LastAccretedCrustKm += gain;
            LastSubductions++;
            if (winnerContinental) LastContinentOverrides++;
            // 安地斯型造山（只在劇本模式、只有指定的板塊隱沒時，見 AndeanSubductingPlate）：大陸前緣被擠壓縮短、增厚、起褶皺
            // 接觸的可以是大陸本身，也可以是大陸前緣那一窄條自己的海洋地殼（造山量之後交給最近的大陸格，見 MoveBudgetToContinent）
            if (AndeanSubductingPlate is { } andean && andean(loser) && !loserContinental)
            {
                _collisionBudget[_resolveCell] += AndeanReferenceCrustKm * AndeanShorteningFraction;
                UpliftSourcesKm[2] += AndeanReferenceCrustKm * AndeanShorteningFraction;
                _collisionUpper[_resolveCell] = winner;
                _collisionFromWall[_resolveCell] = true;
            }
        }
        var before = _thickness[winner][winnerBody];
        var after = MathF.Min(before + gain, Isostasy.MaxCrustThicknessKm);
        _thickness[winner][winnerBody] = after;

        // 密度按厚度加權混合：疊上來的是輸的那一塊的物質。**不往大陸地殼的密度拉**——被擠壓疊起來的洋殼
        // 還是玄武岩，原本往 2,700 拉的寫法讓 14 公里厚、仍屬海洋型的增厚地殼密度掉到 2,800、浮到海平面附近，
        // 再加上沉積物就是一大片碎斑（P4 實測 50 Myr：碎斑陸地 16,562 格，密度中位 2,800）。
        // 增厚到大陸的厚度還是會變成大陸地殼（跟島弧增厚同一個規則，見 PlateTectonics.ApplyConvergence）。
        if (after > before)
        {
            var added = after - before;
            _density[winner][winnerBody] = (_density[winner][winnerBody] * before + _density[loser][loserBody] * added) / after;
            // 轉成大陸地殼除了厚度還要夠輕（分異過）：被擠厚的玄武岩不是大陸（見 Isostasy.DifferentiatedCrustMaxDensity）
            if (_crust[winner][winnerBody] == CrustType.Oceanic && after >= Isostasy.ContinentalThresholdKm
                && _density[winner][winnerBody] <= Isostasy.DifferentiatedCrustMaxDensity
                && !_oceanicOnly[winner][winnerBody])
            {
                _crust[winner][winnerBody] = CrustType.Continental;
                LastContinentsFormed++;
            }
        }

        ScrapeSediment(loser, loserBody, winner, winnerBody);
        _valid[loser][loserBody] = false;
        LastConsumedPoints++;
        LastConsumedCrustKm += loserThickness;
        return winner;
    }

    /// <summary>
    /// 被吃掉的那一點上的沉積物刮下來、併進留下那一點的地殼（增積楔，v2 P7，2026-10-19 使用者選定）。
    /// 沉積物跟著地殼點走之後，到了隱沒帶一定要有去路：跟著隱沒掉的話大陸物質會慢慢流失，
    /// 留在原地又會在海溝那一格無上限累積。併進地殼（體積相同：沉積物的公尺換成地殼的公里），均衡再把它抬起來——
    /// 台灣、日本的增積岩體就是這樣長出來的。地殼頂到上限時多出來的那一點記在 <see cref="LastSedimentOverflowKm"/>。
    /// </summary>
    private void ScrapeSediment(int from, int fromBody, int to, int toBody)
    {
        var sediment = _sediment[from][fromBody];
        if (sediment <= 0f) return;
        _sediment[from][fromBody] = 0f;
        var addKm = sediment / 1000f;
        var before = _thickness[to][toBody];
        var after = MathF.Min(before + addKm, Isostasy.MaxCrustThicknessKm);
        _thickness[to][toBody] = after;
        LastScrapedSedimentKm += addKm;
        UpliftSourcesKm[4] += addKm;
        LastSedimentOverflowKm += addKm - (after - before);
    }

    /// <summary>這塊地殼碰撞時是否浮得住、會縮短拼接而不隱沒（見 <see cref="Isostasy.ResistsSubduction"/>）。</summary>
    private bool IsSuturable(int p, int b) => Isostasy.ResistsSubduction(_crust[p][b], _thickness[p][b], _density[p][b], _oceanicOnly[p][b]);

    private void ResetCounters()
    {
        LastNewCrustCells = 0;
        LastConsumedPoints = 0;
        LastConsumedCrustKm = 0;
        LastConsumedContinentalPoints = 0;
        LastSubductions = 0;
        LastContinentCollisions = 0;
        LastContinentOverrides = 0;
        LastShortenings = 0;
        LastContinentsFormed = 0;
        LastAccretedCrustKm = 0;
        LastScrapedSedimentKm = 0;
        LastSedimentOverflowKm = 0;
        LastDuplicatesDropped = 0;
        LastFragmentsAbsorbed = 0;
        LastFragmentCellsAbsorbed = 0;
        LastPlatesReborn = 0;
        LastPlatesWelded = 0;
        LastHiddenResolves = 0;
        LastHiddenShortenings = 0;
        _collidingPairs.Clear();
        Array.Clear(_collisionBudget);
        Array.Clear(_collisionFromWall);
    }

    /// <summary>把每格看得到的地殼寫進 <paramref name="state"/>。</summary>
    internal void Read(SimulationState state)
    {
        Parallel.For(0, _cellCount, c =>
        {
            var p = _owner[c];
            var b = _bodyCell[c];
            state.CrustThickness[c] = _thickness[p][b];
            state.CrustAge[c] = _age[p][b];
            state.CrustDensity[c] = _density[p][b];
            state.OrganicThickness[c] = _organic[p][b];
            state.StructuralRelief[c] = _relief[p][b];
            state.SedimentThickness[c] = _sediment[p][b];
            state.Crust[c] = _crust[p][b];
            state.OceanicOnly[c] = _oceanicOnly[p][b];
        });
    }

    /// <summary>
    /// 地質作用改完格子之後寫回。用「拉」的：板塊座標系裡**每一個**有地殼的點，去看自己現在在哪一格，
    /// 那一格顯示的是這個板塊的話就收下那一格這一步的修改；類別值（地殼型別、只能是海洋）改了的話直接照抄。
    ///
    /// 不能只「推」給正在顯示的那一點：網格旋轉之後有些點沒被任何格子對到（混疊），
    /// 那些點會永遠收不到地質作用，之後被顯示出來就是一顆顆的舊值。也不能拉絕對值：
    /// 兩點對到同一格時會把其中一點的地殼抄到另一點上。P2 的評比：拉差值時熱點加上去的地殼只遺失 0.36%。
    ///
    /// **正在顯示的那一點寫絕對值，只有混疊出來的其他點才加差值，而且要夾在物理範圍內。**
    /// v1 踩過（2026-09-07，見 <see cref="PlateTectonics.WriteGridBackToMarkers"/>）：差值寫回會把地殼一路壓到
    /// 厚度下限再也回不來。這裡的風險更具體：一點薄、一點厚對到同一格，侵蝕依厚的那一點算出很大的負差值，
    /// 薄的那一點也被扣一樣多。顯示的那一點寫絕對值，畫面上看到的永遠是精確的；
    /// 沒顯示的點只是近似，夾住才不會被推出範圍。
    /// </summary>
    internal void WriteBack(SimulationGrid grid, SimulationState state)
    {
        Parallel.For(0, _cellCount, c =>
        {
            var p = _owner[c];
            var b = _bodyCell[c];
            _dThickness[c] = state.CrustThickness[c] - _thickness[p][b];
            _dAge[c] = state.CrustAge[c] - _age[p][b];
            _dDensity[c] = state.CrustDensity[c] - _density[p][b];
            _dOrganic[c] = state.OrganicThickness[c] - _organic[p][b];
            _dRelief[c] = state.StructuralRelief[c] - _relief[p][b];
            _categoryChanged[c] = state.Crust[c] != _crust[p][b] || state.OceanicOnly[c] != _oceanicOnly[p][b];
        });

        var u = grid.UnitVector;
        for (var p = 0; p < _rotation.Length; p++)
        {
            var plate = p;
            Parallel.For(0, _cellCount, b =>
            {
                if (!_valid[plate][b]) return;
                // 對應格在這一步的 Step 已經算好（旋轉之後位置沒變）；讀檔之後第一次會從頭爬
                var (x, y, z) = SphereMath.Rotate(_rotation[plate], u[b * 3], u[b * 3 + 1], u[b * 3 + 2]);
                var g = SphereMath.ClimbToNearest(grid, _forward[plate][b], x, y, z);
                _forward[plate][b] = g;
                if (_owner[g] != plate) return;

                if (_bodyCell[g] == b)
                {
                    _thickness[plate][b] = state.CrustThickness[g];
                    _age[plate][b] = state.CrustAge[g];
                    _density[plate][b] = state.CrustDensity[g];
                    _organic[plate][b] = state.OrganicThickness[g];
                    _sediment[plate][b] = state.SedimentThickness[g];
                    _relief[plate][b] = state.StructuralRelief[g];
                }
                else
                {
                    _thickness[plate][b] = Math.Clamp(_thickness[plate][b] + _dThickness[g],
                        Isostasy.MinCrustThicknessKm, Isostasy.MaxCrustThicknessKm);
                    _age[plate][b] = MathF.Max(0f, _age[plate][b] + _dAge[g]);
                    _density[plate][b] += _dDensity[g];
                    _organic[plate][b] = MathF.Max(0f, _organic[plate][b] + _dOrganic[g]);
                    // 沉積物不加差值、直接照抄：一格的堆積是好幾十公尺到好幾公里，加差值的話混疊出來的點每一步都多拿一份，
                    // 之後被顯示出來就是一根兩倍厚的沉積柱（P7 第一次校準：40 Myr 單格 39 公里、四周 18 公里）
                    _sediment[plate][b] = state.SedimentThickness[g];
                    _relief[plate][b] = Math.Clamp(_relief[plate][b] + _dRelief[g], -MaxReliefMeters, MaxReliefMeters);
                }
                if (!_categoryChanged[g]) return;
                _crust[plate][b] = state.Crust[g];
                _oceanicOnly[plate][b] = state.OceanicOnly[g];
            });
        }
    }

    /// <summary>存檔：旋轉、每格的歸屬與對應點、每個板塊有地殼的點（只存有地殼的，否則每個板塊都是一整份網格）。</summary>
    internal void WriteTo(BinaryWriter writer)
    {
        writer.Write(_rotation.Length);
        foreach (var (w, x, y, z) in _rotation) { writer.Write(w); writer.Write(x); writer.Write(y); writer.Write(z); }
        SimulationSnapshot.WriteArray(writer, _owner);
        SimulationSnapshot.WriteArray(writer, _bodyCell);
        for (var p = 0; p < _rotation.Length; p++)
        {
            var points = Enumerable.Range(0, _cellCount).Where(b => _valid[p][b]).ToArray();
            writer.Write(points.Length);
            foreach (var b in points)
            {
                writer.Write(b);
                writer.Write(_thickness[p][b]); writer.Write(_age[p][b]);
                writer.Write(_density[p][b]); writer.Write(_organic[p][b]); writer.Write(_relief[p][b]); writer.Write(_sediment[p][b]);
                writer.Write((byte)_crust[p][b]); writer.Write(_oceanicOnly[p][b]);
                writer.Write(_origin[p][b]);
            }
        }
        writer.Write(_nextOrigin);
        writer.Write(_elapsedMyr);
        foreach (var d in _dormantUntilMyr) writer.Write(d);
    }

    /// <summary>
    /// 讀檔。爬山的起點快取（點→格的對應）不存——它只影響搜尋從哪裡開始，不影響找到哪一格，
    /// 所以續跑仍然位元相同。讀完要呼叫 <see cref="PublishOwnership"/>。
    /// </summary>
    internal void ReadFrom(BinaryReader reader)
    {
        var plateCount = reader.ReadInt32();
        if (plateCount != _rotation.Length) throw new InvalidDataException($"存檔有 {plateCount} 個板塊座標系，這裡是 {_rotation.Length} 個。");
        for (var p = 0; p < plateCount; p++)
        {
            _rotation[p] = (reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
        }
        SimulationSnapshot.ReadArray(reader, _owner);
        SimulationSnapshot.ReadArray(reader, _bodyCell);
        for (var p = 0; p < plateCount; p++)
        {
            Array.Clear(_valid[p]);
            Array.Clear(_usedStamp[p]);
            for (var b = 0; b < _cellCount; b++) _forward[p][b] = b;
            var count = reader.ReadInt32();
            for (var i = 0; i < count; i++)
            {
                var b = reader.ReadInt32();
                _thickness[p][b] = reader.ReadSingle(); _age[p][b] = reader.ReadSingle();
                _density[p][b] = reader.ReadSingle(); _organic[p][b] = reader.ReadSingle(); _relief[p][b] = reader.ReadSingle(); _sediment[p][b] = reader.ReadSingle();
                _crust[p][b] = (CrustType)reader.ReadByte(); _oceanicOnly[p][b] = reader.ReadBoolean();
                _origin[p][b] = reader.ReadInt32();
                _valid[p][b] = true;
            }
        }
        _nextOrigin = reader.ReadInt32();
        _elapsedMyr = reader.ReadDouble();
        for (var p = 0; p < plateCount; p++) _dormantUntilMyr[p] = reader.ReadDouble();
        _stamp = 0;
    }

    private T[][] Allocate<T>(int plateCount)
    {
        var arrays = new T[plateCount][];
        for (var p = 0; p < plateCount; p++) arrays[p] = new T[_cellCount];
        return arrays;
    }
}
