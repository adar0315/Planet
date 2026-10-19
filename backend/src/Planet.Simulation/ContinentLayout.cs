using Planet.Generation;

namespace Planet.Simulation;

/// <summary>
/// 大尺度陸地配置（v2 方向 2）：照使用者畫的草圖（<see cref="ContinentSketch"/>）決定大陸在哪裡、長什麼樣子，
/// 再用 <see cref="ContinentScenario"/> 照劇本讓它們移動、碰撞與裂開，物理模擬負責細節（山脈、島弧、海岸、地殼年齡）。
///
/// ## 為什麼不讓物理自己長出來
///
/// P4 試了三輪（島弧拼接、碰撞阻力、板塊焊接，見 docs/planet-evolution-v2-plan.md 的 P4 進度）：
/// 從幾乎全海洋開始，大陸只能靠島弧沿板塊邊緣長，天生是細長的帶子、圍著海盆排成環，
/// 3 億年後陸地夠多了（22.9%），最大兩塊卻只佔陸地 9%＋8%。地球的大陸是幾十億年拼接的結果。
///
/// ## 配置（2026-10-18 使用者畫的草圖）
///
/// 一開始是三塊大陸 A、B、C：
///
/// - **A**：北半（<see cref="ANorth"/>）與南半（<see cref="ASouth"/>）在西側大片相連，東側是一道海灣，之後從這裡慢慢張開
///   （以西側相連處為支點）。整塊往西、跟太平洋板塊相撞，西岸堆出山脈。南半東側裂出大島 <see cref="D"/>。
/// - **B**：往東北撞上 C，形成高山。南部裂出一塊 <see cref="F"/>。
/// - **C**：東北側跟太平洋板塊擠壓，形成高地與山。東南側裂出島 <see cref="E"/>，兩者之間留下零星小島（<see cref="SmallIslands"/>）。
/// - A 與 B、C 之間的海中間是一條南北向的洋脊；C 的東側到 A 的西側是一整塊太平洋板塊，它南邊另有一塊獨立的海洋板塊；
///   南北極各一塊獨立的海洋板塊。
///
/// 草圖直接取樣到網格，形狀照畫的來；取樣位置加一點點起伏（<see cref="ContinentLayoutParameters.SketchWarpDegrees"/>），
/// 海岸才不是像素的鋸齒。大島裂開之前跟母大陸是同一塊板塊（裂開的那一刻才切成新板塊，見 <see cref="ContinentScenario"/>），
/// 所以切線上一開始沒有板塊邊界、不會被碰撞規則的雜訊堆出山。
/// </summary>
public static class ContinentLayout
{
    /// <summary>標籤陣列裡代表海洋的值。</summary>
    public const int Ocean = -1;

    /// <summary>陸塊編號：A 的北半。</summary>
    public const int ANorth = 0;

    /// <summary>陸塊編號：A 的南半。</summary>
    public const int ASouth = 1;

    /// <summary>陸塊編號：D，從 A 南半東側裂出的大島。</summary>
    public const int D = 2;

    /// <summary>陸塊編號：B。</summary>
    public const int B = 3;

    /// <summary>陸塊編號：F，從 B 南部裂出的一塊。</summary>
    public const int F = 4;

    /// <summary>陸塊編號：C。</summary>
    public const int C = 5;

    /// <summary>陸塊編號：E，從 C 東南側裂出的島。</summary>
    public const int E = 6;

    /// <summary>陸塊編號：C 與 E 之間的小島群（同一個編號、好幾塊）。起始時是使用者在板塊圖上畫的 C 與 E 之間的切線與切線上的小島，切線本身見 <see cref="ContinentLayoutResult.RiftGap"/>。</summary>
    public const int SmallIslands = 7;

    /// <summary>陸塊數。</summary>
    public const int LandMassCount = 8;

    /// <summary>
    /// 大陸配置起始時的板塊數：A 北、A 南、B、C、太平洋、北極、南極，加上之後才裂出來的 D、F、E、小島群——
    /// 海洋全部歸給這幾塊板塊，不再有隨機的 Voronoi 海洋板塊。編號固定，見 <see cref="PlateOf"/>。
    /// </summary>
    public const int PlateCount = 12;

    /// <summary>太平洋板塊的編號。</summary>
    public const int PacificPlate = 4;

    /// <summary>北極板塊的編號（2026-10-18 使用者指定：南北極各一個獨立板塊）。</summary>
    public const int NorthPolarPlate = 9;

    /// <summary>南極板塊的編號。</summary>
    public const int SouthPolarPlate = 10;

    /// <summary>
    /// 太平洋南邊的海洋板塊（2026-10-18 使用者指定：A 南板塊位於太平洋板塊南部的部分切出來獨立）：
    /// 原本歸給 A 南的海裡，經度落在太平洋板塊東西範圍內的那一片。
    /// </summary>
    public const int SouthPacificPlate = 11;

    /// <summary>每塊陸地（依陸塊編號）的板塊。D、F、E、小島群裂開之前，它們的格子先歸給母大陸的板塊。</summary>
    public static IReadOnlyList<int> PlateOf { get; } = [0, 1, 5, 2, 6, 3, 7, 8];

    /// <summary>每塊陸地起始時（裂開之前）實際歸給哪個板塊。</summary>
    private static readonly int[] InitialPlate = [0, 1, 1, 2, 2, 3, 3, 3];

    /// <summary>生成配置。</summary>
    /// <param name="grid">網格。</param>
    /// <param name="seed">種子（只影響海岸的細小起伏）。</param>
    /// <param name="parameters">配置參數，預設 <see cref="ContinentLayoutParameters.Default"/>。</param>
    public static ContinentLayoutResult Generate(SimulationGrid grid, int seed, ContinentLayoutParameters? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var p = parameters ?? ContinentLayoutParameters.Default;
        var sketch = PullTowardRidge(ContinentSketch.LoadEmbedded(), p.RidgeGapKeepShare);
        var noise = new SimplexNoise3D(unchecked(seed * 31 + 17));
        var u = grid.UnitVector;

        // A 北與 A 南的分界：草圖上 A 東側海灣最深處（最西）那一列；支點是那一列 A 最西的像素
        var (splitRow, hingeX) = FindGulf(sketch);
        var (hingeLat, hingeLon) = sketch.LatLon(hingeX, splitRow);
        var hinge = FromLatLon(hingeLat, hingeLon);

        var label = new int[grid.CellCount];
        var pacific = new bool[grid.CellCount];
        var ridge = new bool[grid.CellCount];
        for (var c = 0; c < grid.CellCount; c++)
        {
            var v = (X: u[c * 3], Y: u[c * 3 + 1], Z: u[c * 3 + 2]);
            var lat = grid.Latitude[c];
            var lon = Math.Atan2(v.Y, v.X) * 180.0 / Math.PI;
            // 取樣位置加一點起伏，海岸才不是像素的鋸齒
            var dLat = p.SketchWarpDegrees * noise.Fbm(v.X + 3, v.Y + 3, v.Z + 3, 3, 6.0);
            var dLon = p.SketchWarpDegrees * noise.Fbm(v.X + 9, v.Y + 9, v.Z + 9, 3, 6.0) / Math.Max(Math.Cos(lat * Math.PI / 180.0), 0.2);
            var (px, py) = sketch.Pixel(lat + dLat, lon + dLon);
            var cls = sketch.Classes[py * sketch.Width + px];
            label[c] = cls switch
            {
                SketchClass.A => py < splitRow ? ANorth : ASouth,
                SketchClass.D => D,
                SketchClass.B => B,
                SketchClass.F => F,
                SketchClass.C => C,
                SketchClass.E => E,
                _ => Ocean,
            };
            pacific[c] = cls == SketchClass.Pacific;
            ridge[c] = cls == SketchClass.Ridge;
        }

        // E 與小島群（2026-10-18 使用者在板塊圖上重畫的裂開方式，見 ContinentPlateMap.CPartAt）：C 與 E 的陸地照圖上的白線重新切，
        // 切線東南邊是 E，切線上圍起來的是小島，切線本身也歸小島群、裂開時沉成海床（RiftGap）——小島群的板塊因此是連成一條的，
        // 不會被歸屬平滑當成碎片（5 塊互不相連的島只會留下一塊）。
        // 第一版是沿草圖上 C 與 E 的交界，在 E 這一側取一條 90 公里寬的窄帶、分段當小島。
        var plateMap = ContinentPlateMap.LoadEmbedded();
        var gap = new bool[grid.CellCount];
        var unresolved = new Queue<int>();
        var resolved = new bool[grid.CellCount];
        for (var c = 0; c < grid.CellCount; c++)
        {
            if (label[c] != C && label[c] != E) continue;
            var lon = Math.Atan2(u[c * 3 + 1], u[c * 3]) * 180.0 / Math.PI;
            switch (plateMap.CPartAt(grid.Latitude[c], lon))
            {
                case ContinentPlateMap.PartC: label[c] = C; break;
                case ContinentPlateMap.PartE: label[c] = E; break;
                case ContinentPlateMap.PartIsland: label[c] = SmallIslands; break;
                case ContinentPlateMap.PartCut: label[c] = SmallIslands; gap[c] = true; break;
                default:
                    // 圖上這裡塗成了海（使用者把 E 東岸約 55 公里塗成太平洋）：照圖變成海
                    label[c] = Ocean;
                    continue;
            }
            resolved[c] = true;
            unresolved.Enqueue(c);
        }
        // 保險：還沒分到的 C、E 格子跟著旁邊的（照上面的規則不會發生，每一格不是分到就是變成海）
        while (unresolved.Count > 0)
        {
            var c = unresolved.Dequeue();
            var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
            for (var k = 0; k < SimulationGrid.MaxNeighborsPerCell; k++)
            {
                var n = grid.Neighbors[baseIndex + k];
                if (n == SimulationGrid.NoNeighbor || resolved[n] || (label[n] != C && label[n] != E)) continue;
                label[n] = label[c];
                gap[n] = gap[c];
                resolved[n] = true;
                unresolved.Enqueue(n);
            }
        }
        DropStrayCutCells(grid, label, gap);
        SmoothCoast(grid, label, CoastSmoothingPasses);
        FillEnclosedWater(grid, label, p.MaxFilledLakeCells(grid));
        var oceanJoin = OceanMargin(grid, label, plateMap, p.CEOceanMarginKm);
        return new ContinentLayoutResult(label, pacific, ridge, hinge, gap, oceanJoin);
    }

    /// <summary>
    /// 一段切線（連在一起的切線格）完全沒碰到 C 的陸地，就不是 C 與 E 之間的切線，是圖上 E 的海岸線被誤認成切線（兩邊剛好都碰到 C 的顏色），還給 E。
    /// </summary>
    private static void DropStrayCutCells(SimulationGrid grid, int[] label, bool[] gap)
    {
        var seen = new bool[grid.CellCount];
        var members = new List<int>();
        var stack = new Stack<int>();
        for (var start = 0; start < grid.CellCount; start++)
        {
            if (!gap[start] || seen[start]) continue;
            members.Clear();
            var touchesC = false;
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                members.Add(c);
                var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
                for (var k = 0; k < SimulationGrid.MaxNeighborsPerCell; k++)
                {
                    var n = grid.Neighbors[baseIndex + k];
                    if (n == SimulationGrid.NoNeighbor) continue;
                    if (label[n] == C) touchesC = true;
                    if (!gap[n] || seen[n]) continue;
                    seen[n] = true;
                    stack.Push(n);
                }
            }
            if (touchesC) continue;
            foreach (var c in members) { label[c] = E; gap[c] = false; }
        }
    }

    /// <summary>C、E 海岸平滑跑幾輪（見 <see cref="SmoothCoast"/>）。</summary>
    private const int CoastSmoothingPasses = 3;

    /// <summary>
    /// C、E 的海岸修平滑（2026-10-18 使用者指定）：只看 C、E 的陸地與跟它們相鄰的海，每一輪
    /// 陸地格周圍的陸地不到一半就變成海、海格周圍的陸地超過一半就變成陸地（跟著周圍最多的那一塊）。
    /// 板塊圖是 1024×512 的像素圖，切出來的 E 與使用者塗掉的東岸都有像素的鋸齒。小島與切線不動。
    /// </summary>
    private static void SmoothCoast(SimulationGrid grid, int[] label, int passes)
    {
        bool IsCE(int l) => l == C || l == E;
        var next = new int[label.Length];
        for (var pass = 0; pass < passes; pass++)
        {
            Array.Copy(label, next, label.Length);
            for (var c = 0; c < grid.CellCount; c++)
            {
                if (!IsCE(label[c]) && label[c] != Ocean) continue;
                int land = 0, total = 0, ce = 0, cCount = 0, other = 0;
                var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
                for (var k = 0; k < SimulationGrid.MaxNeighborsPerCell; k++)
                {
                    var n = grid.Neighbors[baseIndex + k];
                    if (n == SimulationGrid.NoNeighbor) continue;
                    total++;
                    if (label[n] == Ocean) continue;
                    land++;
                    if (IsCE(label[n])) { ce++; if (label[n] == C) cCount++; }
                    else other++;
                }
                if (IsCE(label[c]) && land * 2 < total && other == 0) next[c] = Ocean;
                else if (label[c] == Ocean && ce * 2 > total && other == 0) next[c] = cCount * 2 >= ce ? C : E;
            }
            Array.Copy(next, label, label.Length);
        }
    }

    /// <summary>
    /// C、E 周圍的海（2026-10-18 使用者指定：E 的板塊要包含周圍的海、C 的板塊往海裡擴大）：離 C、E、小島群的陸地
    /// marginKm 以內、板塊圖上原本是 C 的海或太平洋南的海格，起始時都歸 C 的板塊；其中離 E 比離 C 近的，E 裂開時跟著 E 走。
    /// 太平洋（固定）與兩極不動。回傳每格要併進哪塊陸地的板塊（<see cref="C"/> 或 <see cref="E"/>），其他是 −1。
    /// </summary>
    private static int[] OceanMargin(SimulationGrid grid, int[] label, ContinentPlateMap plateMap, double marginKm)
    {
        var u = grid.UnitVector;
        var maxHops = (int)Math.Round(marginKm * 1000.0 / grid.AverageCellSpacingMeters);
        bool Eligible(int c)
        {
            if (label[c] != Ocean) return false;
            var plate = plateMap.PlateAt(grid.Latitude[c], Math.Atan2(u[c * 3 + 1], u[c * 3]) * 180.0 / Math.PI);
            return plate == InitialPlate[C] || plate == SouthPacificPlate;
        }
        var fromC = HopsFrom(grid, c => label[c] == C, Eligible);
        var fromE = HopsFrom(grid, c => label[c] == E, Eligible);
        var join = new int[grid.CellCount];
        for (var c = 0; c < grid.CellCount; c++)
        {
            join[c] = -1;
            if (label[c] != Ocean) continue;
            var nearest = Math.Min(fromC[c], fromE[c]);
            if (nearest > maxHops) continue;
            join[c] = fromE[c] < fromC[c] ? E : C;
        }
        return join;
    }

    /// <summary>
    /// 洋脊兩側的大陸沿經度往洋脊靠近（見 <see cref="ContinentLayoutParameters.RidgeGapKeepShare"/>）：
    /// A 側（A、D）整塊往東、B/C 側（B、F、C、E）整塊往西，各移 <see cref="RidgeGapShifts"/> 算出的像素數。
    /// 整塊平移經度等於繞地軸轉，形狀不變；洋脊留在原處。空出來的地方：原本貼著太平洋的那一側歸太平洋（太平洋跟著靠過去），其他是一般的海。
    /// </summary>
    internal static ContinentSketch PullTowardRidge(ContinentSketch sketch, double keepShare)
    {
        var (shiftA, shiftBC, _, _, _) = RidgeGapShifts(sketch, keepShare);
        if (shiftA == 0 && shiftBC == 0) return sketch;
        int w = sketch.Width, h = sketch.Height;
        var src = sketch.Classes;
        var classes = new SketchClass[src.Length];
        for (var i = 0; i < src.Length; i++) classes[i] = IsLandClass(src[i]) ? SketchClass.Ocean : src[i];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var cls = src[y * w + x];
                if (!IsLandClass(cls)) continue;
                var dx = IsASide(cls) ? shiftA : -shiftBC;
                classes[y * w + ((x + dx) % w + w) % w] = cls;
            }
        }

        // 大陸跟太平洋之間空出來的那一條歸太平洋（2026-10-18 使用者指定）：原本貼著太平洋的那幾列，
        // 太平洋的邊界跟著靠過去，A、C 一開始就貼著太平洋、劇本一開始就撞得到。
        // 只補原本就貼著的列（相隔不超過 PacificContactPixels）——B 那幾列離太平洋很遠，不能把中間的海整片補成太平洋。
        const int PacificContactPixels = 6;
        for (var y = 0; y < h; y++)
        {
            int Row(int x) => y * w + ((x % w) + w) % w;
            // A 側：最西的 A 側陸地，往西找太平洋的東緣
            var westA = -1;
            for (var x = 0; x < w && westA < 0; x++) if (IsASide(src[Row(x)])) westA = x;
            if (westA >= 0)
            {
                var gap = 1;
                while (gap <= PacificContactPixels && src[Row(westA - gap)] != SketchClass.Pacific) gap++;
                if (gap <= PacificContactPixels)
                    for (var x = westA - gap + 1; x < westA + shiftA; x++)
                        if (classes[Row(x)] == SketchClass.Ocean) classes[Row(x)] = SketchClass.Pacific;
            }
            // B/C 側：最東的 B/C 側陸地，往東找太平洋的西緣
            var eastBC = -1;
            for (var x = w - 1; x >= 0 && eastBC < 0; x--) if (IsLandClass(src[Row(x)]) && !IsASide(src[Row(x)])) eastBC = x;
            if (eastBC >= 0)
            {
                var gap = 1;
                while (gap <= PacificContactPixels && src[Row(eastBC + gap)] != SketchClass.Pacific) gap++;
                if (gap <= PacificContactPixels)
                    for (var x = eastBC + gap - 1; x > eastBC - shiftBC; x--)
                        if (classes[Row(x)] == SketchClass.Ocean) classes[Row(x)] = SketchClass.Pacific;
            }
        }
        return sketch.WithClasses(classes);
    }

    /// <summary>
    /// 洋脊兩側的大陸各要往洋脊移幾個像素：每一列量 A 側東岸到 B/C 側西岸之間的海有多寬（那一列要有洋脊、兩側都有大陸），
    /// 取中位數，要縮掉的寬度兩側平分。也回傳兩側到洋脊最近的距離（移動量要小於它，大陸才不會越過洋脊）。
    /// </summary>
    internal static (int ShiftA, int ShiftBC, double MedianGap, int MinToRidgeA, int MinToRidgeBC) RidgeGapShifts(ContinentSketch sketch, double keepShare)
    {
        int w = sketch.Width, h = sketch.Height;
        // 只採用靠近整條洋脊中線的像素：F（青色）邊緣跟白底混出來的淺青色也會被分成洋脊，
        // 混進來的話那幾列的洋脊位置被拉到 B 旁邊，量到 B/C 離洋脊只剩 2 個像素
        var allRidge = new List<int>();
        for (var i = 0; i < sketch.Classes.Length; i++)
            if (sketch.Classes[i] == SketchClass.Ridge) allRidge.Add(i % w);
        if (allRidge.Count == 0) return (0, 0, 0, 0, 0);
        allRidge.Sort();
        var ridgeCenter = allRidge[allRidge.Count / 2];
        var ridgeReach = w / 12;
        var gaps = new List<int>();
        int minA = int.MaxValue, minBC = int.MaxValue;
        for (var y = 0; y < h; y++)
        {
            long sum = 0; var count = 0;
            for (var x = Math.Max(0, ridgeCenter - ridgeReach); x <= Math.Min(w - 1, ridgeCenter + ridgeReach); x++)
                if (sketch.Classes[y * w + x] == SketchClass.Ridge) { sum += x; count++; }
            if (count == 0) continue;
            var ridgeX = (int)(sum / count);
            // 洋脊往西碰到的第一塊陸地要是 A 側、往東碰到的第一塊要是 B/C 側，這一列才算
            var eastA = -1;
            for (var x = ridgeX - 1; x >= 0; x--)
            {
                var cls = sketch.Classes[y * w + x];
                if (!IsLandClass(cls)) continue;
                if (IsASide(cls)) eastA = x;
                break;
            }
            var westBC = -1;
            for (var x = ridgeX + 1; x < w; x++)
            {
                var cls = sketch.Classes[y * w + x];
                if (!IsLandClass(cls)) continue;
                if (!IsASide(cls)) westBC = x;
                break;
            }
            if (eastA < 0 || westBC < 0) continue;
            gaps.Add(westBC - eastA - 1);
            minA = Math.Min(minA, ridgeX - eastA);
            minBC = Math.Min(minBC, westBC - ridgeX);
        }
        if (gaps.Count == 0) return (0, 0, 0, 0, 0);
        gaps.Sort();
        var median = gaps.Count % 2 == 1 ? gaps[gaps.Count / 2] : (gaps[gaps.Count / 2 - 1] + gaps[gaps.Count / 2]) / 2.0;
        // 兩側平分要縮掉的寬度，但都不准越過洋脊（離洋脊至少留 RidgeClearancePixels）：
        // 一側到頂了，剩下的給另一側；兩側都到頂就只縮到那裡
        // （草圖上 B 的西北角離洋脊最近，拉近 2/3 時 B 會越過洋脊約 21 個像素，2026-10-18）
        const int RidgeClearancePixels = 5;
        var total = (int)Math.Round(median * (1.0 - keepShare));
        int maxA = Math.Max(0, minA - RidgeClearancePixels), maxBC = Math.Max(0, minBC - RidgeClearancePixels);
        var shiftA = Math.Min(total / 2, maxA);
        var shiftBC = Math.Min(total - shiftA, maxBC);
        shiftA = Math.Min(total - shiftBC, maxA);
        return (shiftA, shiftBC, median, minA, minBC);
    }

    private static bool IsLandClass(SketchClass cls) => cls is SketchClass.A or SketchClass.D or SketchClass.B or SketchClass.F or SketchClass.C or SketchClass.E;

    private static bool IsASide(SketchClass cls) => cls is SketchClass.A or SketchClass.D;

    /// <summary>
    /// 找 A 東側海灣最深處：草圖上 A 的範圍內，上方與下方（同一行）都碰得到 A、左邊（同一列）也是 A 的海像素裡最西的那一個。
    /// 回傳那一列與那一列 A 最西的像素（支點）。
    /// </summary>
    private static (int Row, int HingeX) FindGulf(ContinentSketch sketch)
    {
        int w = sketch.Width, h = sketch.Height;
        bool IsA(int x, int y) => sketch.Classes[y * w + ((x % w) + w) % w] == SketchClass.A;
        bool IsSea(int x, int y) => sketch.Classes[y * w + ((x % w) + w) % w] is SketchClass.Ocean or SketchClass.Ridge;
        const int Reach = 120;
        int bestX = int.MaxValue, bestY = -1;
        for (var y = 1; y < h - 1; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (!IsSea(x, y)) continue;
                var up = false;
                for (var d = 1; d <= Reach && y - d >= 0 && !up; d++) { if (IsA(x, y - d)) up = true; else if (!IsSea(x, y - d)) break; }
                if (!up) continue;
                var down = false;
                for (var d = 1; d <= Reach && y + d < h && !down; d++) { if (IsA(x, y + d)) down = true; else if (!IsSea(x, y + d)) break; }
                if (!down) continue;
                var west = false;
                for (var d = 1; d <= Reach && !west; d++) { if (IsA(x - d, y)) west = true; else if (!IsSea(x - d, y)) break; }
                if (!west) continue;
                if (x < bestX) { bestX = x; bestY = y; }
            }
        }
        if (bestY < 0) throw new InvalidOperationException("草圖上找不到 A 東側的海灣（A 北與 A 南的分界）。");
        var hingeX = bestX;
        while (IsA(hingeX - 1, bestY) && bestX - hingeX < w) hingeX--;
        return (bestY, ((hingeX % w) + w) % w);
    }

    /// <summary>
    /// 照配置建立起始的地殼與板塊歸屬（取代早期大轟炸），回傳每塊陸地（依陸塊編號）之後要用的板塊（<see cref="PlateOf"/>）：
    ///
    /// - **陸地**：大陸地殼，海岸 <see cref="CoastCrustKm"/>、往內陸線性加厚到 <see cref="InteriorCrustKm"/>
    ///  （<see cref="InteriorRampKm"/> 內），再加一點雜訊。
    /// - **海洋**：太平洋是 0~60 Myr 的大尺度雜訊；其他海洋的年齡由離洋脊多遠決定（洋脊 0 歲、往兩側變老）。
    ///   平均年齡要接近模擬的平衡年齡，否則海床被新生地殼取代、變淺的過程會一直把海平面頂高。
    /// - **板塊**：板塊數固定 <see cref="PlateCount"/>，旗標全部重設成一般板塊（太平洋是「只能是海洋」）。
    ///   陸地歸給它的板塊（D、F、E、小島群先歸給母大陸）；太平洋那一片歸太平洋板塊；
    ///   其他的海歸給最近的大陸（A 北、A 南、B、C），**不跨過洋脊**——洋脊就是 A 與 B、C 之間的板塊邊界。
    ///
    /// 只改 <see cref="PlateSet.PlateIdByCell"/>，所以只能用在板塊座標系。
    /// </summary>
    /// <param name="grid">網格。</param>
    /// <param name="state">起始狀態，地殼欄位會被覆寫（海陸與海平面之後由引擎注水決定）。</param>
    /// <param name="plates">板塊（要有 <see cref="PlateCount"/> 個），<see cref="PlateSet.PlateIdByCell"/> 與旗標會被改。</param>
    /// <param name="layout"><see cref="Generate"/> 的結果。</param>
    /// <param name="seed">種子（太平洋年齡與大陸厚度的雜訊）。</param>
    public static int[] ApplyTo(SimulationGrid grid, SimulationState state, PlateSet plates, ContinentLayoutResult layout, int seed)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(plates);
        ArgumentNullException.ThrowIfNull(layout);
        if (plates.Plates.Length != PlateCount) throw new ArgumentException($"大陸配置起始要剛好 {PlateCount} 個板塊。", nameof(plates));
        var label = layout.Label;
        var noise = new SimplexNoise3D(unchecked(seed * 131 + 71));
        var u = grid.UnitVector;
        var spacingKm = grid.AverageCellSpacingMeters / 1000.0;

        var coastHops = HopsFrom(grid, c => label[c] != Ocean && Touches(grid, c, n => label[n] == Ocean), c => label[c] != Ocean);
        var ridgeHops = HopsFrom(grid, c => layout.Ridge[c], c => label[c] == Ocean && !layout.Pacific[c]);

        for (var c = 0; c < grid.CellCount; c++)
        {
            var (x, y, z) = (u[c * 3], u[c * 3 + 1], u[c * 3 + 2]);
            state.OrganicThickness[c] = 0f;
            state.SedimentThickness[c] = 0f;
            state.OceanicOnly[c] = false;
            if (label[c] == Ocean)
            {
                state.Crust[c] = CrustType.Oceanic;
                state.CrustThickness[c] = Isostasy.OceanicCrustThicknessKm;
                state.CrustDensity[c] = Isostasy.OceanicCrustDensity;
                double age;
                if (layout.Pacific[c] || ridgeHops[c] == int.MaxValue)
                    age = MaxOceanAgeMyr * (0.5 + 0.5 * noise.Fbm(x, y, z, 3, 1.5));
                else
                    age = Math.Min(MaxOceanAgeMyr, ridgeHops[c] * spacingKm / RidgeHalfSpreadingKmPerMyr);
                state.CrustAge[c] = (float)age;
            }
            else
            {
                var ramp = Math.Min(1.0, coastHops[c] * spacingKm / InteriorRampKm);
                var bump = noise.Fbm(x + 50, y + 50, z + 50, 4, 4.0) * CrustNoiseKm;
                state.Crust[c] = CrustType.Continental;
                state.CrustThickness[c] = (float)(CoastCrustKm + ramp * (InteriorCrustKm - CoastCrustKm) + bump);
                state.CrustDensity[c] = Isostasy.ContinentalCrustDensity;
                state.CrustAge[c] = ContinentalAgeMyr;
            }
        }

        // 板塊旗標全部重設：大陸不能落在重板塊或「只能是海洋」的板塊上；太平洋與兩極只能是海洋
        for (var p = 0; p < PlateCount; p++)
            plates.Plates[p] = plates.Plates[p] with
            {
                DensityAnomalyKgPerM3 = 0f,
                IsOceanicOnly = p is PacificPlate or NorthPolarPlate or SouthPolarPlate or SouthPacificPlate,
            };

        // 陸地與太平洋
        var assigned = new int[grid.CellCount];
        Array.Fill(assigned, -1);
        for (var c = 0; c < grid.CellCount; c++)
        {
            if (label[c] != Ocean) assigned[c] = InitialPlate[label[c]];
            else if (layout.Pacific[c]) assigned[c] = PacificPlate;
        }
        // 其他的海：從大陸往外，歸給最近的大陸，不跨過洋脊（洋脊格本身會被其中一側拿走，但不從它往外擴）
        var queue = new Queue<int>();
        for (var c = 0; c < grid.CellCount; c++)
            if (label[c] != Ocean) queue.Enqueue(c);
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            if (label[c] == Ocean && layout.Ridge[c]) continue;
            var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                var n = grid.Neighbors[baseIndex + s];
                if (n == SimulationGrid.NoNeighbor || assigned[n] >= 0) continue;
                assigned[n] = assigned[c];
                queue.Enqueue(n);
            }
        }
        // 被洋脊整圈圍住的小塊海（上面那一輪不從洋脊往外擴，進不去）：這一輪可以穿過洋脊，就近指派
        for (var c = 0; c < grid.CellCount; c++)
            if (assigned[c] >= 0) queue.Enqueue(c);
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                var n = grid.Neighbors[baseIndex + s];
                if (n == SimulationGrid.NoNeighbor || assigned[n] >= 0) continue;
                assigned[n] = assigned[c];
                queue.Enqueue(n);
            }
        }
        for (var c = 0; c < grid.CellCount; c++)
            plates.PlateIdByCell[c] = assigned[c];

        // A 南板塊的海裡，經度落在太平洋板塊東西範圍內的（太平洋的南邊）切出來成為獨立的海洋板塊
        double sumX = 0, sumY = 0;
        for (var c = 0; c < grid.CellCount; c++)
            if (layout.Pacific[c]) { sumX += u[c * 3]; sumY += u[c * 3 + 1]; }
        var pacificLon = Math.Atan2(sumY, sumX) * 180.0 / Math.PI;
        double PacificRelLon(int c) => ((Math.Atan2(u[c * 3 + 1], u[c * 3]) * 180.0 / Math.PI - pacificLon) % 360 + 540) % 360 - 180;
        double pacificWest = double.PositiveInfinity, pacificEast = double.NegativeInfinity;
        for (var c = 0; c < grid.CellCount; c++)
        {
            if (!layout.Pacific[c]) continue;
            var r = PacificRelLon(c);
            pacificWest = Math.Min(pacificWest, r);
            pacificEast = Math.Max(pacificEast, r);
        }
        for (var c = 0; c < grid.CellCount; c++)
        {
            if (label[c] != Ocean || plates.PlateIdByCell[c] != InitialPlate[ASouth]) continue;
            var r = PacificRelLon(c);
            if (r >= pacificWest && r <= pacificEast) plates.PlateIdByCell[c] = SouthPacificPlate;
        }

        // 南北極各一塊獨立的海洋板塊：緯度超過 PolarPlateLatitudeDegrees（帶一點起伏）的海；陸地不切
        for (var c = 0; c < grid.CellCount; c++)
        {
            if (label[c] != Ocean) continue;
            var (x, y, z) = (u[c * 3], u[c * 3 + 1], u[c * 3 + 2]);
            var limit = PolarPlateLatitudeDegrees + PolarPlateEdgeNoiseDegrees * noise.Fbm(x + 20, y + 20, z + 20, 3, 3.0);
            if (grid.Latitude[c] > limit) plates.PlateIdByCell[c] = NorthPolarPlate;
            else if (grid.Latitude[c] < -limit) plates.PlateIdByCell[c] = SouthPolarPlate;
        }

        // 使用者在板塊圖上直接改過的海洋板塊範圍蓋過上面的規則（見 ContinentPlateMap）；陸地不動。
        // C、E 周圍的海再蓋一次，歸 C 的板塊（見 OceanMargin）
        var plateMap = ContinentPlateMap.LoadEmbedded();
        for (var c = 0; c < grid.CellCount; c++)
        {
            if (label[c] != Ocean) continue;
            var lon = Math.Atan2(u[c * 3 + 1], u[c * 3]) * 180.0 / Math.PI;
            plates.PlateIdByCell[c] = layout.OceanJoin[c] >= 0 ? InitialPlate[C] : plateMap.PlateAt(grid.Latitude[c], lon);
        }

        for (var c = 0; c < grid.CellCount; c++)
        {
            state.BedrockElevation[c] = Isostasy.BedrockElevation(
                state.Crust[c], state.CrustThickness[c], state.CrustAge[c], state.CrustDensity[c]);
        }
        return PlateOf.ToArray();
    }

    /// <summary>南北極板塊從哪個緯度開始（度）。</summary>
    private const double PolarPlateLatitudeDegrees = 70;

    /// <summary>南北極板塊的邊界上下起伏幾度（筆直的緯線不自然）。</summary>
    private const double PolarPlateEdgeNoiseDegrees = 3;

    /// <summary>起始海洋地殼的最大年齡（百萬年）。平均要接近模擬的平衡年齡（約 30），否則海平面會一直上升。</summary>
    private const double MaxOceanAgeMyr = 60;

    /// <summary>
    /// 洋脊兩側的海床年齡用的半擴張速率（公里／百萬年）：年齡＝離洋脊的距離 ÷ 這個。15 ＝ 1.5 公分／年（大西洋）。
    /// </summary>
    private const double RidgeHalfSpreadingKmPerMyr = 15;

    /// <summary>
    /// 海岸的大陸地殼厚度（公里），高度約 +300 m。原本取 30（約 −70 m）：海平面在演化中上升幾百公尺，
    /// 水線剛好切過那片幾乎平的沿海帶，海岸被切得很碎（方向 2 第一次跑，1 億年時破碎度 5.9）。
    /// </summary>
    private const double CoastCrustKm = 32;

    /// <summary>
    /// 內陸的大陸地殼厚度（公里）。地球的大陸平均約 35~40。
    /// 原本取 38：內陸是一片高原，起步時陸地平均高出海面約 1.3 km（地球約 0.84 km），氣溫依高度遞減、水氣在海岸就被抬升降光，
    /// 陸地又冷又乾（P6 診斷，2026-10-19）。改成 35，內陸低約 550 m。
    /// </summary>
    private const double InteriorCrustKm = 35;

    /// <summary>從海岸到內陸厚度的距離（公里）。</summary>
    private const double InteriorRampKm = 400;

    /// <summary>大陸厚度雜訊的振幅（公里）。</summary>
    private const double CrustNoiseKm = 2.5;

    /// <summary>起始大陸地殼的年齡（百萬年）。只用在診斷與顯示，大陸的高度不看年齡。</summary>
    private const float ContinentalAgeMyr = 1_000f;

    /// <summary>從 source 格往外（只走 walkable 的格子）每格隔幾步；走不到是 int.MaxValue。</summary>
    private static int[] HopsFrom(SimulationGrid grid, Func<int, bool> source, Func<int, bool> walkable)
    {
        var hops = new int[grid.CellCount];
        Array.Fill(hops, int.MaxValue);
        var queue = new Queue<int>();
        for (var c = 0; c < grid.CellCount; c++)
            if (source(c)) { hops[c] = 0; queue.Enqueue(c); }
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                var n = grid.Neighbors[baseIndex + s];
                if (n == SimulationGrid.NoNeighbor || hops[n] != int.MaxValue || !walkable(n)) continue;
                hops[n] = hops[c] + 1;
                queue.Enqueue(n);
            }
        }
        return hops;
    }

    private static bool Touches(SimulationGrid grid, int c, Func<int, bool> predicate)
    {
        var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
        for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
        {
            var n = grid.Neighbors[baseIndex + s];
            if (n != SimulationGrid.NoNeighbor && predicate(n)) return true;
        }
        return false;
    }

    /// <summary>切出一塊之後，母陸塊若被切成幾塊，只留最大的那塊，其他的併進切出來的那一塊（它們就在切口旁邊）。</summary>
    private static void KeepParentConnected(SimulationGrid grid, int[] label, int parent, int child)
    {
        var component = new int[grid.CellCount];
        Array.Fill(component, -1);
        var sizes = new List<int>();
        var stack = new Stack<int>();
        for (var start = 0; start < grid.CellCount; start++)
        {
            if (label[start] != parent || component[start] >= 0) continue;
            var id = sizes.Count;
            var size = 0;
            component[start] = id;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                size++;
                var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
                for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
                {
                    var n = grid.Neighbors[baseIndex + s];
                    if (n == SimulationGrid.NoNeighbor || label[n] != parent || component[n] >= 0) continue;
                    component[n] = id;
                    stack.Push(n);
                }
            }
            sizes.Add(size);
        }
        if (sizes.Count <= 1) return;
        var main = sizes.IndexOf(sizes.Max());
        for (var c = 0; c < grid.CellCount; c++)
            if (label[c] == parent && component[c] != main) label[c] = child;
    }

    /// <summary>被同一塊陸地整個包住、不超過 maxCells 的水域填成那塊陸地（要的是完整的大陸，湖是 P5 的事）。</summary>
    private static void FillEnclosedWater(SimulationGrid grid, int[] label, int maxCells)
    {
        var component = new int[grid.CellCount];
        Array.Fill(component, -1);
        var stack = new Stack<int>();
        var cells = new List<int>();
        var next = 0;
        for (var start = 0; start < grid.CellCount; start++)
        {
            if (label[start] != Ocean || component[start] >= 0) continue;
            cells.Clear();
            var touching = new HashSet<int>();
            component[start] = next;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                cells.Add(c);
                var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
                for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
                {
                    var n = grid.Neighbors[baseIndex + s];
                    if (n == SimulationGrid.NoNeighbor) continue;
                    if (label[n] != Ocean) { touching.Add(label[n]); continue; }
                    if (component[n] >= 0) continue;
                    component[n] = next;
                    stack.Push(n);
                }
            }
            next++;
            if (cells.Count <= maxCells && touching.Count == 1)
            {
                var owner = touching.First();
                foreach (var c in cells) label[c] = owner;
            }
        }
    }

    private static (double X, double Y, double Z) FromLatLon(double latDegrees, double lonDegrees)
    {
        var la = latDegrees * Math.PI / 180.0;
        var lo = lonDegrees * Math.PI / 180.0;
        return (Math.Cos(la) * Math.Cos(lo), Math.Cos(la) * Math.Sin(lo), Math.Sin(la));
    }

    /// <summary>from 處指向 to 的切向單位向量。</summary>
    internal static (double X, double Y, double Z) TangentToward((double X, double Y, double Z) from, (double X, double Y, double Z) to)
    {
        var d = Dot(from, to);
        var t = (X: to.X - d * from.X, Y: to.Y - d * from.Y, Z: to.Z - d * from.Z);
        var l = Math.Sqrt(Dot(t, t));
        return l > 1e-12 ? (t.X / l, t.Y / l, t.Z / l) : Normalize(Cross((0, 0, 1), from));
    }

    internal static double Angle((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        Math.Acos(Math.Clamp(Dot(a, b), -1.0, 1.0));

    internal static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    internal static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    internal static (double X, double Y, double Z) Normalize((double X, double Y, double Z) a)
    {
        var l = Math.Sqrt(Dot(a, a));
        return l > 1e-15 ? (a.X / l, a.Y / l, a.Z / l) : (0, 0, 1);
    }
}

/// <summary><see cref="ContinentLayout.Generate"/> 的結果。</summary>
/// <param name="Label">每格屬於哪一塊陸地（<see cref="ContinentLayout.ANorth"/> 等），海洋是 <see cref="ContinentLayout.Ocean"/>。</param>
/// <param name="Pacific">每格是不是草圖上的太平洋板塊。</param>
/// <param name="Ridge">每格是不是草圖上的洋脊。</param>
/// <param name="Hinge">A 北與 A 南西側相連處（單位向量）：兩者以它為支點轉開。</param>
/// <param name="RiftGap">
/// 每格是不是 C 與 E 之間的切線本身：起始時是陸地（標成小島群），E 裂開時跟小島一起切出去、在小島群的板塊上沉成海床，
/// 只有圍起來的小島留在水面上（見 <see cref="ContinentLayout.SmallIslands"/>）。
/// </param>
/// <param name="OceanJoin">
/// 每個海格起始時要併進哪塊陸地的板塊（<see cref="ContinentLayout.C"/>：C 的板塊；<see cref="ContinentLayout.E"/>：
/// 起始時也是 C 的板塊，E 裂開時跟著 E 走），其他是 −1。見 ContinentLayout 的 C、E 周圍的海。
/// </param>
public sealed record ContinentLayoutResult(int[] Label, bool[] Pacific, bool[] Ridge, (double X, double Y, double Z) Hinge, bool[] RiftGap, int[] OceanJoin);

/// <summary>大陸配置與劇本的參數。速度都是地表速度（公分／年），方位從北順時針（度），時間都是百萬年。</summary>
/// <param name="RidgeGapKeepShare">
/// A 與 B、C 之間的海保留原本寬度的幾分之幾（2026-10-18 使用者指定：拉近 2/3、剩 1/3）。洋脊不動，
/// 兩側的大陸（連同之後裂出的大島）沿經度往洋脊各移一樣多；大陸跟太平洋之間空出來的那一條歸太平洋。
/// </param>
/// <param name="CEOceanMarginKm">C、E 海岸外多遠以內的海歸 C、E 的板塊（太平洋與兩極除外，見 ContinentLayout 的 OceanMargin）。</param>
/// <param name="SketchWarpDegrees">取樣草圖時位置起伏幾度（海岸才不是像素的鋸齒）。</param>
/// <param name="MaxFilledLakeAreaSquareKm">陸塊內部被包住的水域，小於這個面積的填成陸地。</param>
/// <param name="InitialSeaLevelMeters">
/// 起始海平面（公尺），水量由它反推（見 SimulationEngine 的注水）。洋脊（約 −2,500）要在水下 2 公里以上——
/// 沿用全海洋星球校準的平均海深 1,550 時，放了大陸之後海平面落在 −3,293 m、洋脊整條浮出水面。
/// </param>
/// <param name="AWestSpeed">
/// A 往西的速度（全程）。這顆星球小，3 公分／年走 40 Myr（1,200 公里）就撞上對面。
/// 太平洋固定、不會被吃，A 往西推進多少，西岸就被吃掉多寬：原本 1.5（40 Myr 吃掉 600 公里寬的一條，跟 C 加起來陸地少了約兩成、
/// 被吃掉的地殼堆成山讓大陸又小又厚、浮得更高），2026-10-19 使用者選擇放慢成 0.5。
/// </param>
/// <param name="AOpeningSpeed">A 南半繞西側支點轉開的速度（離支點約 30° 處）。</param>
/// <param name="ASouthEastSpeed">
/// A 南再沿著跟 A 北的交界（大致東西向）往東滑的速度。繞西側支點轉開時，支點以南的部分會往西走，
/// A 南西岸因此以 0.55 公分／年撞進太平洋（A 北只有 0.37），40 Myr 被吃掉約 22%。
/// 2026-10-19 使用者要求減少 A 南被吃掉的量：往東滑抵銷轉動帶來的西推；沿著交界滑動，兩塊仍然相連。
/// </param>
/// <param name="DRiftStart">D 開始裂開。</param>
/// <param name="DRiftEnd">D 不再被推開。</param>
/// <param name="DRiftSpeed">D 離開 A 南半的速度。</param>
/// <param name="BBearing">B 前進的方向（往東北撞 C）。</param>
/// <param name="BApproachSpeed">B 撞上 C 之前的速度。</param>
/// <param name="BPushSpeed">B 撞上 C 之後繼續推的速度（推到劇本結束，高山才不會被重力擴展削平）。</param>
/// <param name="FRiftStart">F 開始裂開。</param>
/// <param name="FRiftEnd">F 不再被推開。</param>
/// <param name="FRiftSpeed">F 離開 B 的速度。</param>
/// <param name="CBearing">C 前進的方向（往東北擠向太平洋）。</param>
/// <param name="CSpeed">C 的速度（全程）。跟 A 同樣的理由，2026-10-19 從 1 放慢成 0.5。</param>
/// <param name="ERiftStart">E 開始裂開。</param>
/// <param name="ERiftEnd">E 不再被推開。</param>
/// <param name="ERiftSpeed">E 滑出去的速度；小島群用一半，最後落在兩者之間。</param>
/// <param name="EBearing">
/// E 滑出去的方向（從北順時針，度）。2026-10-18 使用者指定：先往正南、貼著太平洋的西緣滑出去（E 的東岸是南北向貼著太平洋，
/// 往東南走會一直擠進固定的太平洋、東岸被吃掉）。
/// </param>
/// <param name="EndMyr">劇本結束（B 焊進 C、A 南焊進 A 北），之後交給動力學、開始檢查停止條件。</param>
public sealed record ContinentLayoutParameters(
    double RidgeGapKeepShare,
    double CEOceanMarginKm,
    double SketchWarpDegrees,
    double MaxFilledLakeAreaSquareKm,
    float InitialSeaLevelMeters,
    double AWestSpeed,
    double AOpeningSpeed,
    double ASouthEastSpeed,
    double DRiftStart,
    double DRiftEnd,
    double DRiftSpeed,
    double BBearing,
    double BApproachSpeed,
    double BPushSpeed,
    double FRiftStart,
    double FRiftEnd,
    double FRiftSpeed,
    double CBearing,
    double CSpeed,
    double ERiftStart,
    double ERiftEnd,
    double ERiftSpeed,
    double EBearing,
    double EndMyr)
{
    /// <summary>預設參數（2026-10-18，照使用者的草圖與說明，第一版）。</summary>
    public static ContinentLayoutParameters Default { get; } = new(
        RidgeGapKeepShare: 1.0 / 3.0,
        CEOceanMarginKm: 300,
        SketchWarpDegrees: 0.8,
        MaxFilledLakeAreaSquareKm: 50_000,
        InitialSeaLevelMeters: -200f,
        AWestSpeed: 0.5,
        AOpeningSpeed: 0.5,
        ASouthEastSpeed: 0,
        DRiftStart: 10,
        DRiftEnd: 30,
        DRiftSpeed: 2,
        BBearing: 45,
        BApproachSpeed: 3,
        BPushSpeed: 2,
        FRiftStart: 10,
        FRiftEnd: 30,
        FRiftSpeed: 2,
        CBearing: 45,
        CSpeed: 0.5,
        ERiftStart: 15,
        ERiftEnd: 35,
        ERiftSpeed: 3,
        EBearing: 180,
        EndMyr: 40);

    /// <summary>填湖門檻換算成格數。</summary>
    internal int MaxFilledLakeCells(SimulationGrid grid) =>
        (int)(MaxFilledLakeAreaSquareKm * 1_000_000.0 / grid.AverageCellAreaSquareMeters);
}
