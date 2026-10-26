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

    /// <summary>河谷最遠刻到離河多遠（公尺），也是最近河道搜尋的範圍。</summary>
    private const double ValleyReachMeters = 8_000;

    /// <summary>三角洲的半徑（公里）＝ 這個值 × √(供砂 ÷ 1,000)，最多 60 公里，小於 3 公里就不畫。</summary>
    private const double DeltaRadiusKmPerSqrtSupply = 15;

    /// <summary>三角洲分流的數目。</summary>
    private const int DeltaDistributaries = 5;

    /// <summary>三角洲輪廓起伏的波長（公尺）。</summary>
    private const double DeltaLobeWavelengthMeters = 8_000;

    private readonly Planet.Generation.SimplexNoise3D _deltaNoise = new(2711);

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

    private readonly SimulationGrid _grid;
    private readonly IReadOnlyList<PlanetCell> _cells;
    private readonly double _radius;

    // 河道曲線：每一條河段（粗格 → 下游粗格）一條折線，單位球上的點
    private readonly int[] _curveStart;
    private readonly double[] _curvePoints;
    private readonly double[] _curveReach; // 這條曲線離弦最遠多少（單位球），用來快速略過
    private readonly float[] _valleyFloor; // 每格河谷底的高度（公尺）
    private readonly int[] _deltaApex; // 每格海：流進來的大河是從哪一格來的（−1 沒有三角洲）
    private readonly bool[] _riverHead; // 河的源頭：沒有上游河段流進來（谷從這裡由淺漸深）

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

        // 河谷底：格子高度減掉谷深，而且不高於上游、不低於下游的谷底（谷底要一路往下游降）
        _valleyFloor = new float[n];
        for (var c = 0; c < n; c++)
        {
            var cell = cells[c];
            var height = Math.Max(0, cell.SurfaceElevationMeters);
            var depthCap = MaxValleyDepthMeters * Math.Sqrt(Math.Min(1, Math.Max(cell.RiverWidthMeters, Rivers.MinChannelWidthMeters / 2) / ValleyDepthFullWidthMeters));
            var depth = Math.Min(depthCap, Math.Max(cell.IncisionRate, cell.GlacialIncisionRate) * ValleyIncisionMyr);
            depth = Math.Min(depth, height * MaxValleyDepthShareOfHeight);
            _valleyFloor[c] = (float)(cell.SurfaceElevationMeters - depth);
        }

        // 源頭：沒有任何河段流進來的河
        _riverHead = new bool[n];
        var hasUpstream = new bool[n];
        for (var c = 0; c < n; c++)
            if (cells[c].RiverDownstreamCellId >= 0) hasUpstream[cells[c].RiverDownstreamCellId] = true;
        for (var c = 0; c < n; c++) _riverHead[c] = cells[c].RiverDownstreamCellId >= 0 && !hasUpstream[c];

        // 河道曲線
        _curveStart = new int[n + 1];
        var points = new List<double>();
        var reach = new double[n];
        Span<int> neighbors = stackalloc int[SimulationGrid.MaxNeighborsPerCell];
        for (var c = 0; c < n; c++)
        {
            _curveStart[c] = points.Count / 3;
            var down = cells[c].RiverDownstreamCellId;
            if (down < 0) continue;
            reach[c] = BuildCurve(c, down, points, neighbors);
        }
        _curveStart[n] = points.Count / 3;
        _curvePoints = [.. points];
        _curveReach = reach;

        // 三角洲：大河流進海的那一格海，記下是哪一格流進來的
        _deltaApex = new int[n];
        Array.Fill(_deltaApex, -1);
        for (var c = 0; c < n; c++)
        {
            var down = cells[c].RiverDownstreamCellId;
            if (down < 0 || cells[down].WaterDepthMeters <= 0f || !cells[down].IsOcean) continue;
            if (DeltaRadiusMeters(down) <= 0) continue;
            if (_deltaApex[down] < 0 || cells[c].RiverWidthMeters > cells[_deltaApex[down]].RiverWidthMeters) _deltaApex[down] = c;
        }
    }

    /// <summary>三角洲的半徑（公尺）；太小不畫是 0。</summary>
    private double DeltaRadiusMeters(int mouth)
    {
        var supply = _cells[mouth].DeltaSupply;
        if (supply <= 0f) return 0;
        var km = Math.Min(60, DeltaRadiusKmPerSqrtSupply * Math.Sqrt(supply / 1_000.0));
        return km < 3 ? 0 : km * 1000;
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
            if (_cells[nb].RiverDownstreamCellId != c) continue;
            if (upstream < 0 || _cells[nb].RiverWidthMeters > _cells[upstream].RiverWidthMeters) upstream = nb;
        }
        var next = _cells[down].RiverDownstreamCellId;

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

    /// <summary>取樣某個經緯度（度）。</summary>
    /// <param name="latitude">緯度。</param>
    /// <param name="longitude">經度。</param>
    /// <param name="nearest">最近的粗格（已經找好的話傳進來，省一次搜尋）。</param>
    /// <param name="interpolatedElevation">內插的高度（已經算好的話）。</param>
    /// <param name="coastProximity">
    /// 附近的海佔多少（0~1，平滑核加權，見 <see cref="SimulatedTerrainSampler.SampleDetailed"/>）。給了就用它判斷沙灘、海崖帶，
    /// 沒給就用粗格之間的 Voronoi 分界（海岸線是格子的邊）。
    /// </param>
    public LandformSample Sample(double latitude, double longitude, int nearest = -1, double? interpolatedElevation = null, double? coastProximity = null)
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

        // ---- 水 ----
        if (cell.WaterDepthMeters > 0f)
        {
            var surface = (double)cell.WaterSurfaceMeters;
            elevation = Math.Min(elevation, surface - 1);
            return new LandformSample(cell.Terrain, cell.HeightCategory, elevation, surface, nearest, -1, double.PositiveInfinity, 0, false);
        }

        var terrain = cell.Terrain;
        var category = cell.HeightCategory;
        elevation = Math.Max(elevation, 0);

        // ---- 河：找最近的河道 ----
        var (riverCell, riverT, riverDist) = NearestRiver(p, nearest, neighbors[..count]);
        double? water = null;
        var floodplain = false;
        double riverWidth = 0;
        if (riverCell >= 0 && terrain != TerrainType.Glacier)
        {
            var source = _cells[riverCell];
            var down = source.RiverDownstreamCellId;
            riverWidth = source.RiverWidthMeters;
            var floor = ValleyFloorAt(riverCell, riverT, elevation);
            var depth = Math.Max(1, riverWidth * DepthPerWidth);
            var halfWidth = riverWidth / 2;
            var aggrading = source.AggradationRate > Math.Max(source.IncisionRate, source.GlacialIncisionRate);

            if (riverDist <= halfWidth)
            {
                // 河道本身
                water = floor;
                elevation = Math.Min(elevation, floor - depth);
                terrain = TerrainType.Freshwater;
                category = WaterDepthCategory(depth);
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
                var (profile, wallSlope) = ValleyProfile(p, nearest, neighbors[..count], elevation);
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

        // ---- 三角洲：大河流進海的那一格附近 ----
        if (water is null)
        {
            for (var i = -1; i < count; i++)
            {
                var k = i < 0 ? nearest : neighbors[i];
                if (_deltaApex[k] < 0) continue;
                if (PaintDelta(p, k, ref terrain, ref category, ref elevation, ref water)) break;
                // 一格的分流畫好就夠了
            }
        }

        // ---- 海岸：沙灘、礫灘、海崖（冰河、雪地的海岸不畫）----
        if (water is null && terrain is not (TerrainType.Glacier or TerrainType.SnowField))
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
        if (water is null && !floodplain)
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
            else if (cell.AggradationRate > 20f && cell.AggradationRate > cell.IncisionRate && Relief(nearest, neighbors[..count]) > 200
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
            var down = _cells[c].RiverDownstreamCellId;
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
    /// </summary>
    private double ValleyFloorAt(int c, double t, double elevation)
    {
        var down = _cells[c].RiverDownstreamCellId;
        var floor = Lerp(_valleyFloor[c], down >= 0 && _cells[down].WaterDepthMeters <= 0f ? _valleyFloor[down] : 0, t);
        floor = Math.Max(floor, 0);
        if (_riverHead[c] && floor < elevation)
        {
            var taper = t * t * (3 - 2 * t);
            floor = elevation - (elevation - floor) * taper;
        }
        return floor;
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
    private (double Profile, double WallSlope) ValleyProfile((double, double, double) p, int nearest, ReadOnlySpan<int> neighbors, double elevation)
    {
        var best = double.PositiveInfinity;
        var bestSlope = 0.0;
        var u = _grid.UnitVector;
        for (var i = -1; i < neighbors.Length; i++)
        {
            var c = i < 0 ? nearest : neighbors[i];
            var cell = _cells[c];
            var down = cell.RiverDownstreamCellId;
            if (down < 0 || cell.AggradationRate > Math.Max(cell.IncisionRate, cell.GlacialIncisionRate)) continue;
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
                var profile = ValleyFloorAt(c, t, elevation) + Math.Max(0, meters - floorHalf) * slope;
                if (profile < best)
                {
                    best = profile;
                    bestSlope = slope;
                }
            }
        }
        return (best, bestSlope);
    }

    /// <summary>三角洲：扇面裡的陸地壓成 0~3 公尺的泥灘與沼澤，分流是淡水。有畫到回傳 true。</summary>
    private bool PaintDelta((double, double, double) p, int mouth, ref TerrainType terrain, ref int category, ref double elevation, ref double? water)
    {
        var apexCell = _deltaApex[mouth];
        var radius = DeltaRadiusMeters(mouth);
        var u = _grid.UnitVector;
        var apex = (u[apexCell * 3], u[apexCell * 3 + 1], u[apexCell * 3 + 2]);
        var sea = (u[mouth * 3], u[mouth * 3 + 1], u[mouth * 3 + 2]);
        // 邊緣用雜訊打散：三角洲的輪廓是一個個朵狀的沙洲，不是圓規畫的扇形
        var f = _radius / DeltaLobeWavelengthMeters;
        var lobe = _deltaNoise.Fbm(p.Item1 * f, p.Item2 * f, p.Item3 * f, 3, 1.0);
        var distance = Length(Sub(p, apex)) * _radius;
        if (distance > radius * (0.8 + 0.35 * lobe) || distance < 1) return false;

        // 扇面的方向：從頂點（河流出山的那一格）指向河口那一格海
        var axis = Normalize(Sub(sea, apex));
        var side = Normalize(Cross(apex, axis));
        var dir = Sub(p, apex);
        var along = Dot(dir, axis);
        var across = Dot(dir, side);
        var angle = Math.Atan2(across, along) * 180 / Math.PI;
        if (Math.Abs(angle) > DeltaHalfAngleDegrees * (0.85 + 0.3 * lobe)) return false;

        // 分流：從頂點放射出去的幾條直線，各自稍微偏一點
        var width = Math.Max(_cells[apexCell].RiverWidthMeters * 0.4, Rivers.MinChannelWidthMeters);
        for (var i = 0; i < DeltaDistributaries; i++)
        {
            var spread = (i - (DeltaDistributaries - 1) / 2.0) / ((DeltaDistributaries - 1) / 2.0) * DeltaHalfAngleDegrees * 0.8;
            spread += ((Hash(mouth * 31 + i) & 15) - 7.5) * 1.5;
            var rad = spread * Math.PI / 180;
            var lateral = Math.Abs(-Math.Sin(rad) * along + Math.Cos(rad) * across) * _radius;
            var forward = (Math.Cos(rad) * along + Math.Sin(rad) * across) * _radius;
            if (forward > 0 && lateral < width / 2)
            {
                terrain = TerrainType.Freshwater;
                category = WaterDepthCategory(2);
                water = 0.5;
                elevation = Math.Min(elevation, -1.5);
                return true;
            }
        }
        // 分流之間：泥灘（靠海）與沼澤（靠內陸），往海邊越來越低
        var fraction = distance / radius;
        elevation = Math.Min(elevation, 3 * (1 - fraction) + 0.5);
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

    private static uint Hash(int value)
    {
        var h = (uint)value;
        h ^= h >> 16; h *= 0x7feb352d; h ^= h >> 15; h *= 0x846ca68b; h ^= h >> 16;
        return h;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>平滑的較小值（多項式 smooth min）：兩者相差超過 <paramref name="k"/> 時就是 min，接近時圓滑過渡、略低於兩者。</summary>
    private static double SmoothMin(double a, double b, double k)
    {
        var h = Math.Max(k - Math.Abs(a - b), 0) / k;
        return Math.Min(a, b) - h * h * k / 4;
    }
    private static (double, double, double) Sub((double, double, double) a, (double, double, double) b) => (a.Item1 - b.Item1, a.Item2 - b.Item2, a.Item3 - b.Item3);
    private static (double, double, double) Add((double, double, double) a, (double, double, double) b) => (a.Item1 + b.Item1, a.Item2 + b.Item2, a.Item3 + b.Item3);
    private static (double, double, double) Scale((double, double, double) a, double s) => (a.Item1 * s, a.Item2 * s, a.Item3 * s);
    private static double Dot((double, double, double) a, (double, double, double) b) => a.Item1 * b.Item1 + a.Item2 * b.Item2 + a.Item3 * b.Item3;
    private static double Length((double, double, double) a) => Math.Sqrt(Dot(a, a));
    private static (double, double, double) Normalize((double, double, double) a)
    {
        var length = Length(a);
        return length > 0 ? Scale(a, 1 / length) : a;
    }
    private static (double, double, double) Cross((double, double, double) a, (double, double, double) b) =>
        (a.Item2 * b.Item3 - a.Item3 * b.Item2, a.Item3 * b.Item1 - a.Item1 * b.Item3, a.Item1 * b.Item2 - a.Item2 * b.Item1);

    /// <summary>沿 a→b 方向，從 b 再延長一段（沒有上游／下游時當作虛擬的控制點）。</summary>
    private static (double, double, double) Extrapolate((double, double, double) a, (double, double, double) b) =>
        Normalize(Add(b, Sub(b, a)));

    private static (double, double, double) CatmullRom(
        (double, double, double) p0, (double, double, double) p1, (double, double, double) p2, (double, double, double) p3, double t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        double Axis(double a, double b, double c, double d) =>
            0.5 * (2 * b + (-a + c) * t + (2 * a - 5 * b + 4 * c - d) * t2 + (-a + 3 * b - 3 * c + d) * t3);
        return Normalize((Axis(p0.Item1, p1.Item1, p2.Item1, p3.Item1), Axis(p0.Item2, p1.Item2, p2.Item2, p3.Item2), Axis(p0.Item3, p1.Item3, p2.Item3, p3.Item3)));
    }

    private static (double Distance, double T) DistanceToSegment((double, double, double) p, (double, double, double) a, (double, double, double) b)
    {
        var ab = Sub(b, a);
        var length2 = Dot(ab, ab);
        var t = length2 > 0 ? Math.Clamp(Dot(Sub(p, a), ab) / length2, 0, 1) : 0;
        return (Length(Sub(Add(a, Scale(ab, t)), p)), t);
    }

    private static double DistanceToChord((double, double, double) p, (double, double, double) a, (double, double, double) b) =>
        DistanceToSegment(p, a, b).Distance;
}
