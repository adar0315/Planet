namespace Planet.Simulation.Representation;

/// <summary>
/// 候選 B：板塊座標系。每個板塊在**自己的**座標系裡存一份地殼（用同一套網格當作板塊上的固定點），
/// 板塊只記錄「從起點到現在轉了多少」（一個四元數）。格子要顯示時，把格心反轉回目前所屬板塊的座標系，
/// 取那一點的地殼。
///
/// ## 為什麼板塊內部是零雜訊
///
/// 標記法每次重新取樣都把地殼「搬」到新的格子上，取樣誤差會一輪一輪累積成碎裂。
/// 這裡地殼**從來不搬**——它一直待在板塊座標系的同一個位置，每一步都從原始資料重新查一次，
/// 誤差有界、不累積。
///
/// ## 只有邊界要處理
///
/// - **聚合（被蓋過）**：一格的歸屬從板塊 q 換成 p，q 在這一點的地殼就被 p 蓋住了 → 標記成已消耗，
///   之後即使邊界退回來也不會再冒出來（它已經隱沒了）。
/// - **張裂（新露出）**：一格屬於 p，但 p 的座標系在這一點從來沒有地殼 → 長出新的海洋地殼，寫進 p 的座標系。
///
/// ## 已知風險（P3/P4 要處理）
///
/// 網格旋轉之後跟板塊座標系的網格不再對齊，最近格取樣會有「兩格對到同一點、某點沒有格子對到」的混疊。
/// 純搬運時無害（誤差有界），但地質作用寫回板塊座標系時，沒被對到的那一點會漏掉修改。
/// </summary>
internal sealed class PlateFrameCarrier : ICrustCarrier
{
    private (double W, double X, double Y, double Z)[] _rotation = [];
    private float[][] _thickness = [];
    private bool[][] _continental = [];
    private bool[][] _valid = [];
    private int[][] _usedStamp = [];
    private int[][] _forward = [];
    private float[] _delta = [];
    private int[] _owner = [], _bodyCell = [];
    private int[] _newOwner = [], _newBodyCell = [], _consumedBody = [];
    private int _stamp;

    /// <inheritdoc />
    public string Name => "B：板塊座標系";

    /// <inheritdoc />
    public void Initialize(SimulationGrid grid, PlateSet plates, float[] thicknessKm, bool[] continental)
    {
        var plateCount = plates.Plates.Length;
        _rotation = Enumerable.Repeat((1.0, 0.0, 0.0, 0.0), plateCount).ToArray();
        _thickness = new float[plateCount][];
        _continental = new bool[plateCount][];
        _valid = new bool[plateCount][];
        _usedStamp = new int[plateCount][];
        _forward = new int[plateCount][];
        _delta = new float[grid.CellCount];
        for (var p = 0; p < plateCount; p++)
        {
            _thickness[p] = new float[grid.CellCount];
            _continental[p] = new bool[grid.CellCount];
            _valid[p] = new bool[grid.CellCount];
            _usedStamp[p] = new int[grid.CellCount];
            _forward[p] = Enumerable.Range(0, grid.CellCount).ToArray();
        }

        _owner = (int[])plates.PlateIdByCell.Clone();
        _bodyCell = Enumerable.Range(0, grid.CellCount).ToArray();
        _newOwner = new int[grid.CellCount];
        _newBodyCell = new int[grid.CellCount];
        _consumedBody = new int[grid.CellCount];
        _stamp = 0;

        for (var c = 0; c < grid.CellCount; c++)
        {
            var p = _owner[c];
            _thickness[p][c] = thicknessKm[c];
            _continental[p][c] = continental[c];
            _valid[p][c] = true;
        }
    }

    /// <inheritdoc />
    public void Step(SimulationGrid grid, PlateSet plates, double deltaMyr)
    {
        for (var p = 0; p < _rotation.Length; p++)
        {
            _rotation[p] = SphereMath.Normalize(SphereMath.Multiply(SphereMath.StepRotation(plates.Plates[p], deltaMyr), _rotation[p]));
        }

        _stamp++;
        var owner = plates.PlateIdByCell;
        var u = grid.UnitVector;

        // 1. 每格反轉回所屬板塊的座標系，找到對應的那一點；歸屬換了的格子，也找出舊板塊在這裡的那一點
        Parallel.For(0, grid.CellCount, c =>
        {
            var p = owner[c];
            var (bx, by, bz) = SphereMath.Rotate(_rotation[p], u[c * 3], u[c * 3 + 1], u[c * 3 + 2], inverse: true);
            // 歸屬沒變：上一步的對應點就在旁邊，爬山就好；換了板塊：座標系完全不同，要全域找
            _newBodyCell[c] = p == _owner[c]
                ? SphereMath.ClimbToNearest(grid, _bodyCell[c], bx, by, bz)
                : grid.FindNearestCell(bx, by, bz);
            _newOwner[c] = p;

            _consumedBody[c] = -1;
            if (p != _owner[c])
            {
                var q = _owner[c];
                var (qx, qy, qz) = SphereMath.Rotate(_rotation[q], u[c * 3], u[c * 3 + 1], u[c * 3 + 2], inverse: true);
                _consumedBody[c] = grid.FindNearestCell(qx, qy, qz);
            }
        });

        for (var c = 0; c < grid.CellCount; c++) _usedStamp[_newOwner[c]][_newBodyCell[c]] = _stamp;

        // 2. 歸屬換了的格子（q → p），而且 p 在這裡有地殼 → p 蓋過 q，q 的那一點消耗掉（聚合）。
        //    q 的那一點這一步如果還有別的格子在顯示（網格混疊），就不算被蓋過，不動它。
        for (var c = 0; c < grid.CellCount; c++)
        {
            var qBody = _consumedBody[c];
            if (qBody < 0 || !_valid[_newOwner[c]][_newBodyCell[c]]) continue;
            var q = _owner[c];
            if (_usedStamp[q][qBody] != _stamp) _valid[q][qBody] = false;
        }

        // 3. 所屬板塊在這一點沒有地殼：先看舊的歸屬與相鄰的板塊在這一點有沒有，有就**轉交**過來；
        //    都沒有才是真的張裂，長新的海洋地殼。
        //
        //    不轉交的話，邊界上一格的量化誤差就會被當成張裂（2026-10-17 第一次跑）：
        //    板塊歸屬是旋轉中的 Voronoi 決定的，貼著邊界的格子，反轉回自己板塊之後可能落在
        //    「起始時屬於隔壁板塊」的那一點上。整顆球同一個旋轉、根本沒有真的邊界，
        //    20 步就少了 566 格大陸。v1 的標記也是改歸屬到覆蓋它的板塊，道理相同。
        Span<int> candidates = stackalloc int[SimulationGrid.MaxNeighborsPerCell + 1];
        for (var c = 0; c < grid.CellCount; c++)
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

                _thickness[p][body] = _thickness[q][qBody];
                _continental[p][body] = _continental[q][qBody];
                if (_usedStamp[q][qBody] != _stamp) _valid[q][qBody] = false; // 搬走了；還有人在顯示就留著
                transferred = true;
            }

            if (!transferred)
            {
                _thickness[p][body] = Isostasy.OceanicCrustThicknessKm;
                _continental[p][body] = false;
            }
            _valid[p][body] = true;
        }

        (_owner, _newOwner) = (_newOwner, _owner);
        (_bodyCell, _newBodyCell) = (_newBodyCell, _bodyCell);
    }

    /// <summary>
    /// 用「拉」的寫回：板塊座標系裡**每一個**有地殼的點，都去看自己現在在哪一格、加上那一格這一步被改了多少。
    ///
    /// 不能只「推」給正在顯示的那一點：網格旋轉之後有些點沒被任何格子對到（混疊），
    /// 那些點會永遠收不到地質作用，等它們之後被顯示出來就是一顆顆沒被改過的舊值（胡椒）。
    /// 也不能拉**絕對值**：兩點對到同一格時，會把其中一點的地殼抄到另一點上。
    /// 拉差值時，同一格的修改會加到對到它的每一點上——局部可能多加或少加，平均起來守恆。
    /// </summary>
    public void WriteBack(SimulationGrid grid, float[] thicknessKm)
    {
        for (var c = 0; c < _delta.Length; c++) _delta[c] = thicknessKm[c] - _thickness[_owner[c]][_bodyCell[c]];

        var u = grid.UnitVector;
        for (var p = 0; p < _rotation.Length; p++)
        {
            var plate = p;
            Parallel.For(0, grid.CellCount, b =>
            {
                if (!_valid[plate][b]) return;
                var (x, y, z) = SphereMath.Rotate(_rotation[plate], u[b * 3], u[b * 3 + 1], u[b * 3 + 2]);
                var g = SphereMath.ClimbToNearest(grid, _forward[plate][b], x, y, z);
                _forward[plate][b] = g;
                if (_owner[g] == plate) _thickness[plate][b] += _delta[g];
            });
        }
    }

    /// <inheritdoc />
    public void Read(float[] thicknessKm, bool[] continental)
    {
        for (var c = 0; c < _owner.Length; c++)
        {
            var p = _owner[c];
            var body = _bodyCell[c];
            thicknessKm[c] = _thickness[p][body];
            continental[c] = _continental[p][body];
        }
    }
}
