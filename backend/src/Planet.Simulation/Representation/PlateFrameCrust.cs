namespace Planet.Simulation.Representation;

/// <summary>
/// 地殼存在各板塊自己的座標系裡（v2 P2 選定的表示法，取代 v1 的標記法；評比見
/// docs/planet-evolution-v2-plan.md 的 P2 結果）。
///
/// 每個板塊有一份「網格大小」的地殼欄位，索引是**板塊座標系裡的固定點**（借用網格的格心當這些點）；
/// 板塊只記錄從起點到現在轉了多少（四元數）。格子要顯示時，把格心反轉回所屬板塊的座標系查那一點。
/// 地殼從來不被「搬」，所以板塊內部沒有取樣雜訊、誤差不累積。
///
/// ## 每一步
///
/// 1. 每個板塊的旋轉往前推一步；每格反轉回所屬板塊的座標系，找到對應點（爬山，通常 0~1 次）。
/// 2. 歸屬換了（q → p）而 p 在這裡有地殼：p 蓋過 q，q 的那一點**消耗**（隱沒）。
/// 3. 所屬板塊在這一點沒有地殼：先從舊歸屬與相鄰板塊**轉交**；都沒有才是真的張裂，長新的海洋地殼。
///    轉交這條不能省——少了它，邊界上一格的量化誤差就被當成張裂（P2 第一次跑，整球同一個旋轉 20 步少了 566 格大陸）。
/// 4. **被蓋住的地殼也消耗掉**：每個點往前算出自己現在在哪一格，那一格**和它的鄰居**都屬於別的板塊
///    （而且這一點這一步沒有被顯示）就是隱沒到別的板塊底下了。只靠第 2 步不夠——那只抓得到「歸屬換了的格子」，而板塊邊界不動、地殼自己
///    越過邊界的情況（聚合邊界最常見的樣子）格子歸屬從來沒換過。P3 第一次在 n=150 跑，新生地殼每步 264 格、
///    消耗只有 88 點，差額就是這些被蓋住卻沒算到的地殼；留著的話邊界一退回來它們會原封不動地冒出來。
/// 5. 把看得到的地殼寫進 <see cref="SimulationState"/>。
///
/// 地質作用改完格子之後呼叫 <see cref="WriteBack"/>，用「拉差值」寫回（見那裡的說明）。
///
/// ## 還沒有的（P4）
///
/// 第 2 步誰蓋過誰是由板塊歸屬（旋轉中的 Voronoi）決定的，**不看浮力**——大陸也會被消耗掉。
/// 隱沒、增生、大陸對撞這些「碰撞規則」是 P4 的範圍。
/// </summary>
internal sealed class PlateFrameCrust
{
    private readonly int _cellCount;
    private readonly (double W, double X, double Y, double Z)[] _rotation;
    private readonly float[][] _thickness, _age, _density, _organic;
    private readonly CrustType[][] _crust;
    private readonly bool[][] _oceanicOnly, _valid;
    private readonly int[][] _usedStamp, _forward;
    private int[] _owner, _bodyCell, _newOwner, _newBodyCell;
    private readonly int[] _consumedBody;
    private readonly float[] _dThickness, _dAge, _dDensity, _dOrganic;
    private readonly bool[] _categoryChanged;
    private readonly bool[] _hidden;
    private int _stamp;

    /// <summary>建立空的板塊座標系地殼。</summary>
    /// <param name="cellCount">格數。</param>
    /// <param name="plateCount">板塊數（固定，不分裂也不合併）。</param>
    internal PlateFrameCrust(int cellCount, int plateCount)
    {
        _cellCount = cellCount;
        _rotation = Enumerable.Repeat((1.0, 0.0, 0.0, 0.0), plateCount).ToArray();
        _thickness = Allocate<float>(plateCount); _age = Allocate<float>(plateCount);
        _density = Allocate<float>(plateCount); _organic = Allocate<float>(plateCount);
        _crust = Allocate<CrustType>(plateCount);
        _oceanicOnly = Allocate<bool>(plateCount); _valid = Allocate<bool>(plateCount);
        _usedStamp = Allocate<int>(plateCount);
        _forward = new int[plateCount][];
        for (var p = 0; p < plateCount; p++) _forward[p] = Enumerable.Range(0, cellCount).ToArray();
        _owner = new int[cellCount]; _bodyCell = new int[cellCount];
        _newOwner = new int[cellCount]; _newBodyCell = new int[cellCount];
        _consumedBody = new int[cellCount];
        _dThickness = new float[cellCount]; _dAge = new float[cellCount];
        _dDensity = new float[cellCount]; _dOrganic = new float[cellCount];
        _categoryChanged = new bool[cellCount];
        _hidden = new bool[cellCount];
    }

    /// <summary>上一步長出新海洋地殼的格數（張裂）。</summary>
    internal int LastNewCrustCells { get; private set; }

    /// <summary>上一步從相鄰板塊轉交過來的格數。</summary>
    internal int LastTransferredCells { get; private set; }

    /// <summary>上一步被蓋過而消耗掉的點數（隱沒）。</summary>
    internal int LastConsumedPoints { get; private set; }

    /// <summary>上一步消耗掉的地殼厚度總和（公里·格）。</summary>
    internal double LastConsumedCrustKm { get; private set; }

    /// <summary>上一步消耗掉的大陸地殼點數——P4 加上碰撞規則之前，這個數字應該要很大才對（大陸也被吃掉）。</summary>
    internal int LastConsumedContinentalPoints { get; private set; }

    /// <summary>所有板塊座標系裡有地殼的點數總和（比格數多的部分是被蓋住、還沒消耗的）。</summary>
    internal long ValidPoints
    {
        get
        {
            long total = 0;
            foreach (var valid in _valid) foreach (var v in valid) if (v) total++;
            return total;
        }
    }

    /// <summary>用目前格子上的地殼初始化：每格的地殼放進它所屬板塊的座標系、旋轉歸零。</summary>
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
            _crust[p][c] = state.Crust[c];
            _oceanicOnly[p][c] = state.OceanicOnly[c];
            _valid[p][c] = true;
        }
    }

    /// <summary>
    /// 推進一步並把看得到的地殼寫進 <paramref name="state"/>。呼叫前板塊已經 <see cref="PlateSet.Advance"/> 過。
    /// </summary>
    /// <param name="grid">網格。</param>
    /// <param name="state">模擬狀態（地殼欄位會被覆寫）。</param>
    /// <param name="plates">板塊。</param>
    /// <param name="deltaMyr">這一步的時間（百萬年）。</param>
    /// <param name="newCrustCells">選填：長出新海洋地殼的格子會被設成 true（給中洋脊火山用）。</param>
    internal void Step(SimulationGrid grid, SimulationState state, PlateSet plates, double deltaMyr, bool[]? newCrustCells = null)
    {
        for (var p = 0; p < _rotation.Length; p++)
        {
            _rotation[p] = SphereMath.Normalize(SphereMath.Multiply(SphereMath.StepRotation(plates.Plates[p], deltaMyr), _rotation[p]));
        }

        _stamp++;
        var owner = plates.PlateIdByCell;
        var u = grid.UnitVector;

        // 0. 老化：年齡是地殼自己的性質，要在板塊座標系裡長。
        //    引擎的老化在回寫**之後**（ApplyIsostasyAndFillWater），標記法每兩步才重新取樣一次、頂多少一步，
        //    這裡每步都重新讀，靠引擎老化的話年齡每一步都被讀回原值——P3 第一次跑，300 步後全球地殼年齡都是 0。
        var ageStep = (float)deltaMyr;
        for (var p = 0; p < _rotation.Length; p++)
        {
            var age = _age[p];
            var valid = _valid[p];
            Parallel.For(0, _cellCount, b => { if (valid[b]) age[b] += ageStep; });
        }

        // 1. 對應點；歸屬換了的格子也找出舊板塊在這裡的那一點
        Parallel.For(0, _cellCount, c =>
        {
            var p = owner[c];
            var (bx, by, bz) = SphereMath.Rotate(_rotation[p], u[c * 3], u[c * 3 + 1], u[c * 3 + 2], inverse: true);
            _newBodyCell[c] = p == _owner[c]
                ? SphereMath.ClimbToNearest(grid, _bodyCell[c], bx, by, bz)
                : grid.FindNearestCell(bx, by, bz);
            _newOwner[c] = p;

            _consumedBody[c] = -1;
            if (p == _owner[c]) return;
            var q = _owner[c];
            var (qx, qy, qz) = SphereMath.Rotate(_rotation[q], u[c * 3], u[c * 3 + 1], u[c * 3 + 2], inverse: true);
            _consumedBody[c] = grid.FindNearestCell(qx, qy, qz);
        });

        for (var c = 0; c < _cellCount; c++) _usedStamp[_newOwner[c]][_newBodyCell[c]] = _stamp;

        // 2. 消耗（隱沒）：歸屬換了、新板塊在這裡有地殼。舊板塊那一點這一步還有別的格子在顯示就不算（網格混疊）
        LastConsumedPoints = 0;
        LastConsumedCrustKm = 0;
        LastConsumedContinentalPoints = 0;
        for (var c = 0; c < _cellCount; c++)
        {
            var qBody = _consumedBody[c];
            if (qBody < 0 || !_valid[_newOwner[c]][_newBodyCell[c]]) continue;
            var q = _owner[c];
            if (!_valid[q][qBody] || _usedStamp[q][qBody] == _stamp) continue;
            _valid[q][qBody] = false;
            LastConsumedPoints++;
            LastConsumedCrustKm += _thickness[q][qBody];
            if (_crust[q][qBody] == CrustType.Continental) LastConsumedContinentalPoints++;
        }

        // 3. 轉交或新生
        LastNewCrustCells = 0;
        LastTransferredCells = 0;
        if (newCrustCells is not null) Array.Clear(newCrustCells);
        Span<int> candidates = stackalloc int[SimulationGrid.MaxNeighborsPerCell + 1];
        for (var c = 0; c < _cellCount; c++)
        {
            var p = _newOwner[c];
            var body = _newBodyCell[c];
            if (_valid[p][body]) continue;

            var count = 0;
            if (_owner[c] != p) candidates[count++] = _owner[c];
            var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                var n = grid.Neighbors[baseIndex + s];
                if (n == SimulationGrid.NoNeighbor) continue;
                var q = _newOwner[n];
                if (q != p && !candidates[..count].Contains(q)) candidates[count++] = q;
            }

            var transferred = false;
            for (var i = 0; i < count && !transferred; i++)
            {
                var q = candidates[i];
                var (qx, qy, qz) = SphereMath.Rotate(_rotation[q], u[c * 3], u[c * 3 + 1], u[c * 3 + 2], inverse: true);
                var qBody = grid.FindNearestCell(qx, qy, qz);
                if (!_valid[q][qBody]) continue;

                CopyPoint(q, qBody, p, body);
                if (_usedStamp[q][qBody] != _stamp) _valid[q][qBody] = false; // 搬走了；還有人在顯示就留著
                transferred = true;
                LastTransferredCells++;
            }

            if (!transferred)
            {
                var plate = plates.Plates[p];
                _thickness[p][body] = Isostasy.OceanicCrustThicknessKm;
                _age[p][body] = 0f;
                // 新生地殼繼承板塊的密度異常（否則重板塊會被自己擴張出來的普通地殼稀釋掉，見 v1 的 SeedRidgeMarker）
                _density[p][body] = Isostasy.OceanicCrustDensity + plate.DensityAnomalyKgPerM3;
                _organic[p][body] = 0f;
                _crust[p][body] = CrustType.Oceanic;
                _oceanicOnly[p][body] = plate.IsOceanicOnly;
                LastNewCrustCells++;
                if (newCrustCells is not null) newCrustCells[c] = true;
            }
            _valid[p][body] = true;
        }

        // 4. 被蓋住的地殼：每個點往前算出自己現在在哪一格（寫回也用這個對應，不必再算一次）
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
                if (owner[g] == plate || _usedStamp[plate][b] == _stamp) return;

                // 要整個落在別的板塊裡面（這一格跟它的鄰居都不屬於自己）才算被蓋住。
                // 貼著邊界的那一格不算：網格的正反兩個對應（格→點、點→格）在邊界上會差一格，
                // 不留這一格的寬容，整顆球同一個旋轉（沒有真的邊界）也會一路消耗地殼。
                // 真的在隱沒的地殼會繼續往裡面走，晚一格就被算到。
                var baseIndex = g * SimulationGrid.MaxNeighborsPerCell;
                for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
                {
                    var n = grid.Neighbors[baseIndex + s];
                    if (n != SimulationGrid.NoNeighbor && owner[n] == plate) return;
                }
                _hidden[b] = true;
            });
            for (var b = 0; b < _cellCount; b++)
            {
                if (!_hidden[b]) continue;
                _valid[plate][b] = false;
                LastConsumedPoints++;
                LastConsumedCrustKm += _thickness[plate][b];
                if (_crust[plate][b] == CrustType.Continental) LastConsumedContinentalPoints++;
            }
        }

        (_owner, _newOwner) = (_newOwner, _owner);
        (_bodyCell, _newBodyCell) = (_newBodyCell, _bodyCell);

        Read(state);
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
            state.Crust[c] = _crust[p][b];
            state.OceanicOnly[c] = _oceanicOnly[p][b];
        });
    }

    /// <summary>
    /// 地質作用改完格子之後寫回。用「拉」的：板塊座標系裡**每一個**有地殼的點，去看自己現在在哪一格，
    /// 那一格屬於這個板塊的話就加上那一格這一步的**變化量**；類別值（地殼型別、只能是海洋）改了的話直接照抄。
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
                }
                else
                {
                    _thickness[plate][b] = Math.Clamp(_thickness[plate][b] + _dThickness[g],
                        Isostasy.MinCrustThicknessKm, Isostasy.MaxCrustThicknessKm);
                    _age[plate][b] = MathF.Max(0f, _age[plate][b] + _dAge[g]);
                    _density[plate][b] += _dDensity[g];
                    _organic[plate][b] = MathF.Max(0f, _organic[plate][b] + _dOrganic[g]);
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
                writer.Write(_density[p][b]); writer.Write(_organic[p][b]);
                writer.Write((byte)_crust[p][b]); writer.Write(_oceanicOnly[p][b]);
            }
        }
    }

    /// <summary>
    /// 讀檔。爬山的起點快取（對應點的反向快取）不存——它只影響搜尋從哪裡開始，不影響找到哪一點，
    /// 所以續跑仍然位元相同。
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
                _density[p][b] = reader.ReadSingle(); _organic[p][b] = reader.ReadSingle();
                _crust[p][b] = (CrustType)reader.ReadByte(); _oceanicOnly[p][b] = reader.ReadBoolean();
                _valid[p][b] = true;
            }
        }
        _stamp = 0;
    }

    private void CopyPoint(int from, int fromBody, int to, int toBody)
    {
        _thickness[to][toBody] = _thickness[from][fromBody];
        _age[to][toBody] = _age[from][fromBody];
        _density[to][toBody] = _density[from][fromBody];
        _organic[to][toBody] = _organic[from][fromBody];
        _crust[to][toBody] = _crust[from][fromBody];
        _oceanicOnly[to][toBody] = _oceanicOnly[from][fromBody];
    }

    private T[][] Allocate<T>(int plateCount)
    {
        var arrays = new T[plateCount][];
        for (var p = 0; p < plateCount; p++) arrays[p] = new T[_cellCount];
        return arrays;
    }
}
