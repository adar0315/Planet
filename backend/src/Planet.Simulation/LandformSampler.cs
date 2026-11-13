using Planet.Domain;

namespace Planet.Simulation;

/// <summary>任意一點的地貌取樣結果（<see cref="LandformSampler.Sample"/>）。高度都是相對海平面的公尺數。</summary>
/// <param name="Terrain">地表覆蓋類型。</param>
/// <param name="HeightCategory">覆蓋物的高度／水深分級（意義同 <see cref="Tile.HeightCategory"/>）。</param>
/// <param name="ElevationMeters">地表（水下是水底）的高度。</param>
/// <param name="WaterSurfaceMeters">水面高度：海是 0，湖是湖面，河是河面；陸地是 null。</param>
/// <param name="SourceCellId">最近的粗格。</param>
/// <param name="RiverCellId">最近的那條河是從哪一格流出來的（查每月流量用）；附近沒有河是 −1。</param>
/// <param name="RiverDistanceMeters">到最近那條河中心線的距離（附近沒有河是無限大）。</param>
/// <param name="RiverWidthMeters">那條河的平均河寬。</param>
/// <param name="Floodplain">在泛濫平原上（豐水期會淹水）。</param>
public readonly record struct LandformSample(
    TerrainType Terrain, int HeightCategory, double ElevationMeters, double? WaterSurfaceMeters,
    int SourceCellId, int RiverCellId, double RiverDistanceMeters, double RiverWidthMeters, bool Floodplain);

/// <summary>
/// 地貌取樣器（水文地貌規劃 H6，2026-10-24）：從模擬粗格（約 15 公里一格）取樣球面上任一點，畫出比一格小的地貌——
/// 彎曲的河道、河谷的斷面、泛濫平原、三角洲、沖積扇、海崖與沙灘、黃土、沙丘、裸露的岩石。
///
/// 模擬負責「哪裡、多少」（下切速率、堆積速率、三角洲供砂、岩性、受浪程度、風蝕與黃土），
/// 這裡只依那些量畫出「長什麼樣子」。放大成 2,000 萬地塊（每格中心取樣一次）與近看細分的小六角（每個小格中心）
/// 都走這一支，所以兩邊一定一致。**完全確定**：同一點永遠回傳同樣的結果（曲流的相位由格子編號雜湊決定）。
///
/// ## 高度先內插、再刻地貌
///
/// 底是 <see cref="SimulatedTerrainSampler"/> 原本的重心內插。河谷是往下刻：地表高度取「內插高度」與「河谷斷面」的較小者，
/// 所以河谷不會讓地形變高；平原是往下壓平到河面附近。海陸仍然照最近的粗格（放大的海岸線不會跟模擬的不一樣）。
/// </summary>
public sealed class LandformSampler
{
    /// <summary>河谷的深度＝下切速率 × 這麼多百萬年（河流維持在同一個位置切了多久的量級）。</summary>
    private const double ValleyIncisionMyr = 2.0;

    /// <summary>河谷最深（公尺）。科羅拉多大峽谷約 1.8 公里、雅魯藏布大峽谷約 5 公里，取 1.5 公里（畫面上還有 8 倍誇張）。</summary>
    private const double MaxValleyDepthMeters = 1_500;

    /// <summary>河谷最多刻到格子高出海面的這個比例（低平原上的河不會刻出比地面還深的谷）。</summary>
    private const double MaxValleyDepthShareOfHeight = 0.7;

    /// <summary>泛濫平原的寬度是河寬的幾倍（地球上常見 10~20 倍），再加上這個最小值。</summary>
    private const double FloodplainWidthPerChannelWidth = 15;

    /// <summary>泛濫平原最窄（公尺，單側）。</summary>
    private const double MinFloodplainHalfWidthMeters = 300;

    /// <summary>泛濫平原比河面高多少（公尺）：平常是乾的，洪峰時淹水。</summary>
    private const double FloodplainAboveWaterMeters = 3;

    /// <summary>冰蝕 U 形谷的谷底半寬（公尺）。</summary>
    private const double GlacialFloorHalfWidthMeters = 600;

    /// <summary>河深（公尺）＝ 河寬 × 這個比例，最少 1 公尺（河的寬深比約 30~100）。</summary>
    private const double DepthPerWidth = 0.02;

    /// <summary>曲流的振幅最多是河寬的幾倍（地球上約 2~3 倍；第一版 8 倍，畫出來像彈簧）。</summary>
    private const double MeanderAmplitudePerWidth = 2.5;

    /// <summary>曲流的波長是河寬的幾倍（地球上約 10~14 倍）。</summary>
    private const double MeanderWavelengthPerWidth = 12;

    /// <summary>坡度大於這個值的河段不曲流（山區的河受岩壁控制），改成沿著谷擺動（見 <see cref="ValleyWiggleMeters"/>）。</summary>
    private const double StraightRiverSlope = 2e-3;

    /// <summary>
    /// 山區河谷的擺動振幅（公尺）：谷在山嘴之間左右繞（交錯山嘴），不是一刀直劃。
    /// 2026-10-26 使用者回報山區小河的谷「像一道道直刮痕」——原本坡度一陡曲流就整個關掉，谷是格心到格心的直線。
    /// </summary>
    private const double ValleyWiggleMeters = 600;

    /// <summary>山區河谷擺動的半波長（公尺）。</summary>
    private const double ValleyWiggleHalfWaveMeters = 3_000;

    /// <summary>
    /// 河谷要多寬的河才刻得到 <see cref="MaxValleyDepthMeters"/>：谷深上限 ∝ √(河寬 ÷ 這個值)。
    /// 10 公尺的小溪約 240 公尺、100 公尺的河約 750 公尺（原本小溪也刻 1.5 公里，畫面上 8 倍後像刀痕）。
    /// </summary>
    private const double ValleyDepthFullWidthMeters = 400;

    /// <summary>谷肩的圓滑寬度（公尺）＝谷深 × 這個比例，最多 <see cref="MaxValleyShoulderMeters"/>：谷壁跟原本的地面不再是一條硬折線。</summary>
    private const double ValleyShoulderShare = 0.3;

    /// <summary>谷肩圓滑寬度的上限（公尺）。</summary>
    private const double MaxValleyShoulderMeters = 250;

    /// <summary>
    /// 河段的下游地表比上游高出這麼多（公尺）以上，就不算連通：河在這裡結束（內流河）。
    /// 模擬的流向會穿過乾涸的內流盆地：盆底低於溢出口幾千公尺，水根本沒有填滿，流向卻還是接到溢出口（2026-11-07 量到翻過 3 公里高的山脊）。
    /// 比這小的高差是粗格的誤差，刻一段峽谷穿過去。
    /// </summary>
    private const double MaxRiverRiseMeters = 200;

    /// <summary>河床最小的坡度：上游一定比下游高，至少高這麼多（公尺／公尺）。</summary>
    private const double MinRiverSlope = 1e-4;

    /// <summary>
    /// 小格算「河」的範圍（公尺，離中心線）：跟前端 seasons.ts 的 MIN_VISIBLE_RIVER_HALF_WIDTH_METERS 同一個值，改的話兩邊一起改。
    /// 河比小格窄，離中心線最近的那串小格要畫成河，高度也要是河床高度，不能是谷壁上的高度。
    /// </summary>
    public const double VisibleRiverHalfWidthMeters = 220;

    /// <summary>河谷最遠刻到離河多遠（公尺），也是最近河道搜尋的範圍。</summary>
    private const double ValleyReachMeters = 8_000;

    /// <summary>
    /// 三角洲的半徑（公里）＝ 這個值 × √(供砂 ÷ 1,000)，最多 60 公里，小於 <see cref="MinDeltaRadiusKm"/> 就不畫。
    /// 原本是 15：半徑中位數 5 公里、最大 16 公里，比 15 公里的格距還小，地圖上幾乎看不到（2026-11-07 使用者回報）。
    /// </summary>
    private const double DeltaRadiusKmPerSqrtSupply = 30;

    /// <summary>三角洲半徑小於這個值（公里）就不畫。</summary>
    private const double MinDeltaRadiusKm = 4;

    /// <summary>扇面的頂點在海岸往內陸半徑的這個比例：扇面大部分（其餘的 70%）往海裡長出去，三角洲是填海長出來的陸地。</summary>
    private const double DeltaInlandShare = 0.3;

    /// <summary>
    /// 三角洲的根：頂點周圍這麼大（頂點到海岸的距離＋這個值，公尺）以內的海都填成三角洲。
    /// 小格的海岸線會被雜訊扭曲、而且跟著平滑後的等高線走（SimulatedTerrainSampler），可以比粗格的分界往內陸好幾公里；
    /// 扇面只從頂點往海裡長，海岸線跑到頂點後面時，三角洲跟陸地之間會夾一條海，變成離岸的島（2026-11-08 使用者回報，
    /// 實測一個半徑 5.9 公里的三角洲，頂點後面 1 公里還是海）。
    /// </summary>
    private const double DeltaRootMarginMeters = 2_500;

    /// <summary>三角洲最高點（頂點）的高度（公尺），往外降到海平面上 0.5 公尺。</summary>
    private const double DeltaTopMeters = 3.5;

    /// <summary>三角洲只畫在這個高度（公尺）以下的陸地。</summary>
    private const double MaxDeltaLandMeters = 60;

    /// <summary>三角洲分流的數目。</summary>
    private const int DeltaDistributaries = 5;

    /// <summary>三角洲輪廓起伏的波長（公尺）。</summary>
    private const double DeltaLobeWavelengthMeters = 8_000;

    /// <summary>三角洲輪廓起伏用的 noise（固定種子，同一顆星球每次畫出一樣的三角洲）。</summary>
    private readonly Planet.Generation.SimplexNoise3D _deltaNoise = new(2711);

    /// <summary>分流左右蜿蜒的幅度與波長（都是半徑的倍數）。</summary>
    private const double DistributaryWiggleShare = 0.05, DistributaryWavesPerRadius = 0.45;

    /// <summary>三角洲扇面的半張角（度）。</summary>
    private const double DeltaHalfAngleDegrees = 70;

    /// <summary>沙灘、海崖帶的寬度（公尺）。</summary>
    private const double ShoreBandMeters = 600;

    /// <summary>海崖的高度（公尺），乘上受浪程度。</summary>
    private const double CliffHeightMeters = 120;

    /// <summary>受浪程度超過這個值、又是硬岩的海岸是海崖。</summary>
    private const float CliffExposure = 0.35f;

    /// <summary>沙丘的波長（公尺）與高度（公尺）。</summary>
    private const double DuneWavelengthMeters = 2_000, DuneHeightMeters = 40;

    /// <summary>黃土堆積速率超過這個值（公尺／百萬年）的草原、灌木就是黃土地（沙漠裡落的塵土會再被吹走，不算）。</summary>
    private const float LoessMinRate = 2f;

    /// <summary>格子比最低的鄰居高出這麼多（公尺）、又是硬岩，就是裸露的岩地。</summary>
    private const float RockReliefMeters = 600;

    /// <summary>模擬網格（粗格）。</summary>
    private readonly SimulationGrid _grid;
    /// <summary>放大時分類好的粗格狀態（河寬、岩性、三角洲供砂等）。</summary>
    private readonly IReadOnlyList<PlanetCell> _cells;
    /// <summary>星球半徑（公尺），用來把單位球距離換成公尺。</summary>
    private readonly double _radius;

    // 河道曲線：每一條河段（粗格 → 下游粗格）一條折線，單位球上的點
    /// <summary>每條河段在 <see cref="_curvePoints"/> 中的起點（以點為單位，CSR）。</summary>
    private readonly int[] _curveStart;
    /// <summary>所有河段曲線的點，依序 (x, y, z) 攤平存放。</summary>
    private readonly double[] _curvePoints;
    /// <summary>這條曲線離弦最遠多少（單位球），用來快速略過。</summary>
    private readonly double[] _curveReach;
    /// <summary>每格河谷底的高度（公尺）</summary>
    private readonly float[] _valleyFloor;
    /// <summary>所有三角洲。</summary>
    private readonly Delta[] _deltas;
    /// <summary>每格：扇面可能蓋到這格的三角洲（_deltas 的索引），沒有是 null。</summary>
    private readonly int[]?[] _deltasNear;

    /// <summary>一個三角洲：河口那一格海、流進來的河從哪一格來、扇面頂點、軸向（往海）、側向、半徑（公尺）。</summary>
    private readonly record struct Delta(int Mouth, int ApexCell,
        (double X, double Y, double Z) Apex, (double X, double Y, double Z) Axis, (double X, double Y, double Z) Side, double Radius);
    /// <summary>河的源頭：沒有上游河段流進來（谷從這裡由淺漸深）</summary>
    private readonly bool[] _riverHead;
    /// <summary>每格的下游粗格（−1 沒有）：RiverDownstreamCellId 去掉不連通的河段（見 MaxRiverRiseMeters）</summary>
    private readonly int[] _down;

    /// <summary>建立取樣器。</summary>
    /// <param name="grid">模擬網格。</param>
    /// <param name="cells">每一格的記錄（依 <see cref="PlanetCell.CellId"/> 排序、從 0 連續）。</param>
    public LandformSampler(SimulationGrid grid, IReadOnlyList<PlanetCell> cells)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(cells);
        _grid = grid;
        _cells = cells;
        _radius = grid.PlanetRadiusMeters;
        var n = grid.CellCount;

        // 河段：下游高太多的不算連通（河在那裡結束）。低於海平面的乾陸地畫出來是 0 公尺。
        // 冰河上沒有河：河從冰河末端開始，流到冰河就結束（河道高度跟冰面高度無關，接在一起會一下跳幾公里）
        _down = new int[n];
        for (var c = 0; c < n; c++)
        {
            var down = cells[c].RiverDownstreamCellId;
            var connected = down >= 0 && cells[c].Terrain != TerrainType.Glacier && cells[down].Terrain != TerrainType.Glacier
                && (cells[down].WaterDepthMeters > 0f
                    || Math.Max(0, cells[down].SurfaceElevationMeters) - Math.Max(0, cells[c].SurfaceElevationMeters) <= MaxRiverRiseMeters);
            _down[c] = connected ? down : -1;
        }

        // 河谷底：格子高度減掉谷深，再整理成一路往下游降（見 MakeValleyFloorsDescend）
        _valleyFloor = new float[n];
        for (var c = 0; c < n; c++)
        {
            var cell = cells[c];
            var height = Math.Max(0, cell.SurfaceElevationMeters);
            var depthCap = MaxValleyDepthMeters * Math.Sqrt(Math.Min(1, Math.Max(cell.RiverWidthMeters, Rivers.MinChannelWidthMeters / 2) / ValleyDepthFullWidthMeters));
            var depth = Math.Min(depthCap, Math.Max(cell.IncisionRate, cell.GlacialIncisionRate) * ValleyIncisionMyr);
            depth = Math.Min(depth, height * MaxValleyDepthShareOfHeight);
            _valleyFloor[c] = (float)Math.Max(0, cell.SurfaceElevationMeters - depth);
        }
        MakeValleyFloorsDescend();

        // 源頭：沒有任何河段流進來的河
        _riverHead = new bool[n];
        var hasUpstream = new bool[n];
        for (var c = 0; c < n; c++)
            if (_down[c] >= 0) hasUpstream[_down[c]] = true;
        for (var c = 0; c < n; c++) _riverHead[c] = _down[c] >= 0 && !hasUpstream[c];

        // 河道曲線
        _curveStart = new int[n + 1];
        var points = new List<double>();
        var reach = new double[n];
        Span<int> neighbors = stackalloc int[SimulationGrid.MaxNeighborsPerCell];
        for (var c = 0; c < n; c++)
        {
            _curveStart[c] = points.Count / 3;
            var down = _down[c];
            if (down < 0) continue;
            reach[c] = BuildCurve(c, down, points, neighbors);
        }
        _curveStart[n] = points.Count / 3;
        _curvePoints = [.. points];
        _curveReach = reach;

        // 三角洲：大河流進海的那一格海，記下是哪一格流進來的（同一格海有好幾條河流進來時取最寬的）
        var apexOf = new int[n];
        Array.Fill(apexOf, -1);
        for (var c = 0; c < n; c++)
        {
            var down = _down[c];
            if (down < 0 || cells[down].WaterDepthMeters <= 0f || !cells[down].IsOcean) continue;
            if (DeltaRadiusMeters(down) <= 0) continue;
            if (apexOf[down] < 0 || cells[c].RiverWidthMeters > cells[apexOf[down]].RiverWidthMeters) apexOf[down] = c;
        }
        (_deltas, _deltasNear) = BuildDeltas(apexOf);
    }

    /// <summary>
    /// 三角洲的扇面：頂點在河口海岸（河那格與海那格的中間）往內陸一點，扇面往海裡長出去。
    /// 再記下每格附近有哪些三角洲——扇面可以比格距大好幾倍，只看最近格與鄰居找不到。
    /// </summary>
    private (Delta[] Deltas, int[]?[] Near) BuildDeltas(int[] apexOf)
    {
        var u = _grid.UnitVector;
        var n = _cells.Count;
        var deltas = new List<Delta>();
        var near = new List<int>?[n];
        var margin = 2 * _grid.AverageCellSpacingMeters;
        Span<int> neighbors = stackalloc int[SimulationGrid.MaxNeighborsPerCell];
        for (var mouth = 0; mouth < n; mouth++)
        {
            var apexCell = apexOf[mouth];
            if (apexCell < 0) continue;
            var radius = DeltaRadiusMeters(mouth);
            var land = (u[apexCell * 3], u[apexCell * 3 + 1], u[apexCell * 3 + 2]);
            var sea = (u[mouth * 3], u[mouth * 3 + 1], u[mouth * 3 + 2]);
            var coast = Normalize(Add(land, sea));
            var axis = Normalize(Sub(sea, land));
            var apex = Normalize(Sub(coast, Scale(axis, DeltaInlandShare * radius / _radius)));
            var index = deltas.Count;
            deltas.Add(new Delta(mouth, apexCell, apex, axis, Normalize(Cross(apex, axis)), radius));

            // 從河口那格往外找，格心離頂點在半徑＋兩個格距以內的格子都記下來
            var seen = new HashSet<int> { mouth };
            var queue = new Queue<int>();
            queue.Enqueue(mouth);
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                (near[c] ??= []).Add(index);
                var count = _grid.GetNeighbors(c, neighbors);
                for (var i = 0; i < count; i++)
                {
                    var k = neighbors[i];
                    if (!seen.Add(k)) continue;
                    if (Length(Sub((u[k * 3], u[k * 3 + 1], u[k * 3 + 2]), apex)) * _radius <= radius + margin) queue.Enqueue(k);
                }
            }
        }
        return ([.. deltas], near.Select(l => l?.ToArray()).ToArray());
    }

    /// <summary>點在不在某個三角洲的扇面裡。在的話回傳離頂點多遠（公尺）、沿軸與側向的分量（單位球）、輪廓雜訊。</summary>
    private bool InFan((double, double, double) p, in Delta delta, out double distance, out double along, out double across, out double lobe)
    {
        var dir = Sub(p, delta.Apex);
        distance = Length(dir) * _radius;
        along = Dot(dir, delta.Axis);
        across = Dot(dir, delta.Side);
        lobe = 0;
        // 先用雜訊的上限排除遠處（雜訊最貴）
        if (distance > delta.Radius * (0.8 + 0.35 * 1.5) || distance < 1) return false;
        // 邊緣用雜訊打散：三角洲的輪廓是一個個朵狀的沙洲，不是圓規畫的扇形
        var f = _radius / DeltaLobeWavelengthMeters;
        lobe = _deltaNoise.Fbm(p.Item1 * f, p.Item2 * f, p.Item3 * f, 3, 1.0);
        if (distance > delta.Radius * (0.8 + 0.35 * lobe)) return false;
        var angle = Math.Atan2(across, along) * 180 / Math.PI;
        return Math.Abs(angle) <= DeltaHalfAngleDegrees * (0.85 + 0.3 * lobe);
    }

    /// <summary>點在不在任何三角洲的扇面或根（見 <see cref="DeltaRootMarginMeters"/>）裡：這些地方的海填成三角洲。</summary>
    private bool InAnyFan((double, double, double) p, int nearest)
    {
        if (_deltasNear[nearest] is not { } list) return false;
        foreach (var d in list)
        {
            var delta = _deltas[d];
            if (InFan(p, delta, out _, out _, out _, out _)) return true;
            if (Length(Sub(p, delta.Apex)) * _radius <= DeltaInlandShare * delta.Radius + DeltaRootMarginMeters) return true;
        }
        return false;
    }

    /// <summary>三角洲的半徑（公尺）；太小不畫是 0。</summary>
    private double DeltaRadiusMeters(int mouth)
    {
        var supply = _cells[mouth].DeltaSupply;
        if (supply <= 0f) return 0;
        var km = Math.Min(60, DeltaRadiusKmPerSqrtSupply * Math.Sqrt(supply / 1_000.0));
        return km < MinDeltaRadiusKm ? 0 : km * 1000;
    }

    /// <summary>
    /// 一條河段的曲線：Catmull-Rom 樣條通過「上游主流 → 這格 → 下游 → 再下游」的格心（不再是 60° 的折角），
    /// 再疊上曲流——平原上蜿蜒、山區幾乎是直的。曲流取整數個半波，兩端的偏移都是 0，前後兩段在格心接得起來。
    /// 回傳曲線離弦最遠多少（單位球）。
    /// </summary>
    private double BuildCurve(int c, int down, List<double> points, Span<int> neighbors)
    {
        var u = _grid.UnitVector;
        // 上游主流：流進這格、河最寬的那一格；沒有就延長
        var count = _grid.GetNeighbors(c, neighbors);
        var upstream = -1;
        for (var k = 0; k < count; k++)
        {
            var nb = neighbors[k];
            if (_down[nb] != c) continue;
            if (upstream < 0 || _cells[nb].RiverWidthMeters > _cells[upstream].RiverWidthMeters) upstream = nb;
        }
        var next = _down[down];

        var p1 = (u[c * 3], u[c * 3 + 1], u[c * 3 + 2]);
        var p2 = (u[down * 3], u[down * 3 + 1], u[down * 3 + 2]);
        var p0 = upstream >= 0 ? (u[upstream * 3], u[upstream * 3 + 1], u[upstream * 3 + 2]) : Extrapolate(p2, p1);
        var p3 = next >= 0 ? (u[next * 3], u[next * 3 + 1], u[next * 3 + 2]) : Extrapolate(p1, p2);

        var chord = Length(Sub(p2, p1));
        var chordMeters = chord * _radius;
        var width = Math.Max(_cells[c].RiverWidthMeters, (float)Rivers.MinChannelWidthMeters);
        var slope = chordMeters > 0 ? Math.Max(0, _cells[c].SurfaceElevationMeters - _cells[down].SurfaceElevationMeters) / chordMeters : 0;
        var sinuosity = Math.Clamp(1 - slope / StraightRiverSlope, 0, 1);
        var halfWaves = Math.Max(1, (int)Math.Round(chordMeters / (MeanderWavelengthPerWidth * width / 2)));
        var amplitude = sinuosity * Math.Min(0.15 * chordMeters, MeanderAmplitudePerWidth * width) / _radius;
        var phase = (Hash(c) & 1) == 0 ? 1.0 : -1.0;
        // 曲流不是正弦波：振幅沿河忽大忽小（調變的兩端也是 0，前後兩段照樣接得起來）
        var modulationWaves = 1 + (int)(Hash(c * 7 + 3) % 3);
        var modulationPhase = (Hash(c * 13 + 5) % 1000) / 1000.0 * 2 * Math.PI;
        // 沿谷不規則地擺動（兩個整數半波疊加，兩端仍是 0）
        // 平地的小河曲流振幅只有幾十公尺，15 公里的河段看起來仍是直線，所以平地也留一半的擺動
        var wiggle = (1 - 0.5 * sinuosity) * Math.Min(0.08 * chordMeters, ValleyWiggleMeters) / _radius;
        var wiggleWaves = Math.Max(2, (int)Math.Round(chordMeters / ValleyWiggleHalfWaveMeters));
        var wiggleWaves2 = wiggleWaves + 1; // 頻率相近的兩個波：拍頻讓擺動忽大忽小，不是規則的鋸齒
        var wigglePhase = (Hash(c * 17 + 11) & 1) == 0 ? 1.0 : -1.0;
        var samples = Math.Clamp(Math.Max(halfWaves * 6, wiggleWaves2 * 10), 12, 240);

        var farthest = 0.0;
        for (var i = 0; i <= samples; i++)
        {
            var t = i / (double)samples;
            var p = CatmullRom(p0, p1, p2, p3, t);
            var tangent = Sub(CatmullRom(p0, p1, p2, p3, Math.Min(1, t + 1e-3)), CatmullRom(p0, p1, p2, p3, Math.Max(0, t - 1e-3)));
            var normal = Normalize(Cross(tangent, p));
            var modulation = 0.55 + 0.45 * Math.Sin(2 * Math.PI * modulationWaves * t + modulationPhase);
            var offset = amplitude * phase * Math.Sin(halfWaves * Math.PI * t) * modulation
                + wiggle * wigglePhase * (0.6 * Math.Sin(wiggleWaves * Math.PI * t) + 0.5 * Math.Sin(wiggleWaves2 * Math.PI * t));
            p = Normalize(Add(p, Scale(normal, offset)));
            points.Add(p.Item1); points.Add(p.Item2); points.Add(p.Item3);
            farthest = Math.Max(farthest, DistanceToChord(p, p1, p2));
        }
        return farthest;
    }

    /// <summary>一條河段（從 <paramref name="cell"/> 流出）的河道中心線，由上游往下游，經緯度（度）。沒有河是空的。</summary>
    /// <param name="cell">河段的起點粗格。</param>
    public IEnumerable<(double Latitude, double Longitude)> RiverCenterline(int cell)
    {
        for (var s = _curveStart[cell]; s < _curveStart[cell + 1]; s++)
        {
            var z = Math.Clamp(_curvePoints[s * 3 + 2], -1, 1);
            yield return (Math.Asin(z) * 180 / Math.PI, Math.Atan2(_curvePoints[s * 3 + 1], _curvePoints[s * 3]) * 180 / Math.PI);
        }
    }

    /// <summary>取樣某個經緯度（度）。</summary>
    /// <param name="latitude">緯度。</param>
    /// <param name="longitude">經度。</param>
    /// <param name="nearest">最近的粗格（已經找好的話傳進來，省一次搜尋）。</param>
    /// <param name="interpolatedElevation">內插的高度（已經算好的話）。</param>
    /// <param name="coastProximity">
    /// 附近的海佔多少（0~1，平滑核加權，見 <see cref="SimulatedTerrainSampler.SampleDetailed"/>）。給了就用它判斷沙灘、海崖帶，
    /// 沒給就用粗格之間的 Voronoi 分界（海岸線是格子的邊）。
    /// </param>
    /// <param name="riverBandMeters">離河道中心線這麼遠以內，高度畫成河床（見 <see cref="VisibleRiverHalfWidthMeters"/>）。</param>
    /// <param name="riverBandAddsToChannel">true：範圍是半個河寬再加上 <paramref name="riverBandMeters"/>（地塊：範圍裡有河就整格是河）；false：取兩者較大的（小格）。</param>
    public LandformSample Sample(double latitude, double longitude, int nearest = -1, double? interpolatedElevation = null, double? coastProximity = null,
        double riverBandMeters = VisibleRiverHalfWidthMeters, bool riverBandAddsToChannel = false)
    {
        var lat = latitude * Math.PI / 180.0;
        var lon = longitude * Math.PI / 180.0;
        var x = Math.Cos(lat) * Math.Cos(lon);
        var y = Math.Cos(lat) * Math.Sin(lon);
        var z = Math.Sin(lat);
        if (nearest < 0) nearest = _grid.FindNearestCell(x, y, z);
        var cell = _cells[nearest];
        var elevation = interpolatedElevation ?? cell.SurfaceElevationMeters;
        var p = (x, y, z);

        Span<int> neighbors = stackalloc int[SimulationGrid.MaxNeighborsPerCell];
        var count = _grid.GetNeighbors(nearest, neighbors);

        // ---- 水（三角洲往海裡長出去的部分是陸地）----
        var seaDelta = cell.WaterDepthMeters > 0f && cell.IsOcean && InAnyFan(p, nearest);
        if (cell.WaterDepthMeters > 0f && !seaDelta)
        {
            var surface = (double)cell.WaterSurfaceMeters;
            elevation = Math.Min(elevation, surface - 1);
            return new LandformSample(cell.Terrain, cell.HeightCategory, elevation, surface, nearest, -1, double.PositiveInfinity, 0, false);
        }

        var terrain = seaDelta ? TerrainType.Swamp : cell.Terrain;
        var category = seaDelta ? 0 : cell.HeightCategory;
        elevation = seaDelta ? DeltaTopMeters : Math.Max(elevation, 0); // 下面的三角洲再往外壓低

        // ---- 河：找最近的河道 ----
        var (riverCell, riverT, riverDist) = NearestRiver(p, nearest, neighbors[..count]);
        double? water = null;
        var floodplain = false;
        double riverWidth = 0;
        var onRiver = false;
        if (riverCell >= 0)
        {
            var source = _cells[riverCell];
            riverWidth = source.RiverWidthMeters;
            var floor = ValleyFloorAt(riverCell, riverT);
            var depth = Math.Max(1, riverWidth * DepthPerWidth);
            var halfWidth = riverWidth / 2;
            var aggrading = FluvialErosion.IsAggrading(source.AggradationRate, source.IncisionRate, source.GlacialIncisionRate);

            if (riverDist <= halfWidth)
            {
                // 河道本身。高度直接等於河床（不取較小值）：細部起伏的凹陷也不能讓河忽高忽低，河一定一路往下游降
                water = floor;
                elevation = floor - depth;
                terrain = TerrainType.Freshwater;
                category = WaterDepthCategory(depth);
                onRiver = true;
            }
            else if (riverDist <= (riverBandAddsToChannel ? halfWidth + riverBandMeters : Math.Max(halfWidth, riverBandMeters)))
            {
                // 比河道寬、但畫出來算河的範圍（小格、拉遠的地塊）：地形不變（前端依季節決定是水還是乾河床），高度是河床
                elevation = floor - depth;
                floodplain = aggrading;
                onRiver = true;
            }
            else if (aggrading)
            {
                // 泛濫平原：河兩側壓平到河面上幾公尺
                var plainHalf = Math.Max(MinFloodplainHalfWidthMeters, FloodplainWidthPerChannelWidth * riverWidth / 2);
                var plain = floor + FloodplainAboveWaterMeters;
                if (riverDist <= plainHalf)
                {
                    elevation = Math.Min(elevation, plain);
                    floodplain = true;
                    if (terrain is TerrainType.Forest && cell.Precipitation > 0) { /* 濕潤的泛濫平原保留森林（河畔林） */ }
                }
                else
                {
                    elevation = Math.Min(elevation, plain + (riverDist - plainHalf) * Math.Tan(5 * Math.PI / 180));
                }
            }
            else
            {
                // 河谷：V 形（或冰蝕的 U 形），谷壁陡度看岩性。
                // 斷面取附近每一小段河道各自斷面的最小值——只用最近那一段的話，河彎內側「最近的一段」會突然換成另一段，
                // 谷底高度跟著跳一階，從河彎放射出一道道直線的陡坎（2026-10-26 近看時的刮痕）。連續函數的最小值仍然連續。
                var (profile, wallSlope) = ValleyProfile(p, nearest, neighbors[..count]);
                // 谷肩圓滑：斷面跟原本的地面取「平滑的較小值」，交界不是一條硬折線
                var shoulder = Math.Min(MaxValleyShoulderMeters, Math.Max(1, (elevation - floor) * ValleyShoulderShare));
                var carved = SmoothMin(elevation, profile, shoulder);
                if (carved < elevation)
                {
                    // 硬岩的陡谷壁露出岩石
                    if (wallSlope > 0.6 && elevation - carved > 50 && terrain is not (TerrainType.Forest or TerrainType.SnowField))
                        terrain = TerrainType.Rock;
                    elevation = carved;
                }
            }
        }

        // ---- 三角洲：大河流進海的那一格附近。只蓋低地：扇面畫在高地上會把山壓平，河沿線一下跳上千公尺 ----
        var onDelta = false;
        if (water is null && !onRiver && elevation <= MaxDeltaLandMeters && _deltasNear[nearest] is { } deltas)
        {
            var distributaryHalfWidth = riverBandAddsToChannel ? riverBandMeters / 2 : riverBandMeters;
            foreach (var d in deltas)
                if (onDelta = PaintDelta(p, _deltas[d], distributaryHalfWidth, ref terrain, ref category, ref elevation, ref water)) break;
        }

        // ---- 海岸：沙灘、礫灘、海崖（冰河、雪地的海岸不畫）----
        if (water is null && !onRiver && !onDelta && !seaDelta && terrain is not (TerrainType.Glacier or TerrainType.SnowField))
        {
            // 海岸帶：平滑後的海陸比例換成「離海多遠」的近似（附近一半是海＝就在海邊）
            var coast = coastProximity is { } proximity
                ? proximity > 0.02 ? ShoreBandMeters * Math.Max(0, 1 - proximity * 2) : double.PositiveInfinity
                : DistanceToSea(p, nearest, neighbors[..count]);
            if (coast < ShoreBandMeters)
            {
                var rock = (ExposedRock)cell.ExposedRock;
                var hard = rock is ExposedRock.Crystalline or ExposedRock.Volcanic or ExposedRock.OceanicBasalt;
                if (hard && cell.WaveExposure >= CliffExposure)
                {
                    terrain = TerrainType.Cliff;
                    category = 0;
                    elevation = Math.Max(elevation, CliffHeightMeters * cell.WaveExposure);
                }
                else if (terrain is not (TerrainType.Swamp or TerrainType.Mud))
                {
                    terrain = rock is ExposedRock.Volcanic or ExposedRock.Melange || cell.WaveExposure > 0.6f ? TerrainType.Gravel : TerrainType.Sand;
                    category = 0;
                    elevation = Math.Min(elevation, 2 + coast / ShoreBandMeters * 4);
                }
            }
        }

        // ---- 風：黃土、沙丘；沖積扇；裸露的岩石 ----
        if (water is null && !floodplain && !onRiver && !onDelta && !seaDelta)
        {
            if (cell.LoessRate >= LoessMinRate && terrain is TerrainType.Grassland or TerrainType.Shrubland)
            {
                terrain = TerrainType.Loess;
                category = 0;
            }
            else if (terrain == TerrainType.Sand && cell.SedimentThicknessMeters > 20f)
            {
                elevation += Dune(p, latitude);
            }
            else if (cell.AggradationRate > 20f && FluvialErosion.IsAggrading(cell.AggradationRate, cell.IncisionRate, cell.GlacialIncisionRate) && Relief(nearest, neighbors[..count]) > 200
                     && terrain is TerrainType.Sand or TerrainType.Shrubland or TerrainType.Grassland)
            {
                terrain = TerrainType.Gravel; // 乾燥區的沖積扇
                category = 0;
            }
            else if (terrain is not (TerrainType.Forest or TerrainType.Glacier or TerrainType.SnowField or TerrainType.Cliff)
                     && (ExposedRock)cell.ExposedRock is ExposedRock.Crystalline or ExposedRock.Volcanic
                     && Relief(nearest, neighbors[..count]) > RockReliefMeters)
            {
                terrain = TerrainType.Rock;
                category = 0;
            }
        }

        return new LandformSample(terrain, category, elevation, water, nearest, riverCell, riverDist, riverWidth, floodplain);
    }

    /// <summary>
    /// 最近的河道：只看最近粗格＋鄰居出發的河段（一條河段從 c 流到 d，點在 c 或 d 附近時，c 一定是最近格或它的鄰居）。
    /// 回傳（河段的起點格、在河段上的位置 0~1、距離公尺）；附近沒有河是 (−1, 0, ∞)。
    /// </summary>
    private (int Cell, double T, double Distance) NearestRiver((double, double, double) p, int nearest, ReadOnlySpan<int> neighbors)
    {
        var best = double.PositiveInfinity;
        var bestCell = -1;
        var bestT = 0.0;
        var u = _grid.UnitVector;
        for (var i = -1; i < neighbors.Length; i++)
        {
            var c = i < 0 ? nearest : neighbors[i];
            var down = _down[c];
            if (down < 0) continue;
            // 快速略過：離弦太遠的不用逐段算
            var chordDist = DistanceToChord(p, (u[c * 3], u[c * 3 + 1], u[c * 3 + 2]), (u[down * 3], u[down * 3 + 1], u[down * 3 + 2]));
            var margin = _curveReach[c] + (_cells[c].RiverWidthMeters * FloodplainWidthPerChannelWidth + ValleyReachMeters) / _radius;
            if (chordDist - margin > best / _radius) continue;

            var from = _curveStart[c];
            var to = _curveStart[c + 1];
            for (var s = from; s < to - 1; s++)
            {
                var a = (_curvePoints[s * 3], _curvePoints[s * 3 + 1], _curvePoints[s * 3 + 2]);
                var b = (_curvePoints[s * 3 + 3], _curvePoints[s * 3 + 4], _curvePoints[s * 3 + 5]);
                var (d, t) = DistanceToSegment(p, a, b);
                var meters = d * _radius;
                if (meters >= best) continue;
                best = meters;
                bestCell = c;
                bestT = (s - from + t) / (to - 1 - from);
            }
        }
        return (bestCell, bestT, best);
    }

    /// <summary>
    /// 某條河段（從 <paramref name="c"/> 流出）在位置 <paramref name="t"/>（0~1）的谷底高度（公尺）。
    /// 源頭那一段谷從 0 漸漸加深（原本在源頭就是全深，四周一圈陡壁，像刀切的楔形）。
    /// 起點用粗格的高度、不用這一點含細部起伏的高度：後者忽高忽低，河就不是一路往下了。
    /// </summary>
    private double ValleyFloorAt(int c, double t)
    {
        var floor = Lerp(_valleyFloor[c], FloorBelow(_down[c], _valleyFloor[c]), t);
        if (!_riverHead[c]) return floor;
        var top = Math.Max(_cells[c].SurfaceElevationMeters, floor);
        return Lerp(top, floor, t * t * (3 - 2 * t));
    }

    /// <summary>河段終點的谷底：陸地是下游的谷底、湖是湖面、海是 0；沒有下游（河在這裡結束）維持 <paramref name="end"/>。</summary>
    private double FloorBelow(int down, double end)
    {
        if (down < 0) return end;
        var cell = _cells[down];
        return cell.WaterDepthMeters <= 0f ? _valleyFloor[down] : cell.IsOcean ? 0 : cell.WaterSurfaceMeters;
    }

    /// <summary>
    /// 把谷底整理成一路往下游降：河的上游一定比下游高。
    /// 先由下游往上游，谷底不低於下游的湖面（流進高山湖的河不能比湖面低）；再由上游往下游，下游的谷底壓到比上游低——
    /// 擋路的小丘就刻出峽谷。兩趟都走拓撲順序（河是樹狀的，支流都處理完才輪到匯流的那一格）。
    /// </summary>
    private void MakeValleyFloorsDescend()
    {
        var n = _down.Length;
        var inflows = new int[n];
        for (var c = 0; c < n; c++)
            if (_down[c] >= 0) inflows[_down[c]]++;
        var order = new List<int>();
        for (var c = 0; c < n; c++)
            if (_down[c] >= 0 && inflows[c] == 0) order.Add(c);
        for (var i = 0; i < order.Count; i++)
        {
            var down = _down[order[i]];
            if (--inflows[down] == 0 && _down[down] >= 0) order.Add(down);
        }

        var lowest = new float[n];
        for (var i = order.Count - 1; i >= 0; i--)
        {
            var c = order[i];
            var below = _cells[_down[c]];
            lowest[c] = below.WaterDepthMeters <= 0f ? lowest[_down[c]] : below.IsOcean ? 0f : below.WaterSurfaceMeters;
            _valleyFloor[c] = Math.Max(_valleyFloor[c], lowest[c]);
        }

        var u = _grid.UnitVector;
        foreach (var c in order)
        {
            var d = _down[c];
            if (_cells[d].WaterDepthMeters > 0f) continue;
            var meters = Length(Sub((u[c * 3], u[c * 3 + 1], u[c * 3 + 2]), (u[d * 3], u[d * 3 + 1], u[d * 3 + 2]))) * _radius;
            _valleyFloor[d] = Math.Max(lowest[d], Math.Min(_valleyFloor[d], _valleyFloor[c] - (float)(meters * MinRiverSlope)));
        }
    }

    /// <summary>谷壁的坡度（tan）：冰蝕谷 40°，其餘看露出的岩性。</summary>
    private double WallSlopeOf(int c)
    {
        var cell = _cells[c];
        var glacial = cell.GlacialIncisionRate > cell.IncisionRate;
        return Math.Tan((glacial ? 40 : (ExposedRock)cell.ExposedRock switch
        {
            ExposedRock.Crystalline or ExposedRock.Volcanic or ExposedRock.OceanicBasalt => 35,
            ExposedRock.Melange => 25,
            ExposedRock.Sedimentary => 20,
            _ => 10,
        }) * Math.PI / 180);
    }

    /// <summary>
    /// 河谷斷面的高度（公尺）：附近每一小段河道（下切為主的河段）各自算「谷底＋離河距離×谷壁坡度」，取最小值。
    /// 回傳（斷面高度、取到最小值那段的谷壁坡度）；附近沒有河谷是 (+∞, 0)。
    /// </summary>
    private (double Profile, double WallSlope) ValleyProfile((double, double, double) p, int nearest, ReadOnlySpan<int> neighbors)
    {
        var best = double.PositiveInfinity;
        var bestSlope = 0.0;
        var u = _grid.UnitVector;
        for (var i = -1; i < neighbors.Length; i++)
        {
            var c = i < 0 ? nearest : neighbors[i];
            var cell = _cells[c];
            var down = _down[c];
            if (down < 0 || FluvialErosion.IsAggrading(cell.AggradationRate, cell.IncisionRate, cell.GlacialIncisionRate)) continue;
            var chordDist = DistanceToChord(p, (u[c * 3], u[c * 3 + 1], u[c * 3 + 2]), (u[down * 3], u[down * 3 + 1], u[down * 3 + 2]));
            if ((chordDist - _curveReach[c]) * _radius > ValleyReachMeters) continue;

            var slope = WallSlopeOf(c);
            var floorHalf = cell.GlacialIncisionRate > cell.IncisionRate ? GlacialFloorHalfWidthMeters : cell.RiverWidthMeters / 2;
            var from = _curveStart[c];
            var to = _curveStart[c + 1];
            for (var s = from; s < to - 1; s++)
            {
                var a = (_curvePoints[s * 3], _curvePoints[s * 3 + 1], _curvePoints[s * 3 + 2]);
                var b = (_curvePoints[s * 3 + 3], _curvePoints[s * 3 + 4], _curvePoints[s * 3 + 5]);
                var (d, tl) = DistanceToSegment(p, a, b);
                var meters = d * _radius;
                if (meters > ValleyReachMeters) continue;
                var t = (s - from + tl) / (to - 1 - from);
                var profile = ValleyFloorAt(c, t) + Math.Max(0, meters - floorHalf) * slope;
                if (profile < best)
                {
                    best = profile;
                    bestSlope = slope;
                }
            }
        }
        return (best, bestSlope);
    }

    /// <summary>
    /// 三角洲：扇面裡的陸地壓成 0.5~3.5 公尺的沼澤與泥灘，分流是淡水。有畫到回傳 true。
    /// 分流至少 <paramref name="minHalfWidth"/> 寬（跟河一樣：比小格窄的話畫不出來）。
    /// </summary>
    private bool PaintDelta((double, double, double) p, in Delta delta, double minHalfWidth,
        ref TerrainType terrain, ref int category, ref double elevation, ref double? water)
    {
        if (!InFan(p, delta, out var distance, out var along, out var across, out var lobe)) return false;

        // 分流：從頂點放射出去的幾條水道，各自稍微偏一點，再左右蜿蜒（直線像車輪的輻條）
        var halfWidth = Math.Max(Math.Max(_cells[delta.ApexCell].RiverWidthMeters * 0.4, Rivers.MinChannelWidthMeters) / 2, minHalfWidth);
        for (var i = 0; i < DeltaDistributaries; i++)
        {
            var spread = (i - (DeltaDistributaries - 1) / 2.0) / ((DeltaDistributaries - 1) / 2.0) * DeltaHalfAngleDegrees * 0.8;
            spread += ((Hash(delta.Mouth * 31 + i) & 15) - 7.5) * 1.5;
            var rad = spread * Math.PI / 180;
            var forward = (Math.Cos(rad) * along + Math.Sin(rad) * across) * _radius;
            var phase = (Hash(delta.Mouth * 57 + i) & 255) / 256.0 * 2 * Math.PI;
            var wave = 2 * Math.PI * forward / (delta.Radius * DistributaryWavesPerRadius);
            var wiggle = DistributaryWiggleShare * delta.Radius * Math.Min(1, forward / (0.25 * delta.Radius))
                * (Math.Sin(wave + phase) + 0.4 * Math.Sin(2.3 * wave + 2 * phase));
            var lateral = Math.Abs((-Math.Sin(rad) * along + Math.Cos(rad) * across) * _radius - wiggle);
            if (forward > 0 && lateral < halfWidth)
            {
                terrain = TerrainType.Freshwater;
                category = WaterDepthCategory(2);
                water = 0.5;
                elevation = Math.Min(elevation, -1.5);
                return true;
            }
        }
        // 分流之間：沼澤（靠內陸）與泥灘（靠海），往海邊越來越低
        var fraction = distance / delta.Radius;
        elevation = Math.Min(elevation, (DeltaTopMeters - 0.5) * (1 - fraction) + 0.5);
        terrain = fraction + 0.3 * lobe > 0.6 ? TerrainType.Mud : TerrainType.Swamp;
        category = 0;
        return true;
    }

    /// <summary>到最近的海的距離（公尺）：最近粗格跟海鄰居之間的 Voronoi 分界線（中垂面）。附近沒有海是無限大。</summary>
    private double DistanceToSea((double, double, double) p, int nearest, ReadOnlySpan<int> neighbors)
    {
        var u = _grid.UnitVector;
        var c = (u[nearest * 3], u[nearest * 3 + 1], u[nearest * 3 + 2]);
        var best = double.PositiveInfinity;
        foreach (var nb in neighbors)
        {
            if (_cells[nb].WaterDepthMeters <= 0f || !_cells[nb].IsOcean) continue;
            var s = (u[nb * 3], u[nb * 3 + 1], u[nb * 3 + 2]);
            var ds = Length(Sub(p, s));
            var dc = Length(Sub(p, c));
            var between = Length(Sub(s, c));
            if (between <= 0) continue;
            // 點到中垂面的距離：(|p−s|² − |p−c|²) / (2|s−c|)
            var d = (ds * ds - dc * dc) / (2 * between) * _radius;
            best = Math.Min(best, Math.Max(0, d));
        }
        return best;
    }

    /// <summary>這格比最低的鄰居高出多少（公尺）：局部起伏。</summary>
    private double Relief(int cell, ReadOnlySpan<int> neighbors)
    {
        var low = double.PositiveInfinity;
        foreach (var nb in neighbors) low = Math.Min(low, _cells[nb].SurfaceElevationMeters);
        return double.IsInfinity(low) ? 0 : _cells[cell].SurfaceElevationMeters - low;
    }

    /// <summary>
    /// 沙丘：垂直於盛行風的一排排沙脊（橫向沙丘）。風向用跟氣候同一套三胞環流的帶狀風（<see cref="Climate.PrevailingWind"/>）。
    /// </summary>
    private double Dune((double X, double Y, double Z) p, double latitude)
    {
        var (east, north) = Climate.PrevailingWind(latitude);
        var eastLength = Math.Sqrt(p.X * p.X + p.Y * p.Y);
        if (eastLength < 1e-9) return 0;
        var e = (-p.Y / eastLength, p.X / eastLength, 0.0);
        var nVec = Cross(p, e);
        var wind = Add(Scale(e, east), Scale(nVec, north));
        var windLength = Length(wind);
        if (windLength < 1e-6) return 0;
        var along = Dot(p, Scale(wind, 1 / windLength)) * _radius;
        var ridge = Math.Sin(2 * Math.PI * along / DuneWavelengthMeters);
        return DuneHeightMeters * Math.Max(0, ridge); // 沙脊之間是平的丘間地
    }

    /// <summary>淡水的水深分級（同 <see cref="TerrainType.Freshwater"/> 的 HeightCategory）。</summary>
    private static int WaterDepthCategory(double depth) => depth switch
    {
        < 0.5 => 0,
        < 0.8 => 1,
        < 1.1 => 2,
        < 1.4 => 3,
        < 2 => 4,
        < 5 => 5,
        _ => 6,
    };

    /// <summary>整數雜湊（splitmix 風格的位元混合），用來替每格產生固定的擬亂數。</summary>
    /// <param name="value">要雜湊的整數。</param>
    /// <returns>雜湊值。</returns>
    private static uint Hash(int value)
    {
        var h = (uint)value;
        h ^= h >> 16; h *= 0x7feb352d; h ^= h >> 15; h *= 0x846ca68b; h ^= h >> 16;
        return h;
    }

    /// <summary>線性內插。</summary>
    /// <param name="a">起點值。</param>
    /// <param name="b">終點值。</param>
    /// <param name="t">內插參數（0＝a、1＝b）。</param>
    /// <returns>內插結果。</returns>
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>平滑的較小值（多項式 smooth min）：兩者相差超過 <paramref name="k"/> 時就是 min，接近時圓滑過渡、略低於兩者。</summary>
    private static double SmoothMin(double a, double b, double k)
    {
        var h = Math.Max(k - Math.Abs(a - b), 0) / k;
        return Math.Min(a, b) - h * h * k / 4;
    }
    /// <summary>向量相減 a − b。</summary>
    /// <param name="a">被減向量。</param>
    /// <param name="b">減向量。</param>
    /// <returns>差向量。</returns>
    private static (double, double, double) Sub((double, double, double) a, (double, double, double) b) => (a.Item1 - b.Item1, a.Item2 - b.Item2, a.Item3 - b.Item3);
    /// <summary>向量相加 a ＋ b。</summary>
    /// <param name="a">第一個向量。</param>
    /// <param name="b">第二個向量。</param>
    /// <returns>和向量。</returns>
    private static (double, double, double) Add((double, double, double) a, (double, double, double) b) => (a.Item1 + b.Item1, a.Item2 + b.Item2, a.Item3 + b.Item3);
    /// <summary>向量乘上純量。</summary>
    /// <param name="a">向量。</param>
    /// <param name="s">純量。</param>
    /// <returns>縮放後的向量。</returns>
    private static (double, double, double) Scale((double, double, double) a, double s) => (a.Item1 * s, a.Item2 * s, a.Item3 * s);
    /// <summary>向量內積。</summary>
    /// <param name="a">第一個向量。</param>
    /// <param name="b">第二個向量。</param>
    /// <returns>內積。</returns>
    private static double Dot((double, double, double) a, (double, double, double) b) => a.Item1 * b.Item1 + a.Item2 * b.Item2 + a.Item3 * b.Item3;
    /// <summary>向量長度。</summary>
    /// <param name="a">向量。</param>
    /// <returns>歐氏長度。</returns>
    private static double Length((double, double, double) a) => Math.Sqrt(Dot(a, a));
    /// <summary>正規化成單位向量（零向量原樣回傳）。</summary>
    /// <param name="a">向量。</param>
    /// <returns>單位向量。</returns>
    private static (double, double, double) Normalize((double, double, double) a)
    {
        var length = Length(a);
        return length > 0 ? Scale(a, 1 / length) : a;
    }
    /// <summary>向量外積 a × b。</summary>
    /// <param name="a">第一個向量。</param>
    /// <param name="b">第二個向量。</param>
    /// <returns>外積。</returns>
    private static (double, double, double) Cross((double, double, double) a, (double, double, double) b) =>
        (a.Item2 * b.Item3 - a.Item3 * b.Item2, a.Item3 * b.Item1 - a.Item1 * b.Item3, a.Item1 * b.Item2 - a.Item2 * b.Item1);

    /// <summary>沿 a→b 方向，從 b 再延長一段（沒有上游／下游時當作虛擬的控制點）。</summary>
    private static (double, double, double) Extrapolate((double, double, double) a, (double, double, double) b) =>
        Normalize(Add(b, Sub(b, a)));

    /// <summary>Catmull-Rom 樣條在 p1 → p2 之間的內插點，結果投影回單位球。</summary>
    /// <param name="p0">p1 之前的控制點。</param>
    /// <param name="p1">區段起點。</param>
    /// <param name="p2">區段終點。</param>
    /// <param name="p3">p2 之後的控制點。</param>
    /// <param name="t">區段內的參數（0＝p1、1＝p2）。</param>
    /// <returns>單位球上的內插點。</returns>
    private static (double, double, double) CatmullRom(
        (double, double, double) p0, (double, double, double) p1, (double, double, double) p2, (double, double, double) p3, double t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        double Axis(double a, double b, double c, double d) =>
            0.5 * (2 * b + (-a + c) * t + (2 * a - 5 * b + 4 * c - d) * t2 + (-a + 3 * b - 3 * c + d) * t3);
        return Normalize((Axis(p0.Item1, p1.Item1, p2.Item1, p3.Item1), Axis(p0.Item2, p1.Item2, p2.Item2, p3.Item2), Axis(p0.Item3, p1.Item3, p2.Item3, p3.Item3)));
    }

    /// <summary>點到線段的最短距離與最近點的參數。</summary>
    /// <param name="p">查詢點。</param>
    /// <param name="a">線段起點。</param>
    /// <param name="b">線段終點。</param>
    /// <returns>距離與最近點在線段上的參數 t（0＝a、1＝b）。</returns>
    private static (double Distance, double T) DistanceToSegment((double, double, double) p, (double, double, double) a, (double, double, double) b)
    {
        var ab = Sub(b, a);
        var length2 = Dot(ab, ab);
        var t = length2 > 0 ? Math.Clamp(Dot(Sub(p, a), ab) / length2, 0, 1) : 0;
        return (Length(Sub(Add(a, Scale(ab, t)), p)), t);
    }

    /// <summary>點到線段（弦）的最短距離。</summary>
    /// <param name="p">查詢點。</param>
    /// <param name="a">弦的起點。</param>
    /// <param name="b">弦的終點。</param>
    /// <returns>最短距離。</returns>
    private static double DistanceToChord((double, double, double) p, (double, double, double) a, (double, double, double) b) =>
        DistanceToSegment(p, a, b).Distance;
}
