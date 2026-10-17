namespace Planet.Simulation;

/// <summary>
/// 板塊動力學：由力矩平衡解出每個板塊的歐拉極與角速度。
///
/// ## 為什麼需要這個
///
/// 在這之前，板塊的歐拉極與角速度是生成時抽的隨機常數、**永遠不變**。
/// 那讓整套系統有運動學而沒有動力學，後果是連鎖的：
///
/// - 板片全部隱沒完的板塊，仍然以原速度前進
/// - 板塊永遠不會加速、減速、改變方向——真實地球每約5,000萬年就有一次板塊重組
/// - 大陸聚合之後不會因為缺少拉力而減速，所以**超大陸旋迴不可能自己出現**
///
/// ## 三個力
///
/// 現代板塊構造學認為驅動力主要來自重力位能，而不是「地函對流推著板塊走」：
///
/// - **板片拉力（Slab Pull）**：最主要的一股，一般估計佔一半以上。冷而重的岩石圈沉入地函時，
///   拉著後面整片板塊往隱沒帶走。這裡直接用 <see cref="Isostasy.LithosphereBuoyancyMeters"/>
///   算出的**負浮力**當強度——沒有岩石圈地函那一層就算不出這個量，
///   這也是為什麼要先做那一項。
/// - **洋脊推力（Ridge Push）**：中洋脊地形較高，板塊沿著傾斜的岩石圈底面往兩側滑開。
///   名字容易誤導，它其實是重力滑坡，量級比板片拉力小一個等級。
/// - **底部拖曳（Basal Drag）**：與速度成正比的阻力。**它是必要的**——
///   沒有它板塊會被持續的力無限加速，永遠達不到終端速度。
///
/// ## 力矩平衡
///
/// 剛體繞歐拉極旋轉，所以要平衡的是**力矩**而不是力：
///
/// - 驅動力矩 <c>T = Σ r × F</c>（沿著邊界累加）
/// - 拖曳力矩 <c>= C · M · ω</c>，其中 <c>M = Σ 面積 × (I − r rᵀ)</c>
///   （對整個板塊累加；這是球面上剛體旋轉的「轉動慣量」對應物）
///
/// 平衡時 <c>ω = M⁻¹ T / C</c>。解出來的是**角速度向量**：
/// 方向就是新的歐拉極、大小就是新的角速度。
///
/// **這是「讓歐拉極也能改變」的關鍵**。只縮放角速度的話，板塊永遠繞著同一根軸轉，
/// 板塊重組不可能發生。
/// </summary>
public sealed class PlateDynamics
{
    /// <summary>
    /// 洋脊推力相對於板片拉力的強度。
    ///
    /// 觀測上洋脊推力比板片拉力小一個量級左右，取0.15。
    /// 這個比例決定了「沒有隱沒帶的板塊」還能跑多快——太大的話板片拉力的主導性就看不出來，
    /// 太小則沒有隱沒帶的板塊會幾乎完全停住。
    /// </summary>
    private const double RidgePushRelativeToSlabPull = 0.15;

    /// <summary>
    /// 速度尺度（度／百萬年，對應單位驅動力矩密度）。
    ///
    /// **這是這個模型唯一的自由參數**，物理上對應地函黏滯度——那是真實地球上也量不準、
    /// 各家地動力學模型都要校準的量。這裡校準的目標是全球平均板塊速度落在
    /// 這顆星球的合理範圍（約1~8公分／年，見 <c>PlateSet.CreateRandom</c> 的說明）。
    ///
    /// **校準它的時候不要順便改別的東西**：板片拉力與洋脊推力的比例、
    /// 岩石圈的三個常數都各自有獨立的物理來源，這裡只該調速度的整體尺度。
    /// </summary>
    private const double VelocityScale = 1500.0;

    /// <summary>
    /// 速度尺度的倍率。標記法是 1（<see cref="VelocityScale"/> 是在 v1 的 Voronoi 邊界上校準的）；
    /// 板塊座標系的邊界是地殼的實際邊緣，驅動力的總量不同，要另外校準（見 <see cref="PlateTectonics"/> 的建構子）。
    /// </summary>
    internal double VelocityScaleFactor { get; set; } = 1.0;

    /// <summary>
    /// 是否套用 <see cref="MaxPlateSpeedMetersPerYear"/>。只有板塊座標系開（見 <see cref="PlateTectonics"/> 的建構子）：
    /// 那裡板塊就是它的地殼，地殼快被吃光的小板塊拖曳趨近 0 才需要它。標記法維持 v1 的行為不變——
    /// 無條件套用的話 v1 的演化會被改掉（P4 實測：SmallIslandCleanupTests 的最大陸塊從 484 變成 486 格）。
    /// </summary>
    internal bool SpeedCapEnabled { get; set; }

    /// <summary>
    /// 板塊最快能跑多快（公尺／年，離歐拉極 90° 處）。地球上最快的板塊約 10~15 公分／年，取 12。
    /// 不是用來校準的旋鈕，是防止小板塊失控的上限——正常的板塊遠低於它。
    /// </summary>
    internal const double MaxPlateSpeedMetersPerYear = 0.12;

    /// <summary>
    /// 角速度往力矩平衡解鬆弛的時間尺度（百萬年）。
    ///
    /// 板塊構造是準靜態的（慣性可以忽略），所以物理上力一變速度就該跟著變。
    /// 但邊界的分類逐步之間會有離散跳動，直接套用會讓速度抖動、
    /// 進而在標記搬運上製造雜訊。取10百萬年——與真實板塊重組的時間尺度同量級，
    /// 足以濾掉逐步的抖動，又不會慢到跟不上構造的變化。
    /// </summary>
    private const double VelocityRelaxationMyr = 10.0;

    /// <summary>
    /// 診斷用：每個板塊來自板片拉力的**力矩**大小。
    ///
    /// **要看力矩不是力的總和**：決定速度的是 <c>Σ r × F</c> 的向量和。
    /// 一塊四周都是隱沒帶的板塊，板片拉力的純量總和很大，但方向互相抵消、淨力矩接近零——
    /// 而那在物理上是對的（被海溝環繞的板塊不會跑，只會被吃掉）。
    /// 2026-09-08 一度用純量總和來檢驗「有板片的板塊比較快」，量出來完全沒有訊號。
    /// </summary>
    public double[] SlabTorqueByPlate { get; private set; } = [];

    /// <summary>診斷用：每個板塊來自洋脊推力的力矩大小。</summary>
    public double[] RidgeTorqueByPlate { get; private set; } = [];

    private double[] _slabTorque = [];
    private double[] _ridgeTorque = [];
    private double[] _torque = [];   // 每個板塊 3 個分量
    private double[] _inertia = [];  // 每個板塊 6 個分量（對稱矩陣的上三角）

    /// <summary>
    /// 依當下的邊界配置重新解出每個板塊的歐拉極與角速度。
    /// </summary>
    /// <param name="grid">網格。</param>
    /// <param name="state">模擬狀態（要用到地殼型別、厚度、密度、年齡與邊界分類）。</param>
    /// <param name="plates">板塊配置，歐拉極與角速度會被更新。</param>
    /// <param name="convergingNeighbor">每個聚合格主要在跟哪一格聚合，−1 表示沒有。</param>
    /// <param name="deltaMyr">這一步代表多少百萬年。</param>
    public void UpdateVelocities(
        SimulationGrid grid, SimulationState state, PlateSet plates,
        int[] convergingNeighbor, double deltaMyr)
    {
        var plateCount = plates.Plates.Length;
        EnsureCapacity(plateCount);

        Array.Clear(_torque);
        Array.Clear(_inertia);
        Array.Clear(_slabTorque);
        Array.Clear(_ridgeTorque);

        AccumulateDragInertia(grid, plates, plateCount);
        AccumulateDrivingTorque(grid, state, plates, convergingNeighbor);

        var relax = Math.Min(1.0, deltaMyr / VelocityRelaxationMyr);
        var solved = new (double X, double Y, double Z)[plateCount];

        for (var p = 0; p < plateCount; p++)
        {
            var target = SolveSymmetric3x3(
                _inertia[p * 6], _inertia[p * 6 + 1], _inertia[p * 6 + 2],
                _inertia[p * 6 + 3], _inertia[p * 6 + 4], _inertia[p * 6 + 5],
                _torque[p * 3], _torque[p * 3 + 1], _torque[p * 3 + 2]);

            var velocityScale = VelocityScale * VelocityScaleFactor;
            target = (target.X * velocityScale, target.Y * velocityScale, target.Z * velocityScale);

            // 速度上限：離歐拉極 90° 處的地表速度不超過 MaxPlateSpeedMetersPerYear。
            // 拖曳正比於板塊面積，一個快消失的小板塊拖曳趨近 0，同樣的驅動力就把它轉到失控
            // （P4 實測 n=24 一個小板塊從 1.3 跳到 18.8°/Myr，其他板塊都在 1 左右）。
            var targetMagnitude = Math.Sqrt(target.X * target.X + target.Y * target.Y + target.Z * target.Z);
            var maxDegPerMyr = MaxPlateSpeedMetersPerYear / grid.PlanetRadiusMeters * 1_000_000 * 180 / Math.PI;
            if (SpeedCapEnabled && targetMagnitude > maxDegPerMyr)
            {
                var capScale = maxDegPerMyr / targetMagnitude;
                target = (target.X * capScale, target.Y * capScale, target.Z * capScale);
            }

            // 從現在的歐拉極與角速度還原出目前的角速度向量，再往目標鬆弛
            var plate = plates.Plates[p];
            var current = (
                X: plate.PoleX * plate.AngularVelocityDegPerMyr,
                Y: plate.PoleY * plate.AngularVelocityDegPerMyr,
                Z: plate.PoleZ * plate.AngularVelocityDegPerMyr);

            solved[p] = (
                current.X + (target.X - current.X) * relax,
                current.Y + (target.Y - current.Y) * relax,
                current.Z + (target.Z - current.Z) * relax);
        }

        plates.ApplyAngularVelocities(solved);
    }

    private void EnsureCapacity(int plateCount)
    {
        if (_torque.Length == plateCount * 3) return;
        _torque = new double[plateCount * 3];
        _inertia = new double[plateCount * 6];
        _slabTorque = new double[plateCount * 3];
        _ridgeTorque = new double[plateCount * 3];
        SlabTorqueByPlate = new double[plateCount];
        RidgeTorqueByPlate = new double[plateCount];
    }

    /// <summary>
    /// 拖曳的「轉動慣量」<c>M = Σ 面積 × (I − r rᵀ)</c>，對板塊的每一格累加。
    ///
    /// 由來：剛體以角速度 ω 旋轉時，位於 r 的速度是 ω × r；拖曳力與速度反向；
    /// 它產生的力矩是 r × (−C(ω × r)) = −C(ω − r(r·ω))，對面積積分就得到 M。
    /// </summary>
    private void AccumulateDragInertia(SimulationGrid grid, PlateSet plates, int plateCount)
    {
        var area = grid.AverageCellAreaSquareMeters;
        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            // **拖曳跟驅動力用同一個範圍：板塊的地殼（OwnerByCell）。**板塊座標系裡板塊就是它的地殼
            // （見 PlateFrameCrust 的說明），種子不再代表任何東西。一個地殼快被吃光的小板塊拖曳趨近 0，
            // 會被同樣的驅動力轉到失控——由速度上限 MaxPlateSpeedMetersPerYear 擋住。
            // 標記法時兩個陣列是同一個，這裡沒有差別。
            var p = plates.OwnerByCell[cell];
            if ((uint)p >= (uint)plateCount) continue;

            var x = grid.UnitVector[cell * 3];
            var y = grid.UnitVector[cell * 3 + 1];
            var z = grid.UnitVector[cell * 3 + 2];

            var b = p * 6;
            _inertia[b] += area * (1 - x * x);      // xx
            _inertia[b + 1] += area * (1 - y * y);  // yy
            _inertia[b + 2] += area * (1 - z * z);  // zz
            _inertia[b + 3] += area * (-x * y);     // xy
            _inertia[b + 4] += area * (-x * z);     // xz
            _inertia[b + 5] += area * (-y * z);     // yz
        }
    }

    /// <summary>沿著邊界累加板片拉力與洋脊推力的力矩。</summary>
    private void AccumulateDrivingTorque(
        SimulationGrid grid, SimulationState state, PlateSet plates, int[] convergingNeighbor)
    {
        var width = grid.AverageCellSpacingMeters;

        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            var boundary = state.Boundary[cell];
            if (boundary is not (BoundaryType.Convergent or BoundaryType.Divergent)) continue;

            var neighbor = boundary == BoundaryType.Convergent
                ? convergingNeighbor[cell]
                : FindNeighborOnOtherPlate(grid, plates, cell);
            if (neighbor < 0) continue;

            var direction = TangentTowards(grid, cell, neighbor);
            if (direction is null) continue; // 這一格算不出方向就跳過這一格，不是放棄整趟累加
            var (dx, dy, dz) = direction.Value;

            var plate = plates.OwnerByCell[cell];
            double magnitude;

            if (boundary == BoundaryType.Convergent)
            {
                // **只有正在隱沒的那一側被拉**：拉力來自沉下去的板片，
                // 上盤不會被自己的隱沒帶拉走。誰是下盤用相對浮力判斷，
                // 跟 PlateTectonics 的碰撞處理一致。
                if (Buoyancy(state, cell) >= Buoyancy(state, neighbor)) continue;

                // **強度用岩石圈地函的熱虧損，不是地表的淨浮力**（2026-09-08 量測後改）。
                //
                // 這兩件事不一樣：
                // - **隱沒能不能開始**：看地表的淨浮力。年輕洋殼的地殼夠輕，整根柱子浮得住，
                //   不會自己沉下去（見 Isostasy.LithosphereBuoyancyMeters 的18百萬年轉折）。
                // - **已經在沉的板片能拉多少**：看它在深處比周圍地函冷多少。板片下去之後，
                //   玄武岩質地殼會轉變成榴輝岩（比地函重），原本抵抗下沉的正浮力就消失了；
                //   真正在拉的是**冷的岩石圈地函**，那一項只跟厚度有關、永遠是正的。
                //
                // 原本用地表淨浮力當強度，等於要求「板片自己浮不住」才算數——
                // 實測聚合格裡符合的只有 **7%**（3,524 格裡 230 格），
                // 於是板片拉力的總和比洋脊推力還**弱5倍**（0.2 比 1），
                // 跟「板片拉力佔驅動力一半以上」完全相反。
                var thermalDeficit = Math.Min(
                    Isostasy.LithosphereThicknessPerSqrtMyrKm * Math.Sqrt(Math.Max(state.CrustAge[cell], 0f)),
                    Isostasy.MaxLithosphereThicknessKm)
                    * 1000.0 * Isostasy.LithosphereMantleDensityExcess / Isostasy.MantleDensity;

                magnitude = thermalDeficit * width;
            }
            else
            {
                // 洋脊推力把板塊往**遠離**邊界的方向推，所以方向要反過來
                dx = -dx; dy = -dy; dz = -dz;
                magnitude = RidgePushRelativeToSlabPull * width * 1000.0;
            }

            var fx = dx * magnitude;
            var fy = dy * magnitude;
            var fz = dz * magnitude;

            var rx = grid.UnitVector[cell * 3];
            var ry = grid.UnitVector[cell * 3 + 1];
            var rz = grid.UnitVector[cell * 3 + 2];

            var tx = ry * fz - rz * fy;
            var ty = rz * fx - rx * fz;
            var tz = rx * fy - ry * fx;

            var b = plate * 3;
            _torque[b] += tx;
            _torque[b + 1] += ty;
            _torque[b + 2] += tz;

            var split = boundary == BoundaryType.Convergent ? _slabTorque : _ridgeTorque;
            split[b] += tx;
            split[b + 1] += ty;
            split[b + 2] += tz;
        }

        for (var p = 0; p < SlabTorqueByPlate.Length; p++)
        {
            SlabTorqueByPlate[p] = Magnitude(_slabTorque, p);
            RidgeTorqueByPlate[p] = Magnitude(_ridgeTorque, p);
        }
    }

    private static double Magnitude(double[] v, int plate)
    {
        var b = plate * 3;
        return Math.Sqrt(v[b] * v[b] + v[b + 1] * v[b + 1] + v[b + 2] * v[b + 2]);
    }

    private static float Buoyancy(SimulationState state, int cell) => Isostasy.LithosphereBuoyancyMeters(
        state.Crust[cell], state.CrustThickness[cell], state.CrustDensity[cell], state.CrustAge[cell]);

    private static int FindNeighborOnOtherPlate(SimulationGrid grid, PlateSet plates, int cell)
    {
        var mine = plates.OwnerByCell[cell];
        var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
        for (var n = 0; n < SimulationGrid.MaxNeighborsPerCell; n++)
        {
            var neighbor = grid.Neighbors[baseIndex + n];
            if (neighbor == SimulationGrid.NoNeighbor) continue;
            if (plates.OwnerByCell[neighbor] != mine) return neighbor;
        }
        return -1;
    }

    /// <summary>從 cell 指向 neighbour 的切平面單位向量（把徑向分量去掉）。</summary>
    private static (double X, double Y, double Z)? TangentTowards(SimulationGrid grid, int cell, int neighbor)
    {
        var rx = grid.UnitVector[cell * 3];
        var ry = grid.UnitVector[cell * 3 + 1];
        var rz = grid.UnitVector[cell * 3 + 2];

        var dx = grid.UnitVector[neighbor * 3] - rx;
        var dy = grid.UnitVector[neighbor * 3 + 1] - ry;
        var dz = grid.UnitVector[neighbor * 3 + 2] - rz;

        var radial = dx * rx + dy * ry + dz * rz;
        dx -= radial * rx;
        dy -= radial * ry;
        dz -= radial * rz;

        var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (length < 1e-12) return null;
        return (dx / length, dy / length, dz / length);
    }

    /// <summary>
    /// 解 3×3 對稱正定方程 <c>M ω = T</c>（用餘因子除以行列式）。
    ///
    /// M 是拖曳的轉動慣量，一定是正定的（每一格都貢獻一個半正定項，而板塊有面積），
    /// 但格數極少的板塊可能接近奇異，所以行列式太小時回傳零向量——
    /// 那代表這個板塊小到力矩平衡沒有意義，讓它停住比解出一個爆炸的角速度好。
    /// </summary>
    private static (double X, double Y, double Z) SolveSymmetric3x3(
        double xx, double yy, double zz, double xy, double xz, double yz,
        double tx, double ty, double tz)
    {
        var c00 = yy * zz - yz * yz;
        var c01 = xz * yz - xy * zz;
        var c02 = xy * yz - xz * yy;
        var determinant = xx * c00 + xy * c01 + xz * c02;

        // **一格都不剩的板塊要先擋掉**（2026-09-09）。慣量全是 0 時 scale 也是 0，
        // 下面那個門檻就變成 `0 < 0`——**是 false**，所以不但沒擋住，還一路往下
        // 除以 determinant = 0，解出 NaN。只有一格的板塊反而擋得住
        // （單格的 M 是奇異的，det = 0 真的小於 8e-9·a³），零格的剛好從縫裡漏掉。
        //
        // 後果是全域的，不是這一個板塊的事：NaN 角速度寫進歐拉極之後，
        // PlateSet.RemoveNetRotation 會把它平均給**每一個**板塊，於是 15 個板塊
        // 全部變 NaN、種子全部變 NaN，Voronoi 的距離比較全落到 false、
        // 所有格子塌進 0 號板塊，最後 SolveSeaLevel 在 NaN 地形上卡成無窮迴圈。
        // 實測（n=24、種子42）：最小的板塊從17格一路縮到0，歸零的那一步
        // 15個板塊同時變 NaN，而其他14個當下都還有幾百格。
        var scale = Math.Abs(xx) + Math.Abs(yy) + Math.Abs(zz);
        if (!(scale > 0)) return (0, 0, 0);
        if (Math.Abs(determinant) < 1e-9 * scale * scale * scale) return (0, 0, 0);

        var c11 = xx * zz - xz * xz;
        var c12 = xy * xz - xx * yz;
        var c22 = xx * yy - xy * xy;

        return (
            (c00 * tx + c01 * ty + c02 * tz) / determinant,
            (c01 * tx + c11 * ty + c12 * tz) / determinant,
            (c02 * tx + c12 * ty + c22 * tz) / determinant);
    }
}
