using Planet.Simulation.Representation;

namespace Planet.Simulation;

// 板塊邊界：分類、聚合線段、板塊動力學（2026-11-17 從 PlateTectonics.cs 拆出來，內容沒改）
public sealed partial class PlateTectonics
{
    /// <summary>
    /// 依兩側板塊的相對運動把每格分類成聚合／張裂／轉形。
    /// 判準：相對速度在「指向鄰居」方向上的分量——為正代表在靠近（聚合），為負代表在遠離（張裂）；
    /// 若這個分量相對於總相對速度太小，代表主要是側向錯動，屬於轉形邊界。
    /// </summary>
    private void ClassifyBoundaries(SimulationGrid grid, SimulationState state, PlateSet plates)
    {
        Parallel.For(0, grid.CellCount, cell =>
        {
            state.Boundary[cell] = BoundaryType.None;
            _convergenceRate[cell] = 0;
            _convergingNeighbor[cell] = -1;
            _boundaryOtherPlate[cell] = -1;

            var myPlate = plates.OwnerByCell[cell];
            var (vx, vy, vz) = plates.VelocityAt(grid, cell);

            var rx = grid.UnitVector[cell * 3];
            var ry = grid.UnitVector[cell * 3 + 1];
            var rz = grid.UnitVector[cell * 3 + 2];

            var strongestNormal = 0.0;
            var strongestRelative = 0.0;
            var strongestNeighbor = -1;

            var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                var neighbor = grid.Neighbors[baseIndex + s];
                if (neighbor == SimulationGrid.NoNeighbor) continue;
                if (plates.OwnerByCell[neighbor] == myPlate) continue;

                // 指向鄰居的地表切線方向（把徑向分量去掉，只留下沿著球面的方向）
                var dx = grid.UnitVector[neighbor * 3] - rx;
                var dy = grid.UnitVector[neighbor * 3 + 1] - ry;
                var dz = grid.UnitVector[neighbor * 3 + 2] - rz;
                var radial = dx * rx + dy * ry + dz * rz;
                dx -= radial * rx;
                dy -= radial * ry;
                dz -= radial * rz;
                var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (length < 1e-12) continue;
                dx /= length; dy /= length; dz /= length;

                var (nvx, nvy, nvz) = plates.VelocityAt(grid, neighbor);
                var relX = vx - nvx;
                var relY = vy - nvy;
                var relZ = vz - nvz;

                // 正值＝這格正朝鄰居靠近
                var normalComponent = relX * dx + relY * dy + relZ * dz;
                if (Math.Abs(normalComponent) <= Math.Abs(strongestNormal)) continue;

                strongestNormal = normalComponent;
                strongestRelative = Math.Sqrt(relX * relX + relY * relY + relZ * relZ);
                strongestNeighbor = neighbor;
            }

            if (strongestNeighbor < 0) return;

            var isMostlyShear = strongestRelative > 0
                && Math.Abs(strongestNormal) / strongestRelative < NormalComponentRatioForTransform;

            _boundaryOtherPlate[cell] = plates.OwnerByCell[strongestNeighbor];

            if (isMostlyShear)
            {
                state.Boundary[cell] = BoundaryType.Transform;
            }
            else if (strongestNormal > 0)
            {
                state.Boundary[cell] = BoundaryType.Convergent;
                _convergenceRate[cell] = strongestNormal;
                _convergingNeighbor[cell] = strongestNeighbor;
            }
            else
            {
                state.Boundary[cell] = BoundaryType.Divergent;
            }
        });

        TraceConvergentSegments(grid, state, plates);
    }

    /// <summary>
    /// 一條聚合線段要多長，才建得起完整強度的造山帶（公里）。
    ///
    /// 真實的造山帶／隱沒邊界都是**很長的線**：喜馬拉雅約2,400公里、日本島弧約1,500公里、
    /// 安地斯約7,000公里。幾百公里的短段落不會長出山脈——應變在那種尺度上是被
    /// 旋轉與走滑消化掉的，不會累積成持續的縮短。取1,500，日本島弧那個量級。
    /// </summary>
    private const double FullOrogenyLengthKm = 1500;

    /// <summary>
    /// 線段末端的衰減長度（公里）。
    ///
    /// 邊界不會在端點戛然而止：末端是傳遞帶（transfer zone），縮短量在那裡逐漸交棒給
    /// 旁邊的構造，所以造山帶的兩端是尖滅的、不是齊頭切斷。三重接合點正是這種端點，
    /// 於是「接合點要打折」變成這條規則的自然結果，不必另外寫一條。取300公里。
    /// </summary>
    private const double SegmentTipTaperKm = 300;

    /// <summary>
    /// 同一條線段內，聚合速率容許差異多少比例。
    ///
    /// **作用線不可能是連續的一整條**（使用者指出，2026-09-07）：球面上剛體旋轉的相對速度
    /// 正比於 sin(離歐拉極的角距)，所以一條長邊界各處的速率本來就不同。
    /// 真實的中洋脊因此不是一條連續曲線，而是一段一段垂直於運動方向的擴張段，
    /// 被平行於運動方向的轉形斷層錯開——階梯狀。隱沒帶同理（阿留申、湯加都是分段的）。
    ///
    /// 原本沿著整條線取平均速率，等於把造成分段的那個變化抹掉了。
    /// 現在泛洪時只有速率跟種子格相近的鄰居才併入同一段，差太多就斷成另一段，
    /// 各段各自用自己的平均速率——沿線平均去掉的仍然是格子尺度的雜訊，
    /// 但**沿走向的真實變化被保留下來**。
    ///
    /// 轉形段落其實已經在斷了（線段只收聚合格，所以「聚合→轉形→聚合」本來就會斷成兩段），
    /// 這裡補的是**純聚合段落內部**的速率變化。
    ///
    /// 取0.25：速率變化超過四分之一就算換了一段。
    /// </summary>
    private const double SegmentRateCoherence = 0.25;

    /// <summary>板塊動力學：由力矩平衡解出歐拉極與角速度。診斷時可以讀它的每板塊力。</summary>
    public PlateDynamics Dynamics { get; } = new();

    /// <summary>診斷用：目前的聚合線段數量。</summary>
    public int LastSegmentCount { get; private set; }

    /// <summary>診斷用：最長的聚合線段有多長（公里）。</summary>
    public double LastLongestSegmentKm { get; private set; }

    /// <summary>診斷用：聚合格的作用權重平均值。用來判斷總造陸量掉了多少。</summary>
    public double LastMeanConvergenceWeight { get; private set; }

    /// <summary>
    /// 把聚合邊界連成**線段**，讓作用以整條線為單位，而不是逐格各自為政。
    ///
    /// ## 為什麼要有線段這個層次
    ///
    /// 板塊構造不是「一塊區域撞上另一塊區域」，而是**很多條線，線的兩側在互相推擠或拉開**。
    /// 原本每個邊界格各自從自己最強的鄰居算出聚合速率、各自增厚，等於把一條連續的隱沒帶
    /// 當成幾百個互不相干的小事件；線段的長度、連續性、兩端怎麼收尾，全部沒有被表達出來。
    ///
    /// 而且板塊是球面 Voronoi 胞，邊界是「直邊＋三重接合點」構成的網。逐格獨立作用時，
    /// 接合點就成了三條邊的效果疊加處，畫出來是一顆顆 Y 形放射狀團塊——
    /// 那是**鑲嵌的幾何被印到星球上**，不是地質。
    ///
    /// ## 一條線段是什麼
    ///
    /// 相鄰、都是聚合、而且**分隔同一對板塊**的格子。分隔的板塊對變了就代表跨到另一條邊去了，
    /// 中間那個轉折正是接合點——所以接合點自然成為線段的端點，不必另外偵測。
    ///
    /// ## 三個效果
    ///
    /// - **長度門檻**：線段短於 <see cref="FullOrogenyLengthKm"/> 就按比例打折。
    /// - **末端衰減**：離端點 <see cref="SegmentTipTaperKm"/> 以內逐漸尖滅（三重接合點就是端點）。
    /// - **沿線平均**：用整條線段的平均聚合速率，取代逐格各自算出來的值。逐格的值是從
    ///   「自己最強的那個鄰居」來的、在格子尺度上很跳；一條線段整體被推擠的速率才有物理意義。
    ///
    /// 長度一律換算成**公里**再比較，不是格數——n=150 與 n=24 的格距差6倍（見 CLAUDE.md）。
    /// </summary>
    private void TraceConvergentSegments(SimulationGrid grid, SimulationState state, PlateSet plates)
    {
        Array.Fill(_segmentId, -1, 0, grid.CellCount);
        Array.Clear(_convergenceWeight, 0, grid.CellCount);
        Array.Clear(_segmentMeanConvergence, 0, grid.CellCount);

        var spacingKm = grid.AverageCellSpacingMeters / 1000.0;
        var segments = 0;
        var longestKm = 0.0;
        var weightSum = 0.0;
        var convergentCells = 0;

        for (var seed = 0; seed < grid.CellCount; seed++)
        {
            if (state.Boundary[seed] != BoundaryType.Convergent) continue;
            if (_segmentId[seed] >= 0) continue;

            var plateA = plates.OwnerByCell[seed];
            var plateB = _boundaryOtherPlate[seed];

            // 這一段的參考速率。速率差太多的鄰居會被排除，於是線在速率變化處自然斷開。
            var seedRate = _convergenceRate[seed];
            var rateTolerance = Math.Abs(seedRate) * SegmentRateCoherence;

            // 1. 泛洪找出整條線段。走訪順序由格子索引決定，所以結果與執行緒排程無關。
            var count = 0;
            _floodQueue[count++] = seed;
            _segmentId[seed] = segments;

            for (var head = 0; head < count; head++)
            {
                var cell = _floodQueue[head];
                var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
                for (var n = 0; n < SimulationGrid.MaxNeighborsPerCell; n++)
                {
                    var neighbor = grid.Neighbors[baseIndex + n];
                    if (neighbor == SimulationGrid.NoNeighbor) continue;
                    if (_segmentId[neighbor] >= 0) continue;
                    if (state.Boundary[neighbor] != BoundaryType.Convergent) continue;

                    // 同一條線段 ＝ 分隔同樣的兩個板塊（兩側各記一邊，所以順序無關）
                    var theirPlate = plates.OwnerByCell[neighbor];
                    var theirOther = _boundaryOtherPlate[neighbor];
                    var sameEdge = (theirPlate == plateA && theirOther == plateB)
                        || (theirPlate == plateB && theirOther == plateA);
                    if (!sameEdge) continue;

                    // 速率差太多就不是同一段（見 SegmentRateCoherence）
                    if (Math.Abs(_convergenceRate[neighbor] - seedRate) > rateTolerance) continue;

                    _segmentId[neighbor] = segments;
                    _floodQueue[count++] = neighbor;
                }
            }

            // 2. 從端點往內泛洪，量每一格離端點幾格。
            //    端點 ＝ 同段鄰居少於2個的格子（線的盡頭，三重接合點旁邊那一格就是）。
            var ends = 0;
            for (var i = 0; i < count; i++)
            {
                var cell = _floodQueue[i];
                var sameSegmentNeighbors = 0;
                var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
                for (var n = 0; n < SimulationGrid.MaxNeighborsPerCell; n++)
                {
                    var neighbor = grid.Neighbors[baseIndex + n];
                    if (neighbor != SimulationGrid.NoNeighbor && _segmentId[neighbor] == segments)
                    {
                        sameSegmentNeighbors++;
                    }
                }

                // 封閉的環沒有端點，那時全部標成「離端點無限遠」＝不衰減
                var isEnd = sameSegmentNeighbors < 2;
                _hopsToSegmentEnd[cell] = isEnd ? 0 : int.MaxValue;
                if (isEnd) _endDistanceQueue[ends++] = cell;
            }

            // **用廣度優先佇列，不要用「反覆鬆弛到不再變動」**（2026-09-07 踩過）：
            // 鬆弛法每一輪都要重掃整段的成員，而距離每輪只前進一格，
            // 一條 n 格的長鏈要 n/2 輪 × n 格 ＝ O(n²)。實測那是這一段的主要成本。
            // 佇列版每一格只進出一次，是 O(n)。
            for (var head = 0; head < ends; head++)
            {
                var cell = _endDistanceQueue[head];
                var hops = _hopsToSegmentEnd[cell];

                var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
                for (var n = 0; n < SimulationGrid.MaxNeighborsPerCell; n++)
                {
                    var neighbor = grid.Neighbors[baseIndex + n];
                    if (neighbor == SimulationGrid.NoNeighbor) continue;
                    if (_segmentId[neighbor] != segments) continue;
                    if (_hopsToSegmentEnd[neighbor] != int.MaxValue) continue;
                    _hopsToSegmentEnd[neighbor] = hops + 1;
                    _endDistanceQueue[ends++] = neighbor;
                }
            }

            // 3. 線段長度：一條 n 格的鏈，中點離端點 (n−1)/2 格，所以 2×最遠+1 就是 n。
            //    環（沒有端點）直接用格數。寬度超過一格的團塊會被低估，那是保守的方向。
            var farthest = 0;
            var convergenceSum = 0.0;
            for (var i = 0; i < count; i++)
            {
                var cell = _floodQueue[i];
                convergenceSum += _convergenceRate[cell];
                if (_hopsToSegmentEnd[cell] != int.MaxValue && _hopsToSegmentEnd[cell] > farthest)
                {
                    farthest = _hopsToSegmentEnd[cell];
                }
            }

            var lengthCells = ends > 0 ? 2 * farthest + 1 : count;
            var lengthKm = lengthCells * spacingKm;
            var lengthWeight = Math.Min(1.0, lengthKm / FullOrogenyLengthKm);
            var meanConvergence = convergenceSum / count;

            for (var i = 0; i < count; i++)
            {
                var cell = _floodQueue[i];
                var hops = _hopsToSegmentEnd[cell];
                var taper = hops == int.MaxValue
                    ? 1.0
                    : Math.Min(1.0, hops * spacingKm / SegmentTipTaperKm);

                _convergenceWeight[cell] = (float)(lengthWeight * taper);
                _segmentMeanConvergence[cell] = meanConvergence;
                weightSum += lengthWeight * taper;
                convergentCells++;
            }

            if (lengthKm > longestKm) longestKm = lengthKm;
            segments++;
        }

        LastSegmentCount = segments;
        LastLongestSegmentKm = longestKm;
        LastMeanConvergenceWeight = convergentCells > 0 ? weightSum / convergentCells : 0;
    }

    /// <summary>
    /// 聚合邊界的造山：上盤增厚。
    ///
    /// 誰是上盤由密度決定（跟 <see cref="SubductionBuoyancyOf"/> 同一套判準）：
    /// 海洋地殼隱沒到大陸底下，上方的大陸因為岩漿上升與增生楔而增厚（安地斯山型）；
    /// 兩塊海洋地殼相遇則是年輕的一方長出島弧。**島弧不斷增厚超過門檻後會轉成大陸地殼**——
    /// 這就是一顆全是海洋的星球長出第一塊陸地的機制。
    /// </summary>
    /// <summary>
    /// 造山帶的寬度（公里）——線後方有多寬的區域會跟著抬升。
    ///
    /// **變形不會只發生在邊界那一格上。** 真實的造山帶是寬帶：安地斯連同阿爾蒂普拉諾高原
    /// 約500公里寬、西藏高原約1,000公里、日本島弧約200公里。而 n=150 一格只有15公里——
    /// 原本把整份增厚都堆在邊界那一格，蓋出來的是一道15公里寬的**牆**，不是造山**帶**，
    /// 這正是大陸在地殼類型圖上看起來像細條的直接原因。
    ///
    /// 取400公里，安地斯與日本之間。
    ///
    /// **總量不變、只改分布**：聚合供應的物質量由縮短速率決定，是固定的；
    /// 帶寬只決定它攤在多寬的範圍上。所以這裡是把同一份預算沿著帶重新分配，
    /// 不是憑空多造地殼——否則量到的差異會分不清是形狀變了還是總量變了。
    /// </summary>
    private const double OrogenWidthKm = 400;

    /// <summary>診斷用：這一步有多少格落在造山帶內（含邊界那一格）。</summary>
    public int LastOrogenCellCount { get; private set; }
}
