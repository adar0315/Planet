namespace Planet.Simulation;

/// <summary>
/// v2 的外營力（P7，2026-10-19；只在板塊座標系模式用，標記法照舊用 <see cref="Erosion"/>）：
/// 河流侵蝕、冰川刨蝕、海浪、風化，刨下來的物質沿著 <see cref="Hydrology"/> 的流向搬走，
/// 堆進湖裡或沿著大陸棚往外鋪。**沉積總量等於侵蝕總量**（<see cref="LastErodedVolumeKm3"/> ＝ <see cref="LastDepositedVolumeKm3"/>）。
///
/// 跟 v1 的 <see cref="Erosion"/> 不同的地方，每一條都是 P7 基準線量到的問題（種子 42 劇本、40 Myr）：
///
/// 1. **流向用 P5 的泛洪**（灌滿窪地之後往主洋的方向），不是最陡下降。最陡下降碰到窪地就停，
///    41% 的沉積物堆在陸地上的小坑裡；泛洪的流向會越過淺窪地，只有真的存水的盆地（湖）才留得住泥沙。
/// 2. **坡度以水面為基準**：流進海的那一格，落差算到海平面，不是算到幾千公尺深的海床。
/// 3. **沉積物進海之後沿著大陸棚往外鋪**（<see cref="ShelfDepthMeters"/>），不是全部倒在第一格海上——
///    那樣海岸那一格會被墊成陸地、下一步又被刨掉，來回震盪。
/// 4. **褶皺（<see cref="SimulationState.StructuralRelief"/>）也會被刨**：先刨鬆散的沉積物、再刨凸起的褶皺、最後才是基岩。
///    褶皺是疊在地表上的一層，刨 1 公尺就降 1 公尺；基岩要換算均衡反彈（見 <see cref="Erosion"/> 的類別說明）。
/// 5. 流量用 <see cref="Hydrology.Discharge"/>（降水的相對單位 × 格），係數直接對著陸地高度校準
///    （2026-10-19 使用者選定：40 Myr 時陸地平均高出海面約 0.8~1 公里、最高峰 9 公里以內）。
///
/// 水文地貌規劃 H2（2026-10-24）加上的三件事：
///
/// 6. **岩性**：侵蝕乘上露出岩石的侵蝕係數（<see cref="Lithology.Erodibility"/>），硬岩留下山脊與峽谷、軟岩被削成盆地；
///    再乘上洪峰係數（<see cref="SimulationState.FloodFactor"/>）。
/// 7. **搬運容量**：載量超過 <see cref="TransportCoefficient"/>·Q·S 的部分在陸地上堆下來——山前坡度一緩就堆成沖積扇、
///    緩坡的下游堆成沖積平原。以前搬運沒有上限，泥沙全部直送湖或海，陸地上不會有任何堆積地形。
///    堆積把河床墊高、坡度變陡、容量跟著變大，所以會自己收斂成「剛好搬得動」的坡度（均衡河）。
/// 8. **河口三角洲**：河口附近的淺海每步最多墊高 <see cref="MaxDeltaFillPerStepMeters"/>，可以墊出海面幾公尺，
///    陸地慢慢往海裡長；其餘照舊沿大陸棚往外鋪。速率限制是必要的：大河一步的泥沙可以填平上百格大陸棚，
///    不限速的話三角洲一步就往外長幾百公里。
/// </summary>
public sealed class FluvialErosion
{
    /// <summary>河流侵蝕係數：E（公尺／百萬年）＝ K · Q^m · S，Q 是 <see cref="Hydrology.Discharge"/>，S 在 <see cref="SlopeReferenceLengthMeters"/> 上量。</summary>
    ///
    /// 校準（2026-10-20，使用者選定）：種子 42 劇本 40 Myr 時陸地平均高出海面 0.93 公里、最高峰 8.9 公里、陸地佔表面 31.6%、
    /// 剝蝕率約 190 公尺／百萬年（物質厚度）。3,000 時陸地 1.10 公里；10,000 時最高峰 10.4 公里、第二大陸的破碎度 4.3 超過上限。
    /// P8（2026-10-20）改了起始大陸地殼（較薄、往內陸慢慢加厚）之後維持 6,000：低地是侵蝕削出來的，K 調低剝蝕率會降、
    /// 但低地變少、陸地變高變冷（1,000 時 0~0.5 km 只剩 16%、冰 19.9%）；使用者選以高度分布為主。停止條件觸發時陸地平均 0.65 公里。
    internal double StreamPowerCoefficient { get; set; } = 6000.0;

    /// <summary>流量的指數（stream power law 的 m）。</summary>
    private const double DischargeExponent = 0.5;

    /// <summary>量坡度用的參考長度（公尺），跟 <see cref="Erosion"/> 一樣，與解析度無關。</summary>
    private const double SlopeReferenceLengthMeters = 15_000.0;

    /// <summary>風化：到處都有的背景剝蝕（公尺地表／百萬年）。</summary>
    private const double BaseWeatheringMetersPerMyr = 5.0;

    /// <summary>冰川刨蝕相對於流水的倍率。</summary>
    private const double GlacialErosionMultiplier = 3.0;

    /// <summary>結冰的溫度門檻（攝氏），低於這個算冰川。</summary>
    private const float GlacialTemperatureC = -5f;

    /// <summary>
    /// 搬運容量係數：一步能帶走的物質（公尺×格）＝ 這個值 · Q · S · 洪峰係數 · Δt（百萬年）。
    /// 載量 ≈ 集水區格數 × 平均剝蝕，Q ≈ 集水區格數 × 平均逕流，所以「搬得動 / 帶來的」≈ 係數·S·逕流 / 剝蝕：
    /// 坡度低於 剝蝕÷(係數·逕流) 的河段就開始堆。地球的沖積平原坡度在 10⁻⁴~10⁻³，以剝蝕約 200 公尺／百萬年、
    /// 逕流約 2 估，3×10⁵ 讓門檻落在 3×10⁻⁴ 附近（n=150 一格約 4 公尺的落差）。
    /// 2026-10-24 第一次在 n=150 劇本上跑：3×10⁵ 時 41 Myr 陸地有 83% 以堆積為主、36% 露出鬆散沉積物（內陸高原整片在堆），
    /// 提高 10 倍讓門檻落在 3×10⁻⁵（地球大河下游平原的坡度）。
    /// </summary>
    internal double TransportCoefficient { get; set; } = 3e6;

    /// <summary>
    /// 一步最多堆高多少公尺（陸上的河流堆積）。20 公尺／5 萬年 ＝ 0.4 公釐／年，地球沖積扇、前陸盆地的量級。
    /// 不設上限的話，大河在第一個緩坡就把整條河的泥沙倒下來，堆出一座壩。
    /// </summary>
    private const double MaxAggradationPerStepMeters = 20.0;

    /// <summary>河口附近多遠以內（公尺）算三角洲的範圍。地球的大三角洲（密西西比、尼羅河、恆河）約一兩百公里。</summary>
    private const double DeltaRadiusMeters = 60_000.0;

    /// <summary>
    /// 三角洲範圍裡的海格一步最多墊高多少公尺。2 公尺／5 萬年：130 公尺深的大陸棚約 300 萬年墊出海面，
    /// 三角洲每 300 萬年往外長一格（15 公里），40 Myr 長約 200 公里——大三角洲的量級。
    /// </summary>
    private const float MaxDeltaFillPerStepMeters = 2f;

    /// <summary>三角洲最高墊到海平面以上幾公尺（三角洲平原）。</summary>
    private const float DeltaTopMeters = 3f;

    /// <summary>
    /// 浪對三角洲的削減（水文地貌規劃 H5）：三角洲範圍裡面向深水（比浪基面深）的海格比例 × 這個值，就是墊高速率打的折扣。
    /// 開闊海岸的河口被浪削成平直的沙岸（地球上浪控型三角洲，如塞內加爾河），封閉的淺海才長得出鳥足狀的大三角洲（密西西比）。
    /// </summary>
    private const double WaveDeltaDamping = 0.8;

    /// <summary>比這更深的海不長三角洲（公尺）：活動大陸邊緣的河口直接面對深海，泥沙順著海底峽谷流走。</summary>
    private const float DeltaMaxWaterDepthMeters = 300f;

    /// <summary>診斷量（下切、堆積、冰蝕、三角洲）移動平均的時間常數（百萬年）。</summary>
    private const double RateAveragingMyr = 1.0;

    /// <summary>浪基面：外面的海比這個淺（公尺）就不算受浪的邊，見 <see cref="SeaNeighborCount"/>。</summary>
    private const float WaveBaseDepthMeters = 200f;

    /// <summary>海浪侵蝕（公尺地表／百萬年／每一條面向深水的邊）。</summary>
    private const double WaveErosionMetersPerMyr = 30.0;

    /// <summary>
    /// 一步最多把地表降低多少公尺：200 公尺／5 萬年 ＝ 4 公釐／年，地球最快的造山帶（台灣、喜馬拉雅東端）的量級。
    /// v1 的 50 公尺在 P7 第一次校準時卡住了碰撞帶最高的 300 格裡的 59~172 格。
    /// </summary>
    private const double MaxSurfaceLoweringPerStepMeters = 200.0;

    /// <summary>河流一步最多刨掉往下游落差的多少：不會把一格刨到比下游還低（挖出坑、下一步坡度反過來，來回震盪）。</summary>
    private const double MaxStreamLoweringFractionOfDrop = 0.5;

    /// <summary>拿掉 1 公尺沉積物地表降多少（均衡反彈之後），見 <see cref="Isostasy.LoadDepressionMeters"/>。</summary>
    private const double SedimentSurfaceRatio = (Isostasy.MantleDensity - Isostasy.SedimentDensity) / (double)Isostasy.MantleDensity;

    /// <summary>湖滿了往下游、流進下一個湖，最多重複幾輪。</summary>
    private const int MaxLakeRounds = 8;

    /// <summary>
    /// 進海的沉積物先把海床墊到海平面下這個深度（公尺），滿了再往更深處流——堆出來的就是大陸棚。
    /// 地球的大陸棚外緣約在海面下 130 公尺。
    /// </summary>
    private const float ShelfDepthMeters = 130f;

    /// <summary>
    /// 進海的沉積物最多往外走多遠（公尺）：濁流可以把泥沙帶到上千公里外的深海平原。
    /// 第一版 600 公里：大陸之間的窄海裡 600 公里內只有幾格，裝不下的平均倒在那幾格上、墊出海面，
    /// 40 Myr 時有 8,993 格陸地是 1 公里以上的沉積物（P7，2026-10-19）。
    /// </summary>
    private const double MaxSeaTransportMeters = 2_000_000.0;

    /// <summary>大陸棚都墊滿了之後，第二輪最多墊到海平面下這個深度（公尺）——沉積物不會自己長出海面。</summary>
    private const float FilledSeaDepthMeters = 5f;

    private readonly SimulationGrid _grid;
    private readonly float[] _surface;
    private readonly float[] _waterSurface;
    private readonly double[] _load;
    private readonly double[] _basinLoad;
    private readonly int[] _lakeCellCount;
    private readonly double[] _lakeCapacity;
    private readonly int[] _visitStamp;
    private readonly List<int> _visited = [];
    private int _stamp;
    private readonly int _maxSeaHops;
    private readonly int _deltaHops;
    private readonly float[] _incision;
    private readonly float[] _aggradation;
    private readonly float[] _glacial;
    private readonly float[] _deltaSupply;

    /// <summary>診斷用：最近一步每一格要求的地表降低量（公尺，夾上限之前）。</summary>
    internal float[] LastRequestedLowering { get; }

    /// <summary>最近一步刨下來的物質（立方公里，沉積物＋褶皺＋基岩）。</summary>
    public double LastErodedVolumeKm3 { get; private set; }

    /// <summary>最近一步堆下去的物質（立方公里），應該等於 <see cref="LastErodedVolumeKm3"/>。</summary>
    public double LastDepositedVolumeKm3 { get; private set; }

    /// <summary>最近一步堆進湖裡的（立方公里）。</summary>
    public double LastLakeDepositKm3 { get; private set; }

    /// <summary>最近一步刨掉的褶皺（立方公里）。</summary>
    public double LastReliefErodedKm3 { get; private set; }

    /// <summary>診斷用：這一步各種備援路徑堆下去的量（公尺×格）：[0] 第二輪墊到海面、[1] 平均鋪、[2] 沒有去處就地堆。</summary>
    internal double[] FallbackDeposits { get; } = new double[3];

    /// <summary>診斷用（P8，累計、不歸零）：刨掉的基岩地殼（公里×格）。</summary>
    internal double CrustErodedKm { get; private set; }

    /// <summary>診斷用（P8，累計、不歸零）：堆進海裡的沉積物（立方公里），也就是陸地的淨輸出。</summary>
    internal double SeaDepositedKm3 { get; private set; }

    /// <summary>診斷用：這一步最大的一筆就地堆（路徑 1 ＝ 主迴圈沒有流向、2 ＝ 往下游送到底）、格子與量。</summary>
    internal (int Path, int Cell, double Amount) LargestDump { get; private set; }

    /// <summary>建立 v2 的外營力模型。</summary>
    /// <param name="grid">網格。</param>
    public FluvialErosion(SimulationGrid grid)
    {
        _grid = grid;
        var n = grid.CellCount;
        _surface = new float[n];
        _waterSurface = new float[n];
        _load = new double[n];
        _basinLoad = new double[n];
        _lakeCellCount = new int[n];
        _lakeCapacity = new double[n];
        _visitStamp = new int[n];
        LastRequestedLowering = new float[n];
        _maxSeaHops = Math.Max(1, (int)Math.Round(MaxSeaTransportMeters / grid.AverageCellSpacingMeters));
        _deltaHops = Math.Max(0, (int)Math.Round(DeltaRadiusMeters / grid.AverageCellSpacingMeters));
        _incision = new float[n];
        _aggradation = new float[n];
        _glacial = new float[n];
        _deltaSupply = new float[n];
    }

    /// <summary>最近一步在陸上堆下的（立方公里，沖積扇與沖積平原）。</summary>
    public double LastLandDepositKm3 { get; private set; }

    /// <summary>最近一步堆進三角洲範圍的（立方公里）。</summary>
    public double LastDeltaDepositKm3 { get; private set; }

    /// <summary>
    /// 推進一步：用上一步的水（海、湖、流向、流量）刨蝕並搬運。排在均衡注水之前（跟 <see cref="Erosion"/> 同一個慣例）。
    /// </summary>
    /// <param name="state">模擬狀態：沉積物、褶皺、地殼厚度會被更新。</param>
    /// <param name="hydrology">上一步解好的水系。</param>
    /// <param name="deltaYears">這一步代表多少年。</param>
    public void Step(SimulationState state, Hydrology hydrology, double deltaYears)
    {
        var deltaMyr = deltaYears / 1_000_000.0;
        var n = state.CellCount;
        var seaLevel = state.SeaLevelMeters;
        var lakeOf = hydrology.LakeOf;
        var basinOf = hydrology.BasinOf;
        var parent = hydrology.FlowParent;
        var order = hydrology.FlowOrder;
        var discharge = hydrology.Discharge;
        // 只有真的河才長三角洲：每一格海岸陸地都「流進海」，不擋的話整條海岸線都在墊三角洲
        var deltaRiver = Rivers.MinDischarge(_grid, state);

        Parallel.For(0, n, c =>
        {
            _surface[c] = state.SurfaceElevationAt(c);
            _waterSurface[c] = _surface[c] + state.WaterDepth[c];
        });
        Array.Clear(_load);
        Array.Clear(_basinLoad);
        Array.Clear(_lakeCellCount);
        Array.Clear(_lakeCapacity);
        Array.Clear(LastRequestedLowering);
        Array.Clear(_incision);
        Array.Clear(_aggradation);
        Array.Clear(_glacial);
        Array.Clear(_deltaSupply);
        LargestDump = default;
        for (var c = 0; c < n; c++) if (lakeOf[c] >= 0) _lakeCellCount[lakeOf[c]]++;

        double eroded = 0, deposited = 0, lake = 0, relief = 0, land = 0;
        _deltaDeposited = 0;

        // 泛洪順序反過來走就是上游先（每一格的流向 parent 都在它前面被泛洪到）
        for (var i = hydrology.FlowOrderCount - 1; i >= 0; i--)
        {
            var c = order[i];
            if (IsSea(state, lakeOf, c) || lakeOf[c] >= 0) continue;

            var p = parent[c];
            // 岩性：硬岩的風化、浪蝕、河蝕都慢（H2）
            var erodibility = Lithology.Erodibility(Lithology.ExposedAt(state, c));
            var flood = state.FloodFactor[c];
            var lowering = erodibility * (BaseWeatheringMetersPerMyr
                + WaveErosionMetersPerMyr * SeaNeighborCount(state, lakeOf, c)) * deltaMyr;
            var drop = 0f;
            if (p >= 0)
            {
                // 坡度以水面為基準：流進海或湖的那一格，落差算到水面
                drop = _surface[c] - _waterSurface[p];
                if (drop > 0f)
                {
                    var stream = StreamPowerCoefficient * erodibility * flood
                        * Math.Pow(Math.Max(discharge[c], 0f), DischargeExponent)
                        * drop / SlopeReferenceLengthMeters;
                    var glacial = state.Temperature[c] < GlacialTemperatureC;
                    if (glacial) stream *= GlacialErosionMultiplier;
                    var streamLowering = Math.Min(stream * deltaMyr, drop * MaxStreamLoweringFractionOfDrop);
                    lowering += streamLowering;
                    var rate = (float)(streamLowering / deltaMyr);
                    if (glacial) _glacial[c] = rate;
                    else _incision[c] = rate;
                }
            }
            LastRequestedLowering[c] = (float)lowering;
            lowering = Math.Min(lowering, MaxSurfaceLoweringPerStepMeters);
            // 基準面：什麼營力都刨不到海平面以下（海浪只削出海面附近的海蝕平台）。少了這一條，海浪與風化把低平原
            // 一路刨到海面下，40 Myr 時 A 北、C 北的平原整片變成淺海、一座大島沉掉（P7，2026-10-20）
            lowering = Math.Min(lowering, Math.Max(0.0, _surface[c] - seaLevel));
            if (lowering > 0)
            {
                var (removed, fromRelief) = RemoveMaterial(state, c, lowering);
                _load[c] += removed;
                eroded += removed;
                relief += fromRelief;
            }
            if (_load[c] <= 0) continue;

            // 搬運容量（H2）：搬不動的在這一格堆下來——沖積扇、沖積平原。流進海或湖的那一格不堆（交給三角洲與湖）
            if (p >= 0 && !IsSea(state, lakeOf, p) && lakeOf[p] < 0)
            {
                var capacity = TransportCoefficient * flood * Math.Max(discharge[c], 0f)
                    * Math.Max(drop, 0f) / SlopeReferenceLengthMeters * deltaMyr;
                var excess = _load[c] - capacity;
                if (excess > 0)
                {
                    var settle = Math.Min(excess, MaxAggradationPerStepMeters);
                    state.SedimentThickness[c] += (float)settle;
                    _surface[c] += (float)settle;
                    _load[c] -= settle;
                    deposited += settle;
                    land += settle;
                    _aggradation[c] = (float)(settle / deltaMyr);
                    if (_load[c] <= 0) continue;
                }
            }

            // 自己就在存水盆地裡（湖邊的坡）：泥沙進湖
            var basin = basinOf[c];
            if (basin >= 0 && hydrology.HoldsWater(basin) && _lakeCellCount[basin] > 0)
            {
                _basinLoad[basin] += _load[c];
                _load[c] = 0;
                continue;
            }
            if (p < 0)
            {
                // 沒有流向：上一步的主洋種子、海平面降了之後露出水面（流向是上一步泛洪的）。
                // 第一版就地堆，整條河的泥沙每一步倒在露出來的那一格，堆出 20~30 公里的沉積柱（P7，2026-10-19）
                deposited += DepositNearestSea(state, lakeOf, c, _load[c], 1);
                _load[c] = 0;
                continue;
            }
            if (IsSea(state, lakeOf, p))
            {
                // 河口：先墊三角洲（限速），剩下的沿大陸棚往外鋪
                var toDelta = deltaRiver > 0f && discharge[c] >= deltaRiver ? BuildDelta(state, lakeOf, p, _load[c], deltaMyr) : 0.0;
                deposited += toDelta;
                _load[c] -= toDelta;
                if (_load[c] > 0) deposited += DepositInSea(state, lakeOf, p, _load[c]);
                _load[c] = 0;
                continue;
            }
            if (lakeOf[p] >= 0)
            {
                _basinLoad[lakeOf[p]] += _load[c];
                _load[c] = 0;
                continue;
            }
            _load[p] += _load[c];
            _load[c] = 0;
        }

        // 進湖的泥沙依水深比例鋪在湖底（湖會慢慢被填淺），最多填到湖面；多的從溢出口接著往下游，
        // 可能流進下一個湖，所以重複到沒有湖還有待處理的泥沙為止
        for (var c = 0; c < n; c++)
        {
            var b = lakeOf[c];
            if (b >= 0) _lakeCapacity[b] += Math.Max(0f, hydrology.LakeLevelAt(c) - _surface[c]);
        }
        for (var round = 0; round < MaxLakeRounds; round++)
        {
            for (var c = 0; c < n; c++)
            {
                var b = lakeOf[c];
                if (b < 0 || _basinLoad[b] <= 0 || _lakeCapacity[b] <= 0) continue;
                var fill = Math.Min(1.0, _basinLoad[b] / _lakeCapacity[b]);
                var add = Math.Max(0f, hydrology.LakeLevelAt(c) - _surface[c]) * fill;
                state.SedimentThickness[c] += (float)add;
                _surface[c] += (float)add;
                deposited += add;
                lake += add;
            }
            var overflowed = false;
            for (var b = 0; b < n; b++)
            {
                if (_basinLoad[b] <= 0) continue;
                var excess = _basinLoad[b] - _lakeCapacity[b];
                _lakeCapacity[b] = Math.Max(0, _lakeCapacity[b] - _basinLoad[b]);
                _basinLoad[b] = 0;
                if (excess <= 0) continue;
                overflowed = true;
                deposited += RouteDownstream(state, hydrology, hydrology.BasinOutlet(b), excess);
            }
            if (!overflowed) break;
        }
        // 輪數用完還沒處理的（湖一個接一個都滿了，極少見）：就地平均鋪在湖上
        for (var c = 0; c < n; c++)
        {
            var b = lakeOf[c];
            if (b < 0 || _basinLoad[b] <= 0) continue;
            var add = _basinLoad[b] / _lakeCellCount[b];
            state.SedimentThickness[c] += (float)add;
            deposited += add;
            lake += add;
        }

        if (SlumpEnabled) SlumpSteepSediment(state, lakeOf);

        // 診斷量的移動平均（約 100 萬年）：放大時決定河谷、平原、三角洲的樣子
        var alpha = (float)Math.Min(1.0, deltaMyr / RateAveragingMyr);
        Parallel.For(0, n, c =>
        {
            state.IncisionRate[c] += (_incision[c] - state.IncisionRate[c]) * alpha;
            state.AggradationRate[c] += (_aggradation[c] - state.AggradationRate[c]) * alpha;
            state.GlacialIncisionRate[c] += (_glacial[c] - state.GlacialIncisionRate[c]) * alpha;
            state.DeltaSupply[c] += (_deltaSupply[c] - state.DeltaSupply[c]) * alpha;
        });

        var cellKm2 = _grid.AverageCellAreaSquareMeters / 1e6;
        LastLandDepositKm3 = land / 1000.0 * cellKm2;
        LastDeltaDepositKm3 = _deltaDeposited / 1000.0 * cellKm2;
        LastErodedVolumeKm3 = eroded / 1000.0 * cellKm2;
        LastDepositedVolumeKm3 = deposited / 1000.0 * cellKm2;
        LastLakeDepositKm3 = lake / 1000.0 * cellKm2;
        SeaDepositedKm3 += (deposited - lake) / 1000.0 * cellKm2;
        LastReliefErodedKm3 = relief / 1000.0 * cellKm2;
    }

    private double _deltaDeposited;

    /// <summary>
    /// 鬆散沉積物最陡能多陡（落差÷格距）。坡面上的鬆散物質會潛移、崩落，十幾公里的尺度上沉積面的坡度很少超過 1~3°。
    /// </summary>
    private const double MaxSedimentSlope = 0.05;

    /// <summary>校準用：坡面崩落開不開。</summary>
    internal bool SlumpEnabled { get; set; } = true;

    /// <summary>最近一步崩落滑走的鬆散沉積物（立方公里）。</summary>
    public double LastSlumpedKm3 { get; private set; }

    /// <summary>
    /// 坡面崩落（水文地貌規劃 H2 補的安全網）：鬆散沉積物比最低的鄰居高出 <see cref="MaxSedimentSlope"/>·格距以上，
    /// 多出來的一半滑到那個鄰居（是海就照常進海），一步最多 <see cref="MaxAggradationPerStepMeters"/>。物質守恆，不算侵蝕也不算堆積。
    ///
    /// 模型原本沒有任何坡面作用，流水又只靠流量搬：沒有雨的沙漠裡，風或流水堆下的沉積物一旦堆成一座小山，
    /// 就沒有任何作用搬得動它。2026-10-24 在 n=150 劇本上量到一格 15 公里厚的沉積、地表高出鄰居 6 公里。
    /// </summary>
    private void SlumpSteepSediment(SimulationState state, int[] lakeOf)
    {
        var maxDrop = MaxSedimentSlope * _grid.AverageCellSpacingMeters;
        var moved = 0.0;
        for (var c = 0; c < state.CellCount; c++)
        {
            var sediment = state.SedimentThickness[c];
            if (sediment <= 0f || state.WaterDepth[c] > 0f) continue;
            var lowest = -1;
            var lowestSurface = float.MaxValue;
            var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
            for (var k = 0; k < SimulationGrid.MaxNeighborsPerCell; k++)
            {
                var nb = _grid.Neighbors[baseIndex + k];
                if (nb == SimulationGrid.NoNeighbor) continue;
                var level = state.WaterDepth[nb] > 0f ? _waterSurface[nb] : _surface[nb]; // 陸地用這一步更新過的地表
                if (level < lowestSurface) { lowestSurface = level; lowest = nb; }
            }
            if (lowest < 0) continue;
            var excess = _surface[c] - lowestSurface - maxDrop;
            if (excess <= 0) continue;
            // 一步最多滑走 MaxAggradationPerStepMeters（跟河流堆積同一個速率上限）：一次搬幾十公里的話，
            // 板塊座標系回寫時混疊的地殼點會照抄那一格的沉積物，搬得越多複製得越多（2026-10-24 n=150 量到一格 225 公里）
            var take = Math.Min(Math.Min(sediment, excess / 2), MaxAggradationPerStepMeters);
            state.SedimentThickness[c] = (float)(sediment - take);
            _surface[c] -= (float)take;
            if (IsSea(state, lakeOf, lowest)) DepositInSea(state, lakeOf, lowest, take);
            else
            {
                state.SedimentThickness[lowest] += (float)take;
                _surface[lowest] += (float)take;
            }
            moved += take;
        }
        LastSlumpedKm3 = moved / 1000.0 * _grid.AverageCellAreaSquareMeters / 1e6;
    }

    /// <summary>
    /// 河口三角洲：從河口一圈一圈往外（<see cref="DeltaRadiusMeters"/> 內），把夠淺的海格各墊高最多 <see cref="MaxDeltaFillPerStepMeters"/>，
    /// 最高到海平面以上 <see cref="DeltaTopMeters"/>。回傳堆下去的量（不超過 <paramref name="amount"/>）。
    /// 墊出海面的格子下一步就是陸地（三角洲平原），河口跟著往外移，三角洲一格一格往海裡長。
    /// </summary>
    private double BuildDelta(SimulationState state, int[] lakeOf, int mouth, double amount, double deltaMyr)
    {
        var top = state.SeaLevelMeters + DeltaTopMeters;
        var deepest = state.SeaLevelMeters - DeltaMaxWaterDepthMeters;
        var put = 0.0;
        _stamp++;
        _visited.Clear();
        _visited.Add(mouth);
        _visitStamp[mouth] = _stamp;
        var ringStart = 0;
        // 先把範圍圈出來：浪的大小要看整個範圍裡有多少深水
        for (var ring = 0; ring < _deltaHops && ringStart < _visited.Count; ring++)
        {
            var ringEnd = _visited.Count;
            for (var i = ringStart; i < ringEnd; i++)
            {
                var baseIndex = _visited[i] * SimulationGrid.MaxNeighborsPerCell;
                for (var k = 0; k < SimulationGrid.MaxNeighborsPerCell; k++)
                {
                    var nb = _grid.Neighbors[baseIndex + k];
                    if (nb == SimulationGrid.NoNeighbor || _visitStamp[nb] == _stamp) continue;
                    _visitStamp[nb] = _stamp;
                    _visited.Add(nb);
                }
            }
            ringStart = ringEnd;
        }
        var seaCells = 0;
        var deepCells = 0;
        foreach (var cell in _visited)
        {
            if (!IsSea(state, lakeOf, cell)) continue;
            seaCells++;
            if (state.WaterDepth[cell] > WaveBaseDepthMeters) deepCells++;
        }
        var exposure = seaCells > 0 ? deepCells / (double)seaCells : 0;
        var maxFill = MaxDeltaFillPerStepMeters * (1 - WaveDeltaDamping * exposure);
        // 由近到遠墊（_visited 是一圈一圈加進來的）；三角洲只往海裡長，不越過陸地
        foreach (var cur in _visited)
        {
            if (put >= amount) break;
            if (!IsSea(state, lakeOf, cur) || _surface[cur] < deepest) continue;
            var add = Math.Min(Math.Min(maxFill, top - _surface[cur]), amount - put);
            if (add <= 0) continue;
            state.SedimentThickness[cur] += (float)add;
            _surface[cur] += (float)add;
            put += add;
        }
        _deltaSupply[mouth] += (float)(put / deltaMyr);
        _deltaDeposited += put;
        return put;
    }

    /// <summary>主洋的海（湖不算）。</summary>
    private static bool IsSea(SimulationState state, int[] lakeOf, int c) => state.WaterDepth[c] > 0f && lakeOf[c] < 0;

    /// <summary>
    /// 進海的沉積物：從河口一圈一圈往外（陸地也走過去、但只填海格），把每一格墊到 <see cref="ShelfDepthMeters"/>——堆出來的是往外長的大陸棚。
    /// 走到 <see cref="_maxSeaHops"/> 圈還有剩，第二輪把走過的格子墊到接近海面，再有剩才平均鋪上去。回傳堆下去的量（等於 <paramref name="amount"/>）。
    ///
    /// 第一版是「往最深的海鄰居一格一格走，走不下去就整包倒在那裡」：碰到封閉的海溝或走到上限就倒在一格，
    /// 40 Myr 時最高的 300 格陸地平均有 5.9 公里的沉積物（P7，2026-10-19）。
    /// </summary>
    private double DepositInSea(SimulationState state, int[] lakeOf, int start, double amount)
    {
        var shelf = state.SeaLevelMeters - ShelfDepthMeters;
        var remaining = amount;
        _stamp++;
        _visited.Clear();
        _visited.Add(start);
        _visitStamp[start] = _stamp;
        var ringStart = 0;
        for (var ring = 0; ring <= _maxSeaHops && ringStart < _visited.Count; ring++)
        {
            var ringEnd = _visited.Count;
            for (var i = ringStart; i < ringEnd; i++)
            {
                var cur = _visited[i];
                if (IsSea(state, lakeOf, cur))
                {
                    var room = shelf - _surface[cur];
                    if (room > 0f)
                    {
                        var put = Math.Min(room, remaining);
                        state.SedimentThickness[cur] += (float)put;
                        _surface[cur] += (float)put;
                        remaining -= put;
                        if (remaining <= 0) return amount;
                    }
                }
                if (ring == _maxSeaHops) continue;
                // 陸地也走（只是不填）：水深是上一步的，板塊動過之後河口可能接到一兩格孤立的小水窪，
                // 只走海格的話整包泥沙倒在那一格，堆出 20 公里的沉積柱（P7，2026-10-19）
                var baseIndex = cur * SimulationGrid.MaxNeighborsPerCell;
                for (var k = 0; k < SimulationGrid.MaxNeighborsPerCell; k++)
                {
                    var nb = _grid.Neighbors[baseIndex + k];
                    if (nb == SimulationGrid.NoNeighbor || _visitStamp[nb] == _stamp) continue;
                    _visitStamp[nb] = _stamp;
                    _visited.Add(nb);
                }
            }
            ringStart = ringEnd;
        }
        // 大陸棚都滿了：第二輪墊到接近海面
        _visited.RemoveAll(cell => !IsSea(state, lakeOf, cell));
        if (_visited.Count == 0) _visited.Add(start);
        var filled = state.SeaLevelMeters - FilledSeaDepthMeters;
        foreach (var cell in _visited)
        {
            var room = filled - _surface[cell];
            if (room <= 0f) continue;
            var put = Math.Min(room, remaining);
            FallbackDeposits[0] += put;
            state.SedimentThickness[cell] += (float)put;
            _surface[cell] += (float)put;
            remaining -= put;
            if (remaining <= 0) return amount;
        }
        // 連海面都到了（極少見）：平均鋪上去
        FallbackDeposits[1] += remaining;
        var each = (float)(remaining / _visited.Count);
        foreach (var cell in _visited)
        {
            state.SedimentThickness[cell] += each;
            _surface[cell] += each;
        }
        return amount;
    }

    /// <summary>
    /// 從 <paramref name="cell"/> 沿流向往下游送（不再刨蝕）：到海就 <see cref="DepositInSea"/>，到存水的湖就記進那個湖待處理。
    /// 回傳這一趟已經堆下去的量（進湖的那一份之後才堆，不算在這裡）。
    /// </summary>
    private double RouteDownstream(SimulationState state, Hydrology hydrology, int cell, double amount)
    {
        var lakeOf = hydrology.LakeOf;
        var basinOf = hydrology.BasinOf;
        var parent = hydrology.FlowParent;
        var last = cell;
        for (var guard = 0; cell >= 0 && guard < state.CellCount; guard++)
        {
            if (IsSea(state, lakeOf, cell)) return DepositInSea(state, lakeOf, cell, amount);
            var b = lakeOf[cell] >= 0 ? lakeOf[cell] : basinOf[cell];
            if (b >= 0 && hydrology.HoldsWater(b) && _lakeCellCount[b] > 0)
            {
                _basinLoad[b] += amount;
                return 0;
            }
            last = cell;
            cell = parent[cell];
        }
        if (last < 0) return 0;
        return DepositNearestSea(state, lakeOf, last, amount, 2);
    }

    /// <summary>
    /// 沒有流向可走的泥沙：找最近的海格交給 <see cref="DepositInSea"/>；附近都沒有海（極少見）才就地堆。
    /// </summary>
    private double DepositNearestSea(SimulationState state, int[] lakeOf, int cell, double amount, int path)
    {
        _stamp++;
        _visited.Clear();
        _visited.Add(cell);
        _visitStamp[cell] = _stamp;
        for (var i = 0; i < _visited.Count && i < MaxNearestSeaSearch; i++)
        {
            var cur = _visited[i];
            if (IsSea(state, lakeOf, cur)) return DepositInSea(state, lakeOf, cur, amount);
            var baseIndex = cur * SimulationGrid.MaxNeighborsPerCell;
            for (var k = 0; k < SimulationGrid.MaxNeighborsPerCell; k++)
            {
                var nb = _grid.Neighbors[baseIndex + k];
                if (nb == SimulationGrid.NoNeighbor || _visitStamp[nb] == _stamp) continue;
                _visitStamp[nb] = _stamp;
                _visited.Add(nb);
            }
        }
        FallbackDeposits[2] += amount;
        if (amount > LargestDump.Amount) LargestDump = (path, cell, amount);
        state.SedimentThickness[cell] += (float)amount;
        _surface[cell] += (float)amount;
        return amount;
    }

    /// <summary>找最近的海最多看幾格。</summary>
    private const int MaxNearestSeaSearch = 5000;

    /// <summary>
    /// 拿掉相當於地表降低 <paramref name="surfaceLoweringMeters"/> 公尺的物質：先沉積物（換算均衡反彈）、再凸起的褶皺（1:1，褶皺沒有均衡補償），
    /// 最後基岩（換算均衡反彈，見 <see cref="Erosion"/>）。回傳拿掉的物質厚度（公尺，之後是下游的沉積物）與其中褶皺的那一份。
    /// </summary>
    private (double Removed, double FromRelief) RemoveMaterial(SimulationState state, int cell, double surfaceLoweringMeters)
    {
        var remaining = surfaceLoweringMeters;
        var removed = 0.0;

        var sediment = state.SedimentThickness[cell];
        if (sediment > 0f)
        {
            // 沉積物也是均衡補償的載重：拿掉 1 公尺只降 (ρ地函 − ρ沉積)/ρ地函 ≈ 0.24 公尺（見 Isostasy.LoadDepressionMeters）
            var take = Math.Min(sediment, remaining / SedimentSurfaceRatio);
            state.SedimentThickness[cell] = (float)(sediment - take);
            removed += take;
            remaining -= take * SedimentSurfaceRatio;
        }

        var fromRelief = 0.0;
        var relief = state.StructuralRelief[cell];
        if (remaining > 0 && relief > 0f)
        {
            fromRelief = Math.Min(relief, remaining);
            state.StructuralRelief[cell] = (float)(relief - fromRelief);
            removed += fromRelief;
            remaining -= fromRelief;
            Lithology.StripCover(state, cell, fromRelief); // 褶皺就是最上面那層岩石
        }
        if (remaining <= 0) return (removed, fromRelief);

        var crustRemovedMeters = remaining / Isostasy.IsostaticSurfaceRatio(state.Crust[cell]);
        var crustKm = state.CrustThickness[cell];
        var takeKm = Math.Min(crustKm - Isostasy.MinCrustThicknessKm, crustRemovedMeters / 1000.0);
        if (takeKm <= 0) return (removed, fromRelief);
        state.CrustThickness[cell] = (float)(crustKm - takeKm);
        CrustErodedKm += takeKm;
        // 蓋層刨穿了才露出基盤：被抬升、侵蝕得夠深的山脈核心自然會露出結晶岩（水文地貌規劃 H1）
        Lithology.StripCover(state, cell, takeKm * 1000.0 * Isostasy.IsostaticSurfaceRatio(state.Crust[cell]));
        return (removed + takeKm * 1000.0, fromRelief);
    }

    /// <summary>
    /// 有幾個鄰居是夠深的海（受浪的邊數，見 <see cref="WaveBaseDepthMeters"/>）。
    ///
    /// 只算外面是深水的邊：寬的大陸棚、淺海會把浪的能量消耗掉，海岸後退會自己慢下來。第一版每一條面海的邊都算，
    /// 低平原被海浪一路往內陸削（削到海平面、變海、下一格又面海），40 Myr 時兩塊大陸北緯 50 度以上的大陸地殼有一半變成淺海（P7，2026-10-20）。
    /// </summary>
    private int SeaNeighborCount(SimulationState state, int[] lakeOf, int cell)
    {
        var count = 0;
        var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
        for (var k = 0; k < SimulationGrid.MaxNeighborsPerCell; k++)
        {
            var nb = _grid.Neighbors[baseIndex + k];
            if (nb != SimulationGrid.NoNeighbor && IsSea(state, lakeOf, nb) && state.WaterDepth[nb] > WaveBaseDepthMeters) count++;
        }
        return count;
    }
}
