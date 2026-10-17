namespace Planet.Simulation.Representation;

/// <summary>
/// 標記法（marker-in-cell）。兩種用法：
///
/// - **v1 基準**：每格 1 個標記，漂移滿一格中心間距才重新取樣——照 <see cref="PlateTectonics"/> 的
///   搬運部分抄的（擁擠的留厚的、多的暫存給附近的空格、真張裂才生新地殼、其餘跟鄰居借），
///   地質作用拿掉。用來確認比較框架量得到 v1 的雜訊。
/// - **候選 A**：每格多個標記。v1 試過 4 個，因為厚度用「總和 ÷ N」推導，標記數的量化雜訊
///   把大陸抹薄了；這裡同時量「總和 ÷ N」與「平均」兩種讀法，驗證規劃裡「換成體積能不能避開」。
/// </summary>
internal sealed class MarkerCarrier : ICrustCarrier
{
    private readonly int _perCell;
    private readonly bool _averageThickness;

    private double[] _x = [], _y = [], _z = [];
    private float[] _thickness = [];
    private bool[] _continental = [];
    private int[] _plate = [], _cell = [];
    private bool[] _alive = [];
    private int _count;

    private float[] _cellThickness = [];
    private bool[] _cellContinental = [];
    private int[] _bucketStart = [], _bucketCount = [], _bucket = [];
    private byte[] _kinds = [];
    private double _driftMeters;
    private double _spacingMeters;

    /// <summary>建立標記法。</summary>
    /// <param name="markersPerCell">每格幾個標記（v1 是 1）。</param>
    /// <param name="averageThickness">
    /// false：格子厚度 ＝ 標記厚度總和 ÷ N（v1 的讀法，標記多擠一個就變厚）；
    /// true：格子厚度 ＝ 標記厚度的平均（不受標記數量影響，但不守恆）。
    /// </param>
    internal MarkerCarrier(int markersPerCell, bool averageThickness)
    {
        _perCell = markersPerCell;
        _averageThickness = averageThickness;
        Name = markersPerCell == 1
            ? "v1 基準（1 標記／格）"
            : $"A：{markersPerCell} 標記／格，{(averageThickness ? "平均" : "總和÷N")}";
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public void Initialize(SimulationGrid grid, PlateSet plates, float[] thicknessKm, bool[] continental)
    {
        var capacity = grid.CellCount * _perCell * 3;
        _x = new double[capacity]; _y = new double[capacity]; _z = new double[capacity];
        _thickness = new float[capacity];
        _continental = new bool[capacity];
        _plate = new int[capacity]; _cell = new int[capacity];
        _alive = new bool[capacity];
        _bucket = new int[capacity + grid.CellCount * _perCell];
        _bucketStart = new int[grid.CellCount];
        _bucketCount = new int[grid.CellCount];
        _cellThickness = (float[])thicknessKm.Clone();
        _cellContinental = (bool[])continental.Clone();
        _kinds = new byte[grid.CellCount];
        _spacingMeters = Math.Sqrt(grid.AverageCellAreaSquareMeters / (3.0 * Math.Sqrt(3.0) / 2.0)) * Math.Sqrt(3.0);
        _count = 0;
        _driftMeters = 0;

        var u = grid.UnitVector;
        Span<int> neighbors = stackalloc int[SimulationGrid.MaxNeighborsPerCell];
        for (var c = 0; c < grid.CellCount; c++)
        {
            var count = grid.GetNeighbors(c, neighbors);
            for (var k = 0; k < _perCell; k++)
            {
                double x = u[c * 3], y = u[c * 3 + 1], z = u[c * 3 + 2];
                if (k > 0)
                {
                    // 往不同的鄰居方向偏 0.35 格：仍然最靠近自己這一格，但不全疊在中心
                    var n = neighbors[(2 * k - 2) % count];
                    x += 0.35 * (u[n * 3] - x); y += 0.35 * (u[n * 3 + 1] - y); z += 0.35 * (u[n * 3 + 2] - z);
                    var length = Math.Sqrt(x * x + y * y + z * z);
                    x /= length; y /= length; z /= length;
                }
                Add(x, y, z, thicknessKm[c], continental[c], plates.PlateIdByCell[c], c);
            }
        }
    }

    /// <inheritdoc />
    public void Step(SimulationGrid grid, PlateSet plates, double deltaMyr)
    {
        var maxAngle = 0.0;
        foreach (var plate in plates.Plates)
        {
            maxAngle = Math.Max(maxAngle, Math.Abs(plate.AngularVelocityDegPerMyr * Math.PI / 180.0 * deltaMyr));
        }

        var rotations = plates.Plates.Select(p => SphereMath.StepRotation(p, deltaMyr)).ToArray();
        Parallel.For(0, _count, m =>
        {
            if (!_alive[m]) return;
            var (x, y, z) = SphereMath.Rotate(rotations[_plate[m]], _x[m], _y[m], _z[m]);
            var length = Math.Sqrt(x * x + y * y + z * z);
            _x[m] = x / length; _y[m] = y / length; _z[m] = z / length;
        });

        // 跟 v1 一樣：漂移滿一格中心間距才重新取樣（這正是 v1「批次化」的來源，照抄才量得到）
        _driftMeters += maxAngle * grid.PlanetRadiusMeters;
        if (_driftMeters < _spacingMeters) return;
        _driftMeters = 0;
        Rasterize(grid, plates);
    }

    /// <inheritdoc />
    public void WriteBack(SimulationGrid grid, float[] thicknessKm)
    {
        // 跟 v1 一樣用差值寫回落在這格的每個標記（直接覆蓋會讓同一格的標記變成一模一樣）。
        // 標記的「所在格」是上一次取樣時的格子——v1 的 _ownerMarker 也是這個語意。
        var delta = new float[thicknessKm.Length];
        for (var c = 0; c < delta.Length; c++) delta[c] = thicknessKm[c] - _cellThickness[c];
        var counts = new int[delta.Length];
        for (var m = 0; m < _count; m++) if (_alive[m]) counts[_cell[m]]++;
        for (var m = 0; m < _count; m++)
        {
            if (!_alive[m]) continue;
            var c = _cell[m];
            // 平均：每個標記加上差值，平均就加上差值。
            // 總和÷N：k 個標記各加 差值×N/k，總和÷N 才剛好加上差值。
            _thickness[m] += _averageThickness ? delta[c] : delta[c] * _perCell / Math.Max(counts[c], 1);
        }
        Array.Copy(thicknessKm, _cellThickness, thicknessKm.Length);
    }

    /// <inheritdoc />
    public void Read(float[] thicknessKm, bool[] continental)
    {
        Array.Copy(_cellThickness, thicknessKm, _cellThickness.Length);
        Array.Copy(_cellContinental, continental, _cellContinental.Length);
    }

    private void Rasterize(SimulationGrid grid, PlateSet plates)
    {
        PlateBoundaryKinds.Classify(grid, plates, _kinds);

        // 1. 每個標記落到哪一格（局部爬山，跟 v1 一樣從上一輪的格子開始）
        Parallel.For(0, _count, m =>
        {
            if (_alive[m]) _cell[m] = SphereMath.ClimbToNearest(grid, _cell[m], _x[m], _y[m], _z[m]);
        });

        // 2. 分桶（CSR），每格至少預留 N 個位置給補滿用
        Array.Clear(_bucketCount);
        for (var m = 0; m < _count; m++) if (_alive[m]) _bucketCount[_cell[m]]++;
        var running = 0;
        for (var c = 0; c < grid.CellCount; c++)
        {
            _bucketStart[c] = running;
            running += Math.Max(_bucketCount[c], _perCell) + _perCell;
            _bucketCount[c] = 0;
        }
        if (running > _bucket.Length) _bucket = new int[running * 2];
        for (var m = 0; m < _count; m++)
        {
            if (!_alive[m]) continue;
            var c = _cell[m];
            _bucket[_bucketStart[c] + _bucketCount[c]++] = m;
        }

        // 3. 擠：聚合邊界上多的隱沒掉（留厚的）；其他地方多的放進暫存，給附近的空格（守恆）
        var spare = new int[grid.CellCount];
        Array.Fill(spare, -1);
        for (var c = 0; c < grid.CellCount; c++)
        {
            while (_bucketCount[c] > _perCell)
            {
                var start = _bucketStart[c];
                var thinnest = 0;
                for (var i = 1; i < _bucketCount[c]; i++)
                {
                    if (_thickness[_bucket[start + i]] < _thickness[_bucket[start + thinnest]]) thinnest = i;
                }
                var m = _bucket[start + thinnest];
                _bucket[start + thinnest] = _bucket[start + _bucketCount[c] - 1];
                _bucketCount[c]--;

                if (_kinds[c] != PlateBoundaryKinds.Convergent && spare[c] < 0) spare[c] = m;
                else _alive[m] = false;
            }
        }

        // 4. 缺：真張裂補到滿（新海洋地殼）；其餘的空格先拿附近的暫存標記，沒有才跟鄰居借
        var u = grid.UnitVector;
        for (var c = 0; c < grid.CellCount; c++)
        {
            if (_kinds[c] == PlateBoundaryKinds.Divergent)
            {
                while (_bucketCount[c] < _perCell)
                {
                    var fresh = Add(u[c * 3], u[c * 3 + 1], u[c * 3 + 2], Isostasy.OceanicCrustThicknessKm, false, plates.PlateIdByCell[c], c);
                    _bucket[_bucketStart[c] + _bucketCount[c]++] = fresh;
                }
                continue;
            }
            if (_bucketCount[c] > 0) continue;

            var moved = TakeSpare(grid, spare, c);
            if (moved >= 0)
            {
                _x[moved] = u[c * 3]; _y[moved] = u[c * 3 + 1]; _z[moved] = u[c * 3 + 2];
                _cell[moved] = c;
                _bucket[_bucketStart[c] + _bucketCount[c]++] = moved;
                continue;
            }

            var (thickness, continental) = Borrow(grid, c);
            var created = Add(u[c * 3], u[c * 3 + 1], u[c * 3 + 2], thickness, continental, plates.PlateIdByCell[c], c);
            _bucket[_bucketStart[c] + _bucketCount[c]++] = created;
        }
        for (var c = 0; c < grid.CellCount; c++) if (spare[c] >= 0) _alive[spare[c]] = false;

        // 5. 讀出格子的值；標記改歸屬到覆蓋它的板塊（v1 同樣這麼做）
        for (var c = 0; c < grid.CellCount; c++)
        {
            var start = _bucketStart[c];
            var count = _bucketCount[c];
            if (count == 0) continue;

            var sum = 0f;
            var continentalCount = 0;
            var top = _bucket[start];
            for (var i = 0; i < count; i++)
            {
                var m = _bucket[start + i];
                sum += _thickness[m];
                if (_continental[m]) continentalCount++;
                if (_thickness[m] > _thickness[top]) top = m;
                _plate[m] = plates.PlateIdByCell[c];
            }

            _cellThickness[c] = _averageThickness ? sum / count : sum / _perCell;
            // v1 取浮得最高的那一塊當表層；多標記平均時用多數決，否則一個大陸標記就讓整格變大陸
            _cellContinental[c] = _averageThickness ? continentalCount * 2 >= count : _continental[top];
        }

        Compact();
    }

    private int TakeSpare(SimulationGrid grid, int[] spare, int c)
    {
        var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
        for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
        {
            var n = grid.Neighbors[baseIndex + s];
            if (n == SimulationGrid.NoNeighbor || spare[n] < 0) continue;
            var m = spare[n];
            spare[n] = -1;
            return m;
        }
        return -1;
    }

    /// <summary>跟鄰居借：取第一個有標記的鄰居目前的值（v1 的 BorrowFromNeighbours 也是複製鄰居）。</summary>
    private (float Thickness, bool Continental) Borrow(SimulationGrid grid, int c)
    {
        var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
        for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
        {
            var n = grid.Neighbors[baseIndex + s];
            if (n == SimulationGrid.NoNeighbor || _bucketCount[n] == 0) continue;
            var m = _bucket[_bucketStart[n]];
            return (_thickness[m], _continental[m]);
        }
        return (Isostasy.OceanicCrustThicknessKm, false);
    }

    private int Add(double x, double y, double z, float thickness, bool continental, int plate, int cell)
    {
        if (_count == _x.Length) Grow();
        var m = _count++;
        _x[m] = x; _y[m] = y; _z[m] = z;
        _thickness[m] = thickness;
        _continental[m] = continental;
        _plate[m] = plate;
        _cell[m] = cell;
        _alive[m] = true;
        return m;
    }

    private void Grow()
    {
        var size = _x.Length * 2;
        Array.Resize(ref _x, size); Array.Resize(ref _y, size); Array.Resize(ref _z, size);
        Array.Resize(ref _thickness, size); Array.Resize(ref _continental, size);
        Array.Resize(ref _plate, size); Array.Resize(ref _cell, size); Array.Resize(ref _alive, size);
    }

    private void Compact()
    {
        var write = 0;
        for (var m = 0; m < _count; m++)
        {
            if (!_alive[m]) continue;
            if (write != m)
            {
                _x[write] = _x[m]; _y[write] = _y[m]; _z[write] = _z[m];
                _thickness[write] = _thickness[m]; _continental[write] = _continental[m];
                _plate[write] = _plate[m]; _cell[write] = _cell[m];
                _alive[write] = true;
            }
            write++;
        }
        for (var m = write; m < _count; m++) _alive[m] = false;
        _count = write;
    }
}
