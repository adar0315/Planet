namespace Planet.Simulation;

/// <summary>
/// 一個板塊的剛體運動。
///
/// 球面上任何剛體運動都可以表示成「繞著某條通過球心的軸旋轉」，那條軸與球面的交點就是
/// **歐拉極（Euler pole）**——這是真實板塊構造學描述板塊運動的標準做法，不是簡化近似。
/// 板塊上任一點的速度 v = ω × r，離歐拉極90度的地方最快、極點本身不動。
/// </summary>
/// <param name="Id">板塊編號。</param>
/// <param name="PoleX">歐拉極單位向量X。</param>
/// <param name="PoleY">歐拉極單位向量Y。</param>
/// <param name="PoleZ">歐拉極單位向量Z。</param>
/// <param name="AngularVelocityDegPerMyr">角速度（度／百萬年），正值為右手定則方向。</param>
/// <param name="DensityAnomalyKgPerM3">
/// 這個板塊的地殼密度相對典型海洋地殼（<see cref="Isostasy.OceanicCrustDensity"/>）差多少。
/// 正值＝比周圍重，見 <see cref="PlateSet.DensePlateCount"/>。
/// </param>
/// <param name="IsOceanicOnly">
/// 這個板塊上的地殼永遠不會轉成大陸地殼。
///
/// **跟密度異常是兩回事**（2026-09-08 解耦）：原本這件事是從「密度異常 &gt; 0」推導出來的，
/// 但太平洋板塊並不特別重，它只是**沒有大陸地殼**。
/// 泛大洋遺存型的板塊（見 <see cref="PlateSet.SuperoceanPlateId"/>）需要後者而不需要前者。
/// </param>
public readonly record struct Plate(
    int Id,
    double PoleX,
    double PoleY,
    double PoleZ,
    double AngularVelocityDegPerMyr,
    float DensityAnomalyKgPerM3 = 0f,
    bool IsOceanicOnly = false);

/// <summary>
/// 整顆星球的板塊配置：每個板塊的歐拉極與角速度，以及「每一格屬於哪個板塊」。
/// </summary>
public sealed class PlateSet
{
    /// <summary>
    /// 預設板塊數。地球目前是7大板塊＋約8個小板塊，取15是同一個量級。
    /// 太少會讓每個板塊大到看不出邊界作用，太多則每個板塊都很小、大陸長不起來。
    /// </summary>
    public const int DefaultPlateCount = 15;

    /// <summary>
    /// 有幾個板塊一開始就比周圍的海洋板塊**重**。
    ///
    /// 重的板塊在均衡上沉得更低（densityLift 是負的），所以形成更深的海盆——
    /// **陸地不是來自這些板塊自己浮起來，而是來自堆在它們上面的火山**：
    /// 負浮力讓板塊持續承受拉張與剪切應力，斷層帶因此裂開，岩漿沿裂縫上湧堆成火山島
    /// （見 <see cref="FissureVolcanism"/>）。地球上的夏威夷就是坐落在又老又重的太平洋板塊上。
    /// </summary>
    public const int DensePlateCount = 2;

    /// <summary>
    /// 重板塊比典型海洋地殼重多少（公斤／立方公尺）。
    ///
    /// 取250：以7公里厚的地殼計算，Airy 均衡的下沉量是 7000×250/3300 ≈ 530公尺。
    /// 原本取150（約320公尺），但海洋深度本來就會因為年齡差出好幾公里
    /// （深度 ∝ √年齡），320公尺根本蓋不過那個變異，看不出「這是比較深的海盆」。
    /// </summary>
    public const float DensePlateAnomalyKgPerM3 = 250f;

    /// <summary>所有板塊，索引即 <see cref="Plate.Id"/>。</summary>
    public Plate[] Plates { get; }

    /// <summary>
    /// 每一格在種子幾何（旋轉中的 Voronoi）下屬於哪個板塊（長度 = 格數）。會隨著板塊移動而改變。
    /// 地質作用要用 <see cref="OwnerByCell"/>——這個只用在運動學（種子跟著板塊轉、拖曳的範圍）。
    /// </summary>
    public int[] PlateIdByCell { get; }

    /// <summary>
    /// 每一格**實際上**屬於哪個板塊——邊界分類、動力學、火山都用這個。
    ///
    /// 標記法時就是 <see cref="PlateIdByCell"/>（同一個陣列）。板塊座標系時是「這一格的地殼是哪個板塊的」
    /// （見 <c>PlateFrameCrust</c>）：Voronoi 的平分線以兩側的平均速度移動，在隱沒帶上會離開上盤真正的邊緣，
    /// 海溝跟著它在上盤上滑動、島弧被抹成一大片薄薄的增厚帶（P4 第一次在 n=150 跑 50 Myr，陸地 7.8% 幾乎都是這種，
    /// 大陸地殼一格都沒長）。改成看地殼在哪，海溝才會停在上盤的邊緣。
    /// </summary>
    public int[] OwnerByCell { get; private set; }

    /// <summary>
    /// 每個板塊有幾個種子點。
    ///
    /// **一個板塊一個種子的話，邊界必然是兩個種子之間那條大圓平分線**——乾淨的直邊，
    /// 三條邊交會在120度的接合點。那個幾何會被整個印到星球上：大陸沿著直邊排成細條、
    /// 接合點是 Y 形團塊（見 docs 的目視驗證）。真實的板塊邊界不是這樣，它是彎的、
    /// 不規則的、被轉形斷層錯開的。
    ///
    /// 改成一個板塊多個種子之後，邊界變成這些子種子之間許多**短而不規則**的段落，
    /// 而板塊本身仍然是剛體（所有子種子繞同一個歐拉極旋轉），
    /// 歸屬仍然是純幾何判定（見 <see cref="Advance"/> 裡那個坑）。
    ///
    /// 這也是之後做威爾遜旋迴的基礎：**要裂解一塊大陸，就是把它的種子集合分成兩組**，
    /// 不需要去追蹤自由的多段線（那會遇到線互相穿越、拉伸後產生缺口讓板塊意外合併、
    /// 聚合時互相吞噬這些前緣追蹤的失效模式）。
    ///
    /// 取6：夠讓邊界明顯不規則，而分配的成本是 15×6=90 次內積／格，仍然只是內積。
    /// </summary>
    public const int SeedsPerPlate = 6;

    // 球面 Voronoi 的種子點，會跟著各自的歐拉極一起旋轉，見 Advance。
    // 長度是 板塊數 × SeedsPerPlate，_seedPlate 記錄每個種子屬於哪個板塊。
    private readonly double[] _seedX;
    private readonly double[] _seedY;
    private readonly double[] _seedZ;
    private readonly int[] _seedPlate;

    /// <summary>
    /// 每個種子的權重，直接加在內積上。權重大的板塊會佔到更大的領域。
    ///
    /// 用來造出一個**泛大洋遺存**級的巨大海洋板塊，見 <see cref="SuperoceanSeedBias"/>。
    /// 權重不隨時間改變，所以增量重新分配的那個界（見 <see cref="Reassign"/>）仍然成立。
    /// </summary>
    private readonly double[] _seedBias;

    /// <summary>
    /// 每一格「最近的種子」贏第二名多少（內積差）。上一次**全掃**時算的。
    /// 見 <see cref="AssignCellsToNearestSeed"/> 裡的說明。
    /// </summary>
    private float[]? _assignMargin;

    /// <summary>上一次全掃之後，種子累積轉了多少角度（弧度）。</summary>
    private double _driftAngleSinceFullScan;

    /// <summary>讓 <see cref="OwnerByCell"/> 變成獨立的陣列（板塊座標系用；起始值等於 Voronoi 的歸屬）。</summary>
    internal void SeparateOwnership()
    {
        if (!ReferenceEquals(OwnerByCell, PlateIdByCell)) return;
        OwnerByCell = (int[])PlateIdByCell.Clone();
    }

    /// <summary>這一次重新分配實際重掃了幾格。診斷與「該不該改回全掃」的判斷用。</summary>
    private int _lastRescanCount;

    private PlateSet(
        Plate[] plates, int[] plateIdByCell,
        double[] seedX, double[] seedY, double[] seedZ, int[] seedPlate, double[] seedBias)
    {
        Plates = plates;
        PlateIdByCell = plateIdByCell;
        OwnerByCell = plateIdByCell;
        _seedX = seedX;
        _seedY = seedY;
        _seedZ = seedZ;
        _seedPlate = seedPlate;
        _seedBias = seedBias;
    }

    /// <summary>
    /// 把板塊配置寫進存檔（見 <see cref="SimulationSnapshot"/>）。
    ///
    /// 板塊**不能從種子重新生成**：歐拉極與角速度每步都被 <see cref="PlateDynamics"/>
    /// 依力矩平衡改寫，種子點也一直在轉。所以要照實存下來。
    ///
    /// 重新分配的累積狀態（種子漂移量、上次全掃的勝負差與之後的漂移角）也要存。
    /// 版本 1 以為那些是「歸零也無妨的快取」，結果讀檔續跑第 2 步就分岔，見 <see cref="SimulationSnapshot"/>。
    /// </summary>
    /// <param name="writer">目標。</param>
    internal void WriteTo(BinaryWriter writer)
    {
        writer.Write(Plates.Length);
        foreach (var plate in Plates)
        {
            writer.Write(plate.Id);
            writer.Write(plate.PoleX);
            writer.Write(plate.PoleY);
            writer.Write(plate.PoleZ);
            writer.Write(plate.AngularVelocityDegPerMyr);
            writer.Write(plate.DensityAnomalyKgPerM3);
            writer.Write(plate.IsOceanicOnly);
        }

        writer.Write(PlateIdByCell.Length);
        SimulationSnapshot.WriteArray(writer, PlateIdByCell);

        writer.Write(_seedX.Length);
        SimulationSnapshot.WriteArray(writer, _seedX);
        SimulationSnapshot.WriteArray(writer, _seedY);
        SimulationSnapshot.WriteArray(writer, _seedZ);
        SimulationSnapshot.WriteArray(writer, _seedPlate);
        SimulationSnapshot.WriteArray(writer, _seedBias);

        // 這三個是「什麼時候重新分配、怎麼分配」的累積狀態，跟板塊本身一樣是狀態。
        // _seedDriftMeters 漏存的症狀是讀檔續跑**第 1 步完全相同、第 2 步分岔**：
        // 板塊歸屬只在種子累積漂移超過 0.25 格距時才重算，歸零的那一顆會晚幾步才重算（2026-10-16）。
        writer.Write(_seedDriftMeters);
        writer.Write(_driftAngleSinceFullScan);
        writer.Write(_assignMargin is not null);
        if (_assignMargin is not null) SimulationSnapshot.WriteArray(writer, _assignMargin);
    }

    /// <summary>從存檔讀回一組板塊配置。順序見 <see cref="WriteTo"/>。</summary>
    /// <param name="reader">來源。</param>
    /// <returns>板塊配置。</returns>
    internal static PlateSet ReadFrom(BinaryReader reader)
    {
        var plateCount = reader.ReadInt32();
        var plates = new Plate[plateCount];
        for (var i = 0; i < plateCount; i++)
        {
            plates[i] = new Plate(
                reader.ReadInt32(),
                reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(),
                reader.ReadDouble(),
                reader.ReadSingle(),
                reader.ReadBoolean());
        }

        var cellCount = reader.ReadInt32();
        var plateIdByCell = new int[cellCount];
        SimulationSnapshot.ReadArray(reader, plateIdByCell);

        var seedCount = reader.ReadInt32();
        var seedX = new double[seedCount];
        var seedY = new double[seedCount];
        var seedZ = new double[seedCount];
        var seedPlate = new int[seedCount];
        var seedBias = new double[seedCount];
        SimulationSnapshot.ReadArray(reader, seedX);
        SimulationSnapshot.ReadArray(reader, seedY);
        SimulationSnapshot.ReadArray(reader, seedZ);
        SimulationSnapshot.ReadArray(reader, seedPlate);
        SimulationSnapshot.ReadArray(reader, seedBias);

        var seedDrift = reader.ReadDouble();
        var drift = reader.ReadDouble();
        float[]? margin = null;
        if (reader.ReadBoolean())
        {
            margin = new float[cellCount];
            SimulationSnapshot.ReadArray(reader, margin);
        }

        return new PlateSet(plates, plateIdByCell, seedX, seedY, seedZ, seedPlate, seedBias)
        {
            _assignMargin = margin,
            _driftAngleSinceFullScan = drift,
            _seedDriftMeters = seedDrift,
        };
    }

    /// <summary>
    /// 把板塊往前推進：種子點各自繞自己的歐拉極旋轉，再重新分配每一格屬於哪個板塊。
    ///
    /// **板塊的範圍是幾何決定的，不是逐格搬運出來的**，這裡踩過一次坑（2026-09-05）：
    /// 原本板塊歸屬跟著地殼的標記一起移動，但「最近格」映射對非均勻的 Goldberg 網格
    /// 不是完美的一對一，每次取樣都會有少量錯配，累積下來把板塊編號攪成胡椒鹽——
    /// **94%的格子都被判定成板塊邊界**，於是整個地表不斷被當成張裂處重置成新生地殼，
    /// 平均地殼厚度一路下滑、撞擊留下的不均勻也被洗掉，而且完全沒有錯誤訊息。
    ///
    /// 板塊是剛體，讓種子點旋轉就能得到平滑、無雜訊、而且會隨相對運動自然增減的邊界。
    /// </summary>
    /// <param name="grid">網格。</param>
    /// <param name="deltaMyr">要推進多少百萬年。</param>
    public void Advance(SimulationGrid grid, double deltaMyr)
    {
        var maxAngleRad = 0.0;
        for (var i = 0; i < _seedX.Length; i++)
        {
            // 同一個板塊的所有子種子繞**同一個**歐拉極、轉**同一個**角度——板塊仍然是剛體
            var plate = Plates[_seedPlate[i]];
            var angle = plate.AngularVelocityDegPerMyr * Math.PI / 180.0 * deltaMyr;
            var (x, y, z) = RotateAboutAxis(
                _seedX[i], _seedY[i], _seedZ[i],
                plate.PoleX, plate.PoleY, plate.PoleZ,
                angle);

            // 每步重新正規化：幾萬步累積下來的浮點誤差會讓種子點慢慢離開單位球面
            var length = Math.Sqrt(x * x + y * y + z * z);
            _seedX[i] = x / length;
            _seedY[i] = y / length;
            _seedZ[i] = z / length;

            maxAngleRad = Math.Max(maxAngleRad, Math.Abs(angle));
        }

        _driftAngleSinceFullScan += maxAngleRad;

        // **旋轉每步都做，重新分配歸屬只在種子真的移動夠遠時才做。**
        // 重新分配是 15個板塊 × 22.5萬格的內積掃描，一步5萬年種子只移動不到1公里、
        // 結果根本不會變，每步都做的話光這件事就佔掉大半的運算時間。
        // 種子位置一直是精確的連續值，所以延後分配不會累積任何誤差。
        _seedDriftMeters += maxAngleRad * grid.PlanetRadiusMeters;
        var threshold = ReassignThresholdInCellSpacings * EstimateCellSpacingMeters(grid);
        if (_seedDriftMeters < threshold) return;

        Reassign(grid);
        _seedDriftMeters = 0;
    }

    /// <summary>
    /// 種子點漂移超過幾倍的「格子中心間距」才重新分配板塊歸屬。
    /// 取0.25：遠小於一格，所以邊界的移動看起來仍然是連續的，但掃描次數少了一個數量級。
    /// </summary>
    private const double ReassignThresholdInCellSpacings = 0.25;

    private double _seedDriftMeters;

    /// <summary>相鄰格子的中心間距（公尺）：由平均面積當正六邊形反推邊長，再乘√3。</summary>
    private static double EstimateCellSpacingMeters(SimulationGrid grid) =>
        Math.Sqrt(grid.AverageCellAreaSquareMeters / (3.0 * Math.Sqrt(3.0) / 2.0)) * Math.Sqrt(3.0);

    /// <summary>羅德里格旋轉公式：把單位向量繞著另一條軸旋轉指定角度。</summary>
    private static (double X, double Y, double Z) RotateAboutAxis(
        double vx, double vy, double vz,
        double kx, double ky, double kz,
        double angleRad)
    {
        var cos = Math.Cos(angleRad);
        var sin = Math.Sin(angleRad);

        var crossX = ky * vz - kz * vy;
        var crossY = kz * vx - kx * vz;
        var crossZ = kx * vy - ky * vx;
        var factor = (kx * vx + ky * vy + kz * vz) * (1 - cos);

        return (
            vx * cos + crossX * sin + kx * factor,
            vy * cos + crossY * sin + ky * factor,
            vz * cos + crossZ * sin + kz * factor);
    }

    /// <summary>
    /// 隨機生成板塊配置：在球面上灑 <paramref name="plateCount"/> 個種子點做球面 Voronoi 分割，
    /// 每個板塊再給一個隨機的歐拉極與角速度。
    ///
    /// **角速度的範圍是依這顆星球的半徑換算的，不是照抄地球的數字。**
    /// 地球板塊速度約 1~10 公分/年，而線速度 = 半徑 × 角速度：
    /// 這顆星球半徑1,852公里（地球的0.29倍），1度/百萬年在赤道約等於 3.2 公分/年，
    /// 所以要得到地球量級的速度，角速度要取 0.3~2.5 度/百萬年。
    /// 直接抄地球的角速度（0.1~0.9）會讓板塊慢上三倍多。
    /// </summary>
    /// <param name="grid">網格。</param>
    /// <param name="seed">亂數種子，同種子會產生同一組板塊。</param>
    /// <param name="plateCount">板塊數量。</param>
    /// <param name="densePlateCount">
    /// 有幾個板塊一開始就比較重（見 <see cref="DensePlateCount"/>）。
    /// 可以指定成0來做對照實驗，確認某個現象是不是重板塊造成的。
    /// </param>
    public static PlateSet CreateRandom(
        SimulationGrid grid, int seed, int plateCount = DefaultPlateCount, int densePlateCount = DensePlateCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(plateCount, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(densePlateCount);

        var rng = new Random(seed);
        var plates = new Plate[plateCount];
        var seedCount = plateCount * SeedsPerPlate;
        var seedX = new double[seedCount];
        var seedY = new double[seedCount];
        var seedZ = new double[seedCount];
        var seedPlate = new int[seedCount];
        var seedBias = new double[seedCount];

        for (var p = 0; p < plateCount; p++)
        {
            var (sx, sy, sz) = RandomUnitVector(rng);

            // 主種子加上散布在它周圍的子種子。第0個就是主種子本身，
            // 這樣板塊中心一定被自己涵蓋，不會整塊被鄰居吃掉。
            for (var k = 0; k < SeedsPerPlate; k++)
            {
                var index = p * SeedsPerPlate + k;
                seedPlate[index] = p;

                // 第0個板塊是泛大洋遺存，領域比其他板塊大得多
                seedBias[index] = p == SuperoceanPlateId ? SuperoceanSeedBias : 0.0;

                if (k == 0)
                {
                    seedX[index] = sx;
                    seedY[index] = sy;
                    seedZ[index] = sz;
                    continue;
                }

                var (ox, oy, oz) = RandomUnitVector(rng);
                var jx = sx + SeedScatter * ox;
                var jy = sy + SeedScatter * oy;
                var jz = sz + SeedScatter * oz;
                var length = Math.Sqrt(jx * jx + jy * jy + jz * jz);
                seedX[index] = jx / length;
                seedY[index] = jy / length;
                seedZ[index] = jz / length;
            }

            var (px, py, pz) = RandomPoleBiasedToSpinAxis(rng);
            // 0.3~2.5 度/百萬年 ＝ 這顆星球上約 1~8 公分/年
            // （動力學接手之後這只是初始值，見 PlateDynamics）
            var omega = 0.3 + rng.NextDouble() * 2.2;

            // 泛大洋遺存：巨大、而且永遠是海洋地殼（太平洋板塊上幾乎沒有大陸）
            plates[p] = new Plate(p, px, py, pz, omega, IsOceanicOnly: p == SuperoceanPlateId);
        }

        AssignDensePlates(plates, rng, densePlateCount);

        var plateIdByCell = new int[grid.CellCount];
        var margin = new float[grid.CellCount];
        AssignCellsToNearestSeed(grid, plateIdByCell, seedX, seedY, seedZ, seedPlate, seedBias, margin);

        RemoveNetRotation(plates, plateIdByCell);
        return new PlateSet(plates, plateIdByCell, seedX, seedY, seedZ, seedPlate, seedBias)
        {
            _assignMargin = margin,
        };
    }

    /// <summary>
    /// 重掃的格數超過總數這個比例時，就改成全掃並重置基準。
    ///
    /// 需要重掃的集合會隨著累積漂移單調變大（見 <see cref="Reassign"/>），
    /// 大到一定程度時全掃反而比較划算，而且能把勝負差的基準重新算準。
    /// </summary>
    private const double FullScanWhenRescanFractionExceeds = 0.5;

    /// <summary>
    /// 重新分配板塊歸屬——只重算**可能翻面**的格子。
    ///
    /// ## 判準是可以證明的界，不是幾何直覺
    ///
    /// 一開始寫成「只重掃邊界附近幾格」，被 <c>PlateAssignmentTests</c> 當場抓到錯
    /// （2026-09-07，25百萬年後13格不一致）。漏掉的情況是：一個子種子可能先被鄰居壓過去、
    /// 完全不擁有任何格子，之後隨著旋轉「浮出來」，於是它的領域**憑空出現在另一個板塊的內部**，
    /// 跟既有邊界完全不相鄰。
    ///
    /// 正確的判準來自內積本身：種子轉了角度 Δ，任一格對它的內積最多變化 Δ；
    /// 最佳與次佳的差最多變化 2Δ。所以**勝負差大於 2×累積漂移的格子不可能翻面**——
    /// 這是保守的上界，涵蓋了上面那種憑空出現的情形，不需要對幾何做任何假設。
    ///
    /// 勝負差是上一次**全掃**時算的，累積漂移也是從那時開始算，所以要重掃的集合
    /// 會隨時間單調變大；超過 <see cref="FullScanWhenRescanFractionExceeds"/> 就全掃、重置基準。
    /// </summary>
    /// <param name="grid">網格。</param>
    private void Reassign(SimulationGrid grid)
    {
        _assignMargin ??= new float[grid.CellCount];

        var threshold = 2.0 * _driftAngleSinceFullScan;
        _lastRescanCount = AssignCellsToNearestSeed(
            grid, PlateIdByCell, _seedX, _seedY, _seedZ, _seedPlate, _seedBias,
            margin: null, skipMarginAbove: threshold, previousMargin: _assignMargin);

        if (_lastRescanCount <= grid.CellCount * FullScanWhenRescanFractionExceeds) return;

        AssignCellsToNearestSeed(grid, PlateIdByCell, _seedX, _seedY, _seedZ, _seedPlate, _seedBias, _assignMargin);
        _driftAngleSinceFullScan = 0;
    }

    /// <summary>診斷用：上一次重新分配實際重掃了幾格（全掃是總格數）。</summary>
    public int LastRescanCount => _lastRescanCount;

    /// <summary>
    /// 守門用：把目前的板塊歸屬跟「完整重掃所有格子」的結果逐格比對，回傳不一致的格數。
    ///
    /// 增量分配（見 <see cref="Reassign"/>）如果推論錯了，症狀是板塊歸屬安靜地算錯、
    /// 沒有任何錯誤訊息，所以必須有測試直接比對。
    ///
    /// **會先強制跑一次增量重新分配**，否則量到的是另一回事：<see cref="Advance"/> 本來就
    /// 只在種子漂移超過 <see cref="ReassignThresholdInCellSpacings"/> 才重新分配，
    /// 所以任何時刻的歸屬相對於「用當下種子位置完整重掃」都會落後幾格——那是刻意的延遲，
    /// 不是增量跳過造成的錯誤。2026-09-07 就是沒有先跑這一步，13格的延遲被誤判成
    /// 增量分配壞掉，白改了一版。
    /// </summary>
    /// <param name="grid">網格。</param>
    /// <returns>與完整重掃不一致的格數。正確時應該是0。</returns>
    public int CountAssignmentMismatchesAgainstFullScan(SimulationGrid grid)
    {
        Reassign(grid);

        var reference = new int[grid.CellCount];
        AssignCellsToNearestSeed(grid, reference, _seedX, _seedY, _seedZ, _seedPlate, _seedBias, null);

        var mismatches = 0;
        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            if (reference[cell] != PlateIdByCell[cell]) mismatches++;
        }
        return mismatches;
    }

    /// <summary>哪一個板塊是泛大洋遺存。</summary>
    public const int SuperoceanPlateId = 0;

    /// <summary>
    /// 泛大洋遺存板塊的種子權重（直接加在內積上，內積本身的範圍是 −1~1）。
    ///
    /// **太平洋不是大陸裂開產生的新海盆，而是泛大洋（Panthalassa）的直接遺存**——
    /// 它從超大陸時代就存在，是被**繼承**下來的，不是後來的作用造出來的
    /// （見 `地球科學/太平洋的成因.md`）。所以在這個模型裡它是**初始條件**，
    /// 而不是某個機制的產物。
    ///
    /// 這也是目前唯一可行的做法：板塊的種子不會生也不會滅，
    /// 所以板塊數與大小分布永遠凍結在初始值，大板塊不可能後天長出來。
    /// 等威爾遜旋迴（種子的分裂與合併）做出來之後，這個初始條件可以退場。
    ///
    /// 校準目標是太平洋佔地球表面的比例（約20%）。15個板塊平均佔6.7%，
    /// 所以這一塊要大約3倍大。
    /// </summary>
    public const double SuperoceanSeedBias = 0.20;

    /// <summary>
    /// 子種子散布在主種子周圍多遠（單位向量相加時的權重，約等於角度的正切）。
    ///
    /// 15個板塊時每塊的平均角半徑約30度。取0.20 → 子種子最遠偏離主種子約11度，
    /// 大約是板塊半徑的三分之一：足以讓邊界明顯不規則，又不至於讓子種子跑進鄰居的領域裡
    /// 把板塊切成互不相連的碎塊（那會讓「板塊是剛體」這個前提失去意義）。
    /// 板塊是否仍然連通，用 <c>PlateFragmentCount</c> 量得出來。
    /// </summary>
    private const double SeedScatter = 0.20;

    /// <summary>
    /// 診斷用：板塊被切成幾塊互不相連的區域。理想是等於板塊數
    /// （每塊板塊自己是連通的）；明顯大於板塊數就代表 <see cref="SeedScatter"/> 太大。
    /// </summary>
    /// <param name="grid">網格。</param>
    public int PlateFragmentCount(SimulationGrid grid)
    {
        var visited = new bool[grid.CellCount];
        var queue = new int[grid.CellCount];
        var fragments = 0;

        for (var start = 0; start < grid.CellCount; start++)
        {
            if (visited[start]) continue;
            fragments++;
            var plate = PlateIdByCell[start];
            var count = 0;
            queue[count++] = start;
            visited[start] = true;

            for (var head = 0; head < count; head++)
            {
                var cell = queue[head];
                var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
                for (var n = 0; n < SimulationGrid.MaxNeighborsPerCell; n++)
                {
                    var neighbor = grid.Neighbors[baseIndex + n];
                    if (neighbor == SimulationGrid.NoNeighbor) continue;
                    if (visited[neighbor] || PlateIdByCell[neighbor] != plate) continue;
                    visited[neighbor] = true;
                    queue[count++] = neighbor;
                }
            }
        }

        return fragments;
    }

    /// <summary>
    /// 隨機挑 <see cref="DensePlateCount"/> 個板塊，讓它們的地殼比周圍的海洋板塊重。
    ///
    /// 用 Fisher–Yates 洗牌取前幾個，而不是「隨機抽直到湊滿」——後者在板塊數少的時候
    /// 可能反覆抽到同一個而空轉，而且抽樣不均勻。
    /// </summary>
    private static void AssignDensePlates(Plate[] plates, Random rng, int densePlateCount)
    {
        if (densePlateCount <= 0) return;

        var order = Enumerable.Range(0, plates.Length).ToArray();
        for (var i = order.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        var count = Math.Min(densePlateCount, plates.Length);
        for (var i = 0; i < count; i++)
        {
            var id = order[i];
            plates[id] = plates[id] with
            {
                DensityAnomalyKgPerM3 = DensePlateAnomalyKgPerM3,
                IsOceanicOnly = true,
            };
        }
    }

    /// <summary>
    /// 把各板塊的密度異常寫進地殼狀態。
    ///
    /// 必須在 <c>PlateTectonics.Reset</c> **之前**呼叫——標記（marker）的密度是從
    /// <see cref="SimulationState.CrustDensity"/> 拷貝過去的，順序反了的話異常永遠不會生效。
    /// </summary>
    /// <param name="state">要套用的模擬狀態。</param>
    public void ApplyDensityAnomaliesTo(SimulationState state)
    {
        for (var cell = 0; cell < state.CellCount; cell++)
        {
            var plate = Plates[PlateIdByCell[cell]];

            if (plate.DensityAnomalyKgPerM3 > 0f) state.CrustDensity[cell] += plate.DensityAnomalyKgPerM3;
            if (!plate.IsOceanicOnly) continue;

            state.OceanicOnly[cell] = true;

            // 早期大轟炸可能已經在這裡放了原始高地（那一步跑在板塊指派之前），
            // 落在「永遠是海洋」的板塊上要整個降回海洋地殼，
            // 否則「這塊板塊沒有大陸」從第0步就不成立。
            //
            // **厚度也要一起重設**（2026-09-08 補）：原本只改型別、不改厚度，
            // 被大轟炸加厚的地殼仍然留在那裡，很快又會越過25公里的門檻轉回大陸。
            // 實測泛大洋板塊上的大陸格從第0步的3,447格一路長到80百萬年的14,128格，
            // 而它本該一格都沒有；那些大陸的浮力還讓它在邊界上常常變成上盤，
            // 跟太平洋「幾乎整圈都在隱沒下去」正好相反。
            state.Crust[cell] = CrustType.Oceanic;
            state.CrustThickness[cell] = Isostasy.OceanicCrustThicknessKm;
        }
    }

    /// <summary>
    /// 球面 Voronoi：每格歸給最接近的種子點。板塊數很少，直接暴力比對即可
    /// （15個板塊 × 22.5萬格 ≈ 340萬次內積，跟最近格搜尋比起來便宜得多）。
    /// </summary>
    /// <summary>
    /// 把每一格指派給最近的種子，並（可選）記下「贏第二名多少」。
    /// </summary>
    /// <param name="grid">網格。</param>
    /// <param name="plateIdByCell">輸出：每格屬於哪個板塊。</param>
    /// <param name="seedX">種子的X座標。</param>
    /// <param name="seedY">種子的Y座標。</param>
    /// <param name="seedZ">種子的Z座標。</param>
    /// <param name="seedPlate">每個種子屬於哪個板塊。</param>
    /// <param name="seedBias">每個種子的權重，直接加在內積上。</param>
    /// <param name="margin">
    /// 輸出：最佳與次佳種子的內積差。null 代表不需要。
    /// 這個差值是增量重新分配的依據，見 <see cref="Reassign"/>。
    /// </param>
    /// <param name="skipMarginAbove">
    /// 勝負差大於這個值的格子直接跳過不重算（配合 <paramref name="margin"/> 使用的舊值）。
    /// 負數代表全部都要算。
    /// </param>
    /// <param name="previousMargin">判斷是否跳過時要看的舊勝負差。</param>
    private static int AssignCellsToNearestSeed(
        SimulationGrid grid, int[] plateIdByCell,
        double[] seedX, double[] seedY, double[] seedZ, int[] seedPlate, double[] seedBias,
        float[]? margin, double skipMarginAbove = -1, float[]? previousMargin = null)
    {
        var seedCount = seedX.Length;
        var scanned = 0;
        Parallel.For(0, grid.CellCount, () => 0, (cell, _, local) =>
        {
            if (skipMarginAbove >= 0 && previousMargin is not null
                && previousMargin[cell] > skipMarginAbove)
            {
                return local;
            }

            var x = grid.UnitVector[cell * 3];
            var y = grid.UnitVector[cell * 3 + 1];
            var z = grid.UnitVector[cell * 3 + 2];

            var best = 0;
            var bestDot = double.NegativeInfinity;
            var secondDot = double.NegativeInfinity;
            for (var i = 0; i < seedCount; i++)
            {
                // 權重直接加在內積上：權重大的板塊，邊界會往外推
                var dot = x * seedX[i] + y * seedY[i] + z * seedZ[i] + seedBias[i];
                if (dot > bestDot)
                {
                    secondDot = bestDot;
                    bestDot = dot;
                    best = i;
                }
                else if (dot > secondDot)
                {
                    secondDot = dot;
                }
            }

            // 屬於哪個板塊由子種子映射回去——邊界因此是子種子之間的不規則段落，
            // 而不是兩個板塊種子之間那條乾淨的大圓平分線
            plateIdByCell[cell] = seedPlate[best];
            if (margin is not null) margin[cell] = (float)(bestDot - secondDot);
            return local + 1;
        },
        local => Interlocked.Add(ref scanned, local));

        return scanned;
    }

    /// <summary>
    /// 用求解出來的**角速度向量**更新所有板塊：方向就是新的歐拉極、大小就是新的角速度。
    ///
    /// **這是「歐拉極也能改變」的入口**。只縮放角速度的話板塊永遠繞著同一根軸轉，
    /// 板塊重組不可能發生。求解見 <see cref="PlateDynamics"/>。
    ///
    /// 更新後一定要重新扣掉淨旋轉（見 <see cref="RemoveNetRotation"/>）——
    /// 力矩解出來的角速度沒有理由自己加總成零，而殘留的全球共同旋轉會讓整片地殼
    /// 一起掃過網格製造滿地雜訊，物理上卻什麼都沒發生。
    /// </summary>
    /// <param name="angularVelocities">每個板塊的角速度向量（度／百萬年）。</param>
    public void ApplyAngularVelocities(ReadOnlySpan<(double X, double Y, double Z)> angularVelocities)
    {
        for (var p = 0; p < Plates.Length; p++)
        {
            var (x, y, z) = angularVelocities[p];
            var omega = Math.Sqrt(x * x + y * y + z * z);

            // 角速度趨近零時歐拉極沒有定義，保留原本的極、只把速率設成零。
            //
            // **非有限值走同一條路**（2026-09-09 加）：寫成 `!(omega >= 1e-12)` 是為了讓
            // NaN 也落進來——`NaN < 1e-12` 是 false，原本的寫法會讓 NaN 直接寫進歐拉極
            // （`NaN / NaN`），而下一行的 RemoveNetRotation 會把它平均給**每一個**板塊，
            // 一個壞掉的板塊就毀掉整顆星球。這是第二道防線，
            // 第一道在 PlateDynamics.SolveSymmetric3x3（那裡擋的是零格板塊的 0/0）。
            if (!(omega >= 1e-12) || double.IsInfinity(omega))
            {
                Plates[p] = Plates[p] with { AngularVelocityDegPerMyr = 0 };
                continue;
            }

            Plates[p] = Plates[p] with
            {
                PoleX = x / omega,
                PoleY = y / omega,
                PoleZ = z / omega,
                AngularVelocityDegPerMyr = omega,
            };
        }

        // 依面積加權用「實際的歸屬」：板塊座標系裡板塊就是它的地殼（標記法時是同一個陣列）
        RemoveNetRotation(Plates, OwnerByCell);
    }

    /// <summary>
    /// 換到**無淨旋轉參考系**：扣掉全體板塊（依面積加權）的平均角速度。
    ///
    /// 板塊運動只有**相對**運動是物理的，絕對旋轉取決於選哪個參考系，真實的板塊模型
    /// 也是用這個參考系發表數字的。但在這個模擬裡，絕對旋轉不是無害的自由度——
    /// <c>PlateTectonics.Advect</c> 是把地殼搬過離散的格子網格，每搬一次就產生取整誤差、
    /// 空格與假碰撞。若全體帶著一個共同的旋轉，整片地殼會一起掃過網格製造滿地雜訊，
    /// 而物理上什麼事都沒發生。
    ///
    /// 生成時 <c>omega</c> 恆為正、歐拉極隨機，15個向量相加是隨機漫步、不會自己抵消，
    /// 所以這個扣除是必要的。**不要改成「把其中一塊板塊固定住」**——那也能消掉淨旋轉，
    /// 但會讓那一塊變成唯一沒有取整雜訊的板塊，不成比例地容易長出大陸。
    /// </summary>
    private static void RemoveNetRotation(Plate[] plates, int[] plateIdByCell)
    {
        var cellsPerPlate = new int[plates.Length];
        foreach (var plate in plateIdByCell) cellsPerPlate[plate]++;

        double meanX = 0, meanY = 0, meanZ = 0;
        for (var p = 0; p < plates.Length; p++)
        {
            var weight = (double)cellsPerPlate[p] / plateIdByCell.Length;
            meanX += plates[p].PoleX * plates[p].AngularVelocityDegPerMyr * weight;
            meanY += plates[p].PoleY * plates[p].AngularVelocityDegPerMyr * weight;
            meanZ += plates[p].PoleZ * plates[p].AngularVelocityDegPerMyr * weight;
        }

        for (var p = 0; p < plates.Length; p++)
        {
            var x = plates[p].PoleX * plates[p].AngularVelocityDegPerMyr - meanX;
            var y = plates[p].PoleY * plates[p].AngularVelocityDegPerMyr - meanY;
            var z = plates[p].PoleZ * plates[p].AngularVelocityDegPerMyr - meanZ;

            var omega = Math.Sqrt(x * x + y * y + z * z);

            // **一律用 `with`，不要用 `new Plate(...)` 重建**（2026-09-08 踩過）。
            //
            // 原本這裡是位置參數重建，只手動帶了密度異常，而且上面還留了一條註解警告
            // 「漏掉的話重板塊會被靜靜地變回普通板塊」。後來加了 IsOceanicOnly，
            // 這裡沒跟著更新，於是那個旗標每次扣淨旋轉都被預設值蓋掉——
            // 症狀是泛大洋板塊上照樣長出大陸，而且**完全沒有錯誤訊息**，
            // 連密度異常都還在（因為它有被手動帶過去），看起來像是設定根本沒生效。
            //
            // 註解擋不住第二次；`with` 才擋得住，因為新欄位會自動跟著走。
            // 扣完之後剛好靜止的板塊沒有歐拉極可言，保留原本的極、角速度歸零即可。
            plates[p] = omega < 1e-12
                ? plates[p] with { AngularVelocityDegPerMyr = 0 }
                : plates[p] with
                {
                    PoleX = x / omega,
                    PoleY = y / omega,
                    PoleZ = z / omega,
                    AngularVelocityDegPerMyr = omega,
                };
        }
    }

    /// <summary>
    /// 某一格的板塊運動速度（公尺／百萬年），v = ω × r。
    /// 回傳的是三維向量，本身已經與該點的地表相切（外積的結果必定垂直於 r）。
    /// </summary>
    /// <param name="grid">網格。</param>
    /// <param name="cell">格子索引。</param>
    public (double X, double Y, double Z) VelocityAt(SimulationGrid grid, int cell)
    {
        // **用這一格地殼實際所屬的板塊（OwnerByCell），不是 Voronoi 的**：速度是地殼的速度。
        // 板塊座標系時兩者可以不同，邊界兩側的格子常常同屬一個 Voronoi 板塊——用 Voronoi 的話相對速度是 0，
        // 邊界分類整個亂掉（P4 實測：分類出 10,175 個轉形、107 個聚合，用正確的速度重算是 4,201 個聚合），
        // 沒有聚合邊界就沒有板片拉力，所有板塊慢到 0.01~0.16°/Myr。標記法時兩者是同一個陣列。
        var plate = Plates[OwnerByCell[cell]];
        var omegaRad = plate.AngularVelocityDegPerMyr * Math.PI / 180.0;

        // r 用實際半徑，算出來的速度才有公尺/百萬年的物理意義
        var rx = grid.UnitVector[cell * 3] * grid.PlanetRadiusMeters;
        var ry = grid.UnitVector[cell * 3 + 1] * grid.PlanetRadiusMeters;
        var rz = grid.UnitVector[cell * 3 + 2] * grid.PlanetRadiusMeters;

        var wx = plate.PoleX * omegaRad;
        var wy = plate.PoleY * omegaRad;
        var wz = plate.PoleZ * omegaRad;

        return (wy * rz - wz * ry, wz * rx - wx * rz, wx * ry - wy * rx);
    }

    /// <summary>
    /// 歐拉極要偏向自轉軸多少（0＝完全隨機，1＝正好落在自轉軸上）。
    ///
    /// 板塊繞著靠近自轉軸的歐拉極旋轉時，地表的運動就沿著緯線——也就是**以東西向為主**。
    ///
    /// **這是這顆星球的設定，不是照抄地球。** 地球的板塊運動主要由地函對流驅動
    /// （板片拉力、洋脊推力），跟自轉沒有直接關係，所以印度板塊往北、太平洋板塊往西北、
    /// 納茲卡板塊往東，方向各異。真正跟自轉有關的是「岩石圈西向漂移」
    /// （相對地函約0.2度/百萬年的淨西向旋轉，一般歸因於潮汐減速），但那是小的疊加分量、不是主導。
    /// 取0.7而不是1.0：保留一些南北向分量，否則所有板塊邊界都會變成平行的水平線，
    /// 長不出斜向的隱沒帶與轉形斷層。
    /// </summary>
    private const double EulerPoleSpinAxisBias = 0.7;

    /// <summary>
    /// 取一個偏向自轉軸（±Z）的隨機歐拉極，讓板塊運動以東西向為主，見 <see cref="EulerPoleSpinAxisBias"/>。
    /// </summary>
    private static (double X, double Y, double Z) RandomPoleBiasedToSpinAxis(Random rng)
    {
        var (x, y, z) = RandomUnitVector(rng);

        // 把 |z| 往 1 推，再把水平分量等比例縮小以維持單位長度
        var sign = z < 0 ? -1.0 : 1.0;
        var biasedAbsZ = 1.0 - (1.0 - Math.Abs(z)) * (1.0 - EulerPoleSpinAxisBias);
        var horizontalScale = Math.Sqrt(Math.Max(0.0, 1.0 - biasedAbsZ * biasedAbsZ))
            / Math.Max(Math.Sqrt(Math.Max(0.0, 1.0 - z * z)), 1e-12);

        return (x * horizontalScale, y * horizontalScale, sign * biasedAbsZ);
    }

    /// <summary>在球面上取一個均勻分布的隨機單位向量（用z均勻分布法，直接對經緯度取隨機會在極區聚集）。</summary>
    private static (double X, double Y, double Z) RandomUnitVector(Random rng)
    {
        var z = rng.NextDouble() * 2.0 - 1.0;
        var theta = rng.NextDouble() * 2.0 * Math.PI;
        var r = Math.Sqrt(1.0 - z * z);
        return (r * Math.Cos(theta), r * Math.Sin(theta), z);
    }
}
