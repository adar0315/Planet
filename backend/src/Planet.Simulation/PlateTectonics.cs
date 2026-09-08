namespace Planet.Simulation;

/// <summary>
/// 內營力：板塊運動、隱沒、碰撞造山、海底擴張。把一顆全是海洋的星球演化出陸地。
///
/// ## ⚠️ 地殼的性質掛在「標記」上，不是掛在格子上（marker-in-cell）
///
/// 這是整個階段最關鍵的離散化決定，而且**踩過一次很難看出來的坑**（2026-09-05）。
///
/// 問題：格子中心間距約14.9公里，板塊速度1~10公分/年，一步5萬年只前進0.5~5公里，
/// 比一格還短。直覺的做法是「累積位移，超過一格才把整格的資料搬過去」——
/// 但球面剛體旋轉的位移是 <c>角度 × R × sin(離歐拉極的角距)</c>，
/// **在板塊上是連續變化的**：離歐拉極90度的地方移動一整格，靠近極點的地方趨近0。
/// 而整格跳躍是量化的。不管怎麼安排跳躍的時機（每板塊一個門檻、或每格各自累積都試過），
/// 都會在「已經跳了」與「還沒跳」的區帶之間撕出裂縫，而那些裂縫又被當成張裂/聚合，
/// 憑空生出中洋脊與隱沒帶。症狀是貼圖上滿滿繞著歐拉極的弧形細絲、陸地散成斑點，
/// 但**陸地「比例」看起來完全正常**——只看統計量絕對抓不到，一定要把貼圖畫出來看。
///
/// 解法（也是地球動力學數值模擬的標準做法）：地殼的性質掛在**可以連續移動的標記**上。
/// 標記的位置永遠是精確的浮點座標、每步依歐拉極旋轉，**從不對齊到格子中心**；
/// 格子只是週期性地去「取樣」標記。量化只發生在取樣這一步，而且不累積誤差。
///
/// 於是空缺與碰撞只會出現在流場真的發散／收斂的地方，也就是**真正的板塊邊界**：
///
/// - 兩個標記落在同一格 → 聚合，依浮力決定誰隱沒（<see cref="ResolveCollision"/>）
/// - 一格裡一個標記都沒有 → 張裂 → 新生海洋地殼，年齡歸零（海底擴張）
///
/// 地質作用改的是**格子**上的值（那裡才有鄰居關係可用），改完再依 <c>_ownerMarker</c>
/// 寫回標記，否則下次取樣就會把這一步的地質作用整個蓋掉。
///
/// ## 大陸能不能長出來，取決於四件事同時成立
///
/// 這一段每一項都是實測踩出來的，缺任何一項的症狀都是「跑幾億年還是一片海洋」或
/// 「長出滿天單格孤島而不是大陸」，而且都**不會有任何錯誤訊息**（2026-09-05）：
///
/// 1. 碰撞的勝負要看**浮力（厚度）**而不只是年齡 —— <see cref="ResolveCollision"/>
/// 2. 隱沒的地殼要有一部分**增生**到上盤，而不是整塊消失 —— <see cref="Accrete"/>
/// 3. 搬動不能有量化誤差，否則假的張裂／聚合會淹沒真的 —— 見上面的 marker-in-cell 那節
/// 4. 板塊配置要在**無淨旋轉參考系**下，否則整片地殼一起掃過網格製造雜訊
///    —— <c>PlateSet.RemoveNetRotation</c>
/// 5. 起始地殼要有**大尺度的不均勻**當作大陸的核心 —— <c>SimulationState.CreateInitialOceanWorld</c>
/// </summary>
public sealed class PlateTectonics
{
    /// <summary>
    /// 標記漂移超過幾倍的「格子中心間距」就重新取樣一次。
    ///
    /// 這個值**只影響邊界更新的頻率與成本，不影響正確性**——標記的位置一直是精確的連續值，
    /// 取樣得疏一點不會累積任何誤差（這正是 marker-in-cell 相對於「整格跳躍」的好處）。
    /// 取0.5：邊界大約每移動半格就重新判斷一次，夠即時又不會把時間都花在最近格搜尋上。
    ///
    /// **單位是中心間距而不是邊長**，這裡踩過一次坑：最初用「0.5倍邊長」當門檻，
    /// 結果板塊完全不動——六角格的中心間距是邊長的√3倍，0.5倍邊長只有0.29倍中心間距，
    /// 每次都四捨五入回原本那一格，而且不會有任何錯誤訊息。
    /// </summary>
    private const double RasterizeThresholdInCellSpacings = 1.0;

    /// <summary>
    /// 聚合時每前進1公里，地殼增厚幾公里。
    ///
    /// 校準目標：以5公分/年（＝50公里/百萬年）的聚合速率，讓地殼在約5,000萬年內從35公里增厚到70公里
    /// （地球上喜馬拉雅造山的量級）。0.7 ÷ 50 ≈ 0.014。
    /// 這個值是**調出來的**而不是推導的——真實造山帶的縮短量怎麼分配到增厚上非常複雜，
    /// 這裡只要求結果的量級與時間尺度像地球。
    /// </summary>
    private const double ThickeningPerConvergence = 0.014;

    /// <summary>相對運動中，垂直於邊界的分量要佔多少比例才算聚合/張裂，低於此視為轉形（以錯動為主）。</summary>
    private const double NormalComponentRatioForTransform = 0.3;

    /// <summary>
    /// 比浮力時，均衡高度差要超過這個值（公尺）才算「明顯浮得比較高」，否則改用年齡當判準。
    /// 留這個容差是因為全球絕大多數海洋地殼的條件都完全一樣，直接比浮點數大小會讓勝負由捨入誤差決定，
    /// 同樣的種子在不同硬體上可能演化出不同的星球。
    /// </summary>
    private const float BuoyancyToleranceMeters = 5f;

    /// <summary>
    /// 聚合處要有多大的均衡高度差（公尺）才算「隱沒」而不是「對撞增厚」。
    ///
    /// 取500：海洋地殼與大陸地殼的高度差是數千公尺，一定算隱沒；
    /// 兩塊條件相近的地殼擠在一起則是造山，不該讓其中一塊憑空消失。
    /// </summary>
    private const float SubductionBuoyancyContrastMeters = 500f;

    /// <summary>
    /// 地殼厚度超過這個值（公里）才會開始重力擴展。
    ///
    /// **判準是「自己的絕對厚度」而不是「跟鄰居的厚度差」**，這裡來回踩過兩次（2026-09-05／06）：
    ///
    /// 中途一度改成用厚度差當判準，理由是想解決「大陸散成碎片」——但那個碎片其實是搬動的
    /// 離散化假象造成的（見類別說明的 marker-in-cell 那節），歸錯了因。用厚度差的後果是
    /// **正常的大陸邊緣永遠在流失**：一塊35公里的大陸旁邊必然貼著7公里的海洋地殼，
    /// 落差28公里永遠超過任何合理的門檻，於是大陸一路被磨平——實測起始4.55%的陸地
    /// 到6億年只剩0.35%，而且完全沒有錯誤訊息。
    ///
    /// 真實的大陸邊緣之所以穩定，是因為大陸已經處在均衡狀態、基底沒有驅動的壓力梯度；
    /// 會側向流動的是**超出自身均衡厚度**的部分（造山帶重力崩解／orogenic collapse），
    /// 這也是西藏是一片寬闊高原而不是一根尖峰的原因。
    ///
    /// 取40：略高於地球大陸的典型35~40公里（所以正常的大陸不會平白流掉），
    /// 但低於75公里的物理上限（所以碰撞帶不會先撞到上限、把超出的地殼硬切掉）。
    ///
    /// **這個值目前實質上決定了大陸的平衡厚度**，因為階段2還沒有侵蝕作用——
    /// 地殼增厚唯一的出口就是重力擴展。實測門檻50時大陸平均厚達57.9公里（地球是35~40），
    /// 所以取40。等階段4補上侵蝕這個真正的物質移除機制之後，這個門檻應該可以放寬回去。
    /// </summary>
    private const float GravitationalSpreadingThresholdKm = 40f;

    /// <summary>
    /// 重力擴展每百萬年流走「超厚部分」的比例。
    ///
    /// 取1.0（特徵時間約100萬年）而不是更小的值，是因為**這個速率必須快過增生**，
    /// 否則地殼會一路頂到 <see cref="Isostasy.MaxCrustThicknessKm"/> 的上限，
    /// 而那個上限是用 <c>MathF.Min</c> 硬切的——超出的地殼會被直接丟掉，
    /// 形成一個不物理的質量漏洞，整顆星球的陸地比例會被這個漏洞決定而不是被地質決定
    /// （2026-09-05 實測：擴展速率0.1時最大厚度死貼在74.8，陸地比例在5.9%卡住整整10億年）。
    /// 取0.5：平衡厚度是「門檻 ＋ 增生速率 ÷ 這個值」，實測增生約1.4公里/百萬年，
    /// 速率0.1會讓大陸平衡在53.6公里（太厚），0.5則落在40幾公里、接近地球的量級。
    /// 用絕對超厚量當判準時，把這個值調快是安全的——低於門檻的正常大陸超厚量是0、根本不會流。
    /// **這個值跟 <see cref="GravitationalSpreadingThresholdKm"/> 是綁在一起的**，
    /// 改其中一個一定要重測另一個——這兩個值中途一起改錯過，症狀是大陸被抹平到23公里、
    /// 全部沉到水面下，陸地比例0.00%（2026-09-05 實測）。
    /// </summary>
    private const double GravitationalSpreadingRatePerMyr = 0.5;

    /// <summary>
    /// 隱沒的地殼有多少比例被刮下來增生到上盤，其餘回到地函。
    ///
    /// 取0.03。原本取0.1（地球上增生楔捕獲的比例大約一~三成），但那是「每次隱沒事件」的比例，
    /// 換算到這個模型的取樣頻率之後全球增生速率變成約1.4公里/百萬年——
    /// 而地球的島弧增生（約1~3 km³/年）按本星球表面積縮放後只有約0.04公里/百萬年，**強了35倍**，
    /// 大陸因此一路增厚到50公里以上（地球是35~40）。
    ///
    /// 早期之所以不敢調小，是因為調小就完全長不出大陸；但那是**起始條件太均勻**造成的，
    /// 現在有早期大轟炸留下的原始陸塊當核心（見 <c>SimulationState.ApplyEarlyBombardment</c>），
    /// 增生只需要讓大陸長大，不必再負責無中生有。
    /// </summary>
    private const float AccretedFractionOfSubductedCrust = 0.03f;

    /// <summary>
    /// 兩塊大陸對撞時，被擠掉的那一塊有多少厚度轉移到留下的那一塊。
    /// 取0.5：大陸地殼太輕、誰都沉不下去，物質只能往上下擠成山脈與山根——
    /// 這是大陸變厚最主要的機制（喜馬拉雅型造山），跟隱沒的3%增生完全不同量級。
    /// </summary>
    private const float ContinentalCollisionTransfer = 0.5f;

    /// <summary>
    /// 取樣錯配的空格向鄰居借地殼時，實際從鄰居扣掉幾成（見 <see cref="BorrowFromNeighbours"/>）。
    ///
    /// 取0（借了不扣，等同標準 MPM 的重新播種）：1.0 是完全守恆，但實測會讓整顆星球被磨到
    /// 只剩地殼下限、100%海洋。改成一格多個標記之後這條路徑已經很少走到
    /// （錯配空格從86/步降到2.6/步），這個取捨的影響也跟著小了一個數量級。
    /// </summary>
    private const float MisfitDeductionFraction = 0.0f;

    /// <summary>
    /// 每格放幾個標記。
    ///
    /// **試過4，最後回到1**（2026-09-06/07）。
    ///
    /// 改成4的動機是對的：一格一個標記時，球面剛體旋轉映射到離散網格不是完美排列，
    /// 每步約1.5%的格子拿不到任何標記（錯配空格），而那些格子只能憑空生地殼或跟鄰居借。
    /// 改成4之後錯配空格從86/步降到2.6/步，效果確實顯著。
    ///
    /// 但一格多個標記時，格子的厚度只能由「落在裡面的標記數量」推導（總和÷N），
    /// 於是**每次取樣的最近格量化雜訊都會把密集的聚集抹開**——實測取樣每步移除
    /// 0.037公里的大陸厚度，是島弧增厚（+0.0057）的6.5倍。把島弧增厚與增生各放大3倍
    /// 完全沒有反應，因為源頭再大也填不滿一個大6倍的洞。
    /// 結果是大陸地殼只有12~14公里、只比海洋厚1.6倍（單標記版是4~5倍），雙峰幾乎消失。
    ///
    /// 一格一個標記時厚度直接跟著標記走、不由數量推導，就沒有這個擴散。
    /// 錯配空格改用下面的「配對」機制處理（多的搬去補少的，守恆）。
    ///
    /// 要再往多標記走的話，得先換一套不靠數量推導厚度的取樣方式，那是獨立的題目。
    /// </summary>
    private const int MarkersPerCell = 1;

    /// <summary>
    /// 標記陣列的容量係數。聚合處標記會擠在一起、張裂處會新生，總數在
    /// 「格數×<see cref="MarkersPerCell"/>」上下浮動，留五成餘裕。
    /// </summary>
    /// 標記總數的平衡點約是「格數 × MarkersPerCell × 平均地殼厚 ÷ 新生地殼厚」，
    /// 地殼厚到 40 公里時就是 4×40/7 ≈ 23 倍的格數，所以餘裕要留得比直覺多很多——
    /// 撞到上限時 AllocateMarker 會回 -1、中洋脊補標記靜靜失敗，症狀是標記數卡在一個整數
    /// （實測卡在剛好 6.00 倍格數）而地殼停止增厚（2026-09-06）。
    private const int MarkerCapacityFactor = 12;


    private readonly double[] _convergenceRate; // 公尺／百萬年，每步重算
    private readonly int[] _convergingNeighbor; // 這格主要在跟哪一格聚合，-1 表示沒有
    /// <summary>
    /// 這個邊界格分隔的「另一個」板塊。用來判斷兩個相鄰的邊界格是不是在**同一條邊**上——
    /// 不是的話它們就分屬兩條邊，中間那個轉折就是接合點。非邊界格是 −1。
    /// </summary>
    private readonly int[] _boundaryOtherPlate;

    /// <summary>這個聚合格屬於哪一條線段。非聚合格是 −1。見 <c>TraceConvergentSegments</c>。</summary>
    private readonly int[] _segmentId;

    /// <summary>這一格離所屬線段端點有幾格（泛洪距離）。用來讓效果在線段兩端衰減。</summary>
    private readonly int[] _hopsToSegmentEnd;

    /// <summary>線段的作用權重（0~1）：長度不足或靠近端點就打折。</summary>
    private readonly float[] _convergenceWeight;

    /// <summary>整條線段的平均聚合速率（公尺／百萬年）。取代逐格各自為政的值。</summary>
    private readonly double[] _segmentMeanConvergence;

    /// <summary>泛洪用的佇列。每條線段重複使用，成員就是它的前 count 個元素。</summary>
    private readonly int[] _floodQueue;

    /// <summary>量「離線段端點幾格」用的廣度優先佇列。</summary>
    private readonly int[] _endDistanceQueue;

    /// <summary>這一格這一步供應多少地殼（公里）。只有聚合線上的上盤格子是正值。</summary>
    private readonly double[] _orogenBudget;

    /// <summary>這一格屬於哪一條線段的造山帶。不在任何帶內是 −1。</summary>
    private readonly int[] _orogenSegment;

    /// <summary>這一格離造山帶所屬的那條線幾格。線上是0。</summary>
    private readonly int[] _orogenHops;

    /// <summary>造山帶泛洪用的佇列。</summary>
    private readonly int[] _orogenQueue;

    /// <summary>每條線段這一步的地殼預算（公里）。索引是線段編號。</summary>
    private readonly double[] _segmentBudget;

    /// <summary>每條線段的造山帶上，分配曲線的總和。用來把預算正規化，總量才不會變。</summary>
    private readonly double[] _segmentProfileSum;

    private readonly CrustType[] _crustSnapshot;

    /// <summary>
    /// 聚合判定用的基岩高度快照。跟 <c>_crustSnapshot</c> 同樣的理由——
    /// <c>ApplyConvergence</c> 的平行迴圈會寫自己的厚度、又要讀鄰居的厚度。
    /// </summary>
    private readonly float[] _liftSnapshot;
    private readonly float[] _spreadBuffer; // 重力擴展的 out-of-place 暫存
    private readonly double[] _arcAdded;   // 診斷用：每格島弧增厚了多少

    // 標記（marker）：地殼的性質掛在這裡，位置是連續的，見類別說明。
    // 容量給格數的兩倍：聚合處標記會擠在一起、張裂處會新生，總數在格數上下浮動，留一倍餘裕。
    private readonly double[] _markerX;
    private readonly double[] _markerY;
    private readonly double[] _markerZ;
    private readonly float[] _markerThickness;
    private readonly float[] _markerAge;
    private readonly float[] _markerDensity;
    // 礁灰岩的厚度（公尺）。它是地殼柱的一部分，所以**必須跟地殼一起漂**，
    // 不能像以前那樣掛在格子上——見 CoralReefs 類別說明裡的「化石紀錄」那一段。
    private readonly float[] _markerOrganic;
    private readonly CrustType[] _markerCrust;
    private readonly bool[] _markerOceanicOnly; // 永遠不能變成大陸地殼，見 SimulationState.OceanicOnly
    private readonly int[] _markerPlate;
    private readonly bool[] _markerAlive;
    private readonly int[] _markerTarget; // 這個標記這一輪落在哪一格
    private readonly int[] _markerCell;   // 上一輪落在哪一格，局部搜尋的起點
    private readonly int[] _remap;        // 壓實時的「舊索引 → 新索引」
    private int _markerCount;

    // 每格的標記清單（CSR：start/count 指進 _markersByCell），每次取樣重建
    private readonly int[] _cellMarkerStart;
    private readonly int[] _cellMarkerCount;
    private readonly int[] _markersByCell;

    // 取樣當下寫進格子的值。地質作用改完格子之後要用「差值」寫回標記，
    // 直接覆蓋會讓同一格裡的所有標記變成一模一樣，多個標記就白費了。
    private readonly float[] _sampledThickness;
    private readonly float[] _sampledAge;
    private readonly float[] _sampledDensity;

    /// <summary>診斷用：最近一步各來源新增的地殼厚度總和（公里·格），用來找出誰在超產。</summary>
    public double LastRidgeCrustKm { get; private set; }

    /// <summary>診斷用：島弧增厚新增的地殼厚度總和（公里·格）。</summary>
    public double LastArcCrustKm { get; private set; }

    /// <summary>診斷用：增生新增的地殼厚度總和（公里·格）。</summary>
    public double LastAccretionCrustKm { get; private set; }

    /// <summary>診斷用：隱沒消耗掉的地殼厚度總和（公里·格，正值代表移除）。</summary>
    public double LastSubductedCrustKm { get; private set; }

    /// <summary>診斷用：目前存活的標記數量。跟格數比對可以看出標記有沒有異常增生。</summary>
    public int MarkerCount => _markerCount;

    /// <summary>診斷用：所有存活標記的厚度總和（公里）。</summary>
    public double TotalMarkerThicknessKm
    {
        get
        {
            var total = 0.0;
            for (var m = 0; m < _markerCount; m++)
            {
                if (_markerAlive[m]) total += _markerThickness[m];
            }
            return total;
        }
    }


    /// <summary>診斷用：取樣錯配的空格沿用舊厚度時，總共注入了多少地殼（公里·格）。</summary>
    public double LastMisfitFillCrustKm { get; private set; }

    /// <summary>診斷用：取樣錯配的空格有幾個。</summary>
    public int LastMisfitFillCount { get; private set; }

    /// <summary>診斷用：其中有幾個連一個鄰居標記都借不到（會退回最薄地殼）。</summary>
    public int LastNoDonorCount { get; private set; }


    /// <summary>
    /// 每一格「擠不下、暫時無主」的標記（-1 表示沒有）。取樣錯配造成的多餘標記先放這裡，
    /// 稍後配給附近的錯配空格，見 <see cref="ResolveCollision"/>。
    /// </summary>
    private readonly int[] _spareAtCell;

    /// <summary>每一格目前是被哪個標記取樣到的（-1 表示沒有）。地質作用改完格子要靠它寫回標記。</summary>
    private readonly int[] _ownerMarker;

    /// <summary>自上次取樣以來，最快的標記大約漂移了多少公尺。超過門檻就重新取樣。</summary>
    private double _driftSinceRasterMeters;

    /// <summary>距離上次重新取樣過了多少百萬年。中洋脊的火山活動速率要用它當分母。</summary>
    private double _myrSinceLastRaster;

    /// <summary>建立內營力模擬器。建好之後要呼叫 <see cref="Reset"/> 才會有標記。</summary>
    /// <param name="grid">網格。</param>
    /// <param name="plateCount">板塊數量。</param>
    public PlateTectonics(SimulationGrid grid, int plateCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(plateCount);

        _convergenceRate = new double[grid.CellCount];
        _convergingNeighbor = new int[grid.CellCount];
        _boundaryOtherPlate = new int[grid.CellCount];
        _segmentId = new int[grid.CellCount];
        _hopsToSegmentEnd = new int[grid.CellCount];
        _convergenceWeight = new float[grid.CellCount];
        _segmentMeanConvergence = new double[grid.CellCount];
        _floodQueue = new int[grid.CellCount];
        _endDistanceQueue = new int[grid.CellCount];
        _orogenBudget = new double[grid.CellCount];
        _orogenSegment = new int[grid.CellCount];
        _orogenHops = new int[grid.CellCount];
        _orogenQueue = new int[grid.CellCount];
        _segmentBudget = new double[grid.CellCount];
        _segmentProfileSum = new double[grid.CellCount];
        _crustSnapshot = new CrustType[grid.CellCount];
        _liftSnapshot = new float[grid.CellCount];
        _spreadBuffer = new float[grid.CellCount];
        _arcAdded = new double[grid.CellCount];
        _ownerMarker = new int[grid.CellCount];
        _spareAtCell = new int[grid.CellCount];

        var capacity = grid.CellCount * MarkersPerCell * MarkerCapacityFactor / 2;
        _cellMarkerStart = new int[grid.CellCount];
        _cellMarkerCount = new int[grid.CellCount];
        // 每格至少保留 MarkersPerCell 個位置，擁擠的格子再多佔一些，所以要比標記總數更寬裕
        _markersByCell = new int[capacity + grid.CellCount * MarkersPerCell];
        _sampledThickness = new float[grid.CellCount];
        _sampledAge = new float[grid.CellCount];
        _sampledDensity = new float[grid.CellCount];
        _markerX = new double[capacity];
        _markerY = new double[capacity];
        _markerZ = new double[capacity];
        _markerThickness = new float[capacity];
        _markerAge = new float[capacity];
        _markerDensity = new float[capacity];
        _markerOrganic = new float[capacity];
        _markerCrust = new CrustType[capacity];
        _markerOceanicOnly = new bool[capacity];
        _markerPlate = new int[capacity];
        _markerAlive = new bool[capacity];
        _markerTarget = new int[capacity];
        _markerCell = new int[capacity];
        _remap = new int[capacity];
    }

    /// <summary>依目前的狀態重建標記：每格一個，放在格子中心。</summary>
    /// <param name="grid">網格。</param>
    /// <param name="state">模擬狀態，標記的初始性質由它決定。</param>
    /// <param name="plates">板塊配置。</param>
    public void Reset(SimulationGrid grid, SimulationState state, PlateSet plates)
    {
        _markerCount = grid.CellCount * MarkersPerCell;
        _driftSinceRasterMeters = 0;
        _myrSinceLastRaster = 0;

        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            var cx = grid.UnitVector[cell * 3];
            var cy = grid.UnitVector[cell * 3 + 1];
            var cz = grid.UnitVector[cell * 3 + 2];
            var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;

            for (var k = 0; k < MarkersPerCell; k++)
            {
                var marker = cell * MarkersPerCell + k;

                // 第0個放正中心，其餘往不同鄰居的方向偏一點，讓同一格的標記散開。
                // 全部疊在中心的話，它們會永遠一起移動、等同於只有一個標記。
                var x = cx;
                var y = cy;
                var z = cz;
                if (k > 0)
                {
                    var neighbor = grid.Neighbors[baseIndex + (k - 1) % SimulationGrid.MaxNeighborsPerCell];
                    if (neighbor != SimulationGrid.NoNeighbor)
                    {
                        const double offset = 0.3; // 偏向鄰居三成，仍在自己這一格內
                        x += (grid.UnitVector[neighbor * 3] - cx) * offset;
                        y += (grid.UnitVector[neighbor * 3 + 1] - cy) * offset;
                        z += (grid.UnitVector[neighbor * 3 + 2] - cz) * offset;
                        var length = Math.Sqrt(x * x + y * y + z * z);
                        x /= length; y /= length; z /= length;
                    }
                }

                _markerX[marker] = x;
                _markerY[marker] = y;
                _markerZ[marker] = z;
                _markerThickness[marker] = state.CrustThickness[cell];
                _markerAge[marker] = state.CrustAge[cell];
                _markerDensity[marker] = state.CrustDensity[cell];
                _markerOrganic[marker] = state.OrganicThickness[cell];
                _markerCrust[marker] = state.Crust[cell];
                _markerOceanicOnly[marker] = state.OceanicOnly[cell];
                _markerPlate[marker] = plates.PlateIdByCell[cell];
                _markerAlive[marker] = true;
                _markerCell[marker] = cell;
            }

            _sampledThickness[cell] = state.CrustThickness[cell];
            _sampledAge[cell] = state.CrustAge[cell];
            _sampledDensity[cell] = state.CrustDensity[cell];
            _ownerMarker[cell] = cell * MarkersPerCell;
        }
    }

    /// <summary>
    /// 推進一步內營力。
    ///
    /// **不含**地殼老化、均衡換算高度與注水——那些是所有地質作用共同的收尾
    /// （階段3的水循環、階段4的侵蝕改的也是同一組欄位），由 <see cref="SimulationEngine"/> 統一做，
    /// 這樣火山等其他作用才能排在同一步的均衡之前生效。
    /// </summary>
    /// <param name="grid">網格。</param>
    /// <param name="state">模擬狀態，會被就地更新。</param>
    /// <param name="plates">板塊配置，格子的板塊歸屬會隨搬動而改變。</param>
    /// <param name="deltaYears">這一步代表多少年。</param>
    public void Step(SimulationGrid grid, SimulationState state, PlateSet plates, double deltaYears)
    {
        var deltaMyr = deltaYears / 1_000_000.0;
        LastRidgeCrustKm = 0;
        LastMisfitFillCrustKm = 0;
        LastMisfitFillCount = 0;
        LastPairedFillCount = 0;
        LastNoDonorCount = 0;
        LastArcCrustKm = 0;
        LastAccretionCrustKm = 0;
        LastSubductedCrustKm = 0;

        // 板塊的範圍與邊界類型只取決於板塊的幾何與速度，跟地殼無關，所以先算。
        // **順序很重要**：取樣時要靠邊界類型才能分辨「這個空格是真的張裂，還是只是取樣的錯配」，
        // 只有真張裂處才該生成新的海洋地殼。
        plates.Advance(grid, deltaMyr);
        ClassifyBoundaries(grid, state, plates);

        // **邊界分類完才更新板塊速度**：驅動力來自邊界（板片拉力在隱沒帶、洋脊推力在張裂帶），
        // 所以要先知道邊界在哪、哪一側在隱沒。解出來的速度下一步生效。
        Dynamics.UpdateVelocities(grid, state, plates, _convergingNeighbor, deltaMyr);

        MoveMarkersAndMaybeRasterize(grid, state, plates, deltaMyr);
        ApplyConvergence(grid, state, plates, deltaMyr);
        for (var cell = 0; cell < grid.CellCount; cell++) LastArcCrustKm += _arcAdded[cell];
        SpreadOverthickenedCrust(grid, state, deltaMyr);
        FlowWithinOrogenBand(grid, state, deltaMyr);

        // **這裡刻意不回寫標記**——回寫由 SimulationEngine 在「所有」會改地殼的作用跑完之後統一做。
        // 原本在這裡就回寫，導致排在後面的火山與侵蝕改完地殼之後，下一次 Rasterize 會用
        // 舊的標記內容把它們整個蓋掉。而 Rasterize 的頻率跟解析度有關：
        // n=150 大約每2步就取樣一次，等於**一半的火山產出被靜靜丟掉**（2026-09-06）。
    }

    /// <summary>
    /// 把標記依各自板塊的歐拉極連續旋轉，漂移夠遠時重新取樣到格子上。
    ///
    /// **旋轉每步都做、取樣才是週期性的**：標記位置是精確的連續值，
    /// 取樣頻率只影響邊界更新的即時性與成本，不會累積誤差（見類別說明）。
    /// </summary>
    private void MoveMarkersAndMaybeRasterize(SimulationGrid grid, SimulationState state, PlateSet plates, double deltaMyr)
    {
        _myrSinceLastRaster += deltaMyr;

        var maxAngleRad = 0.0;
        Parallel.For(0, _markerCount, marker =>
        {
            var plate = plates.Plates[_markerPlate[marker]];
            var angle = plate.AngularVelocityDegPerMyr * Math.PI / 180.0 * deltaMyr;

            var (x, y, z) = RotateAboutAxis(
                _markerX[marker], _markerY[marker], _markerZ[marker],
                plate.PoleX, plate.PoleY, plate.PoleZ,
                angle);

            // 每步都重新正規化：幾萬步下來的浮點誤差會讓標記慢慢離開單位球面
            var length = Math.Sqrt(x * x + y * y + z * z);
            _markerX[marker] = x / length;
            _markerY[marker] = y / length;
            _markerZ[marker] = z / length;
        });

        foreach (var plate in plates.Plates)
        {
            var angle = Math.Abs(plate.AngularVelocityDegPerMyr * Math.PI / 180.0 * deltaMyr);
            if (angle > maxAngleRad) maxAngleRad = angle;
        }

        // 最大位移出現在離歐拉極90度處，也就是 角度 × R
        _driftSinceRasterMeters += maxAngleRad * grid.PlanetRadiusMeters;

        var threshold = RasterizeThresholdInCellSpacings * EstimateCellSpacingMeters(grid);
        if (_driftSinceRasterMeters < threshold) return;

        Rasterize(grid, state, plates);
        _driftSinceRasterMeters = 0;
        _myrSinceLastRaster = 0;
    }

    /// <summary>
    /// 把標記取樣到格子上：每個標記落到最近的格子，一格多個標記＝聚合，一格沒有＝張裂。
    /// </summary>
    private void Rasterize(SimulationGrid grid, SimulationState state, PlateSet plates)
    {
        Array.Fill(_ownerMarker, -1, 0, grid.CellCount);
        Array.Fill(_spareAtCell, -1, 0, grid.CellCount);

        // 1. 每個標記算出自己落在哪一格（彼此無相依，可以平行）。
        //
        //    **用局部搜尋而不是全域的 FindNearestCell**：兩次取樣之間標記最多只漂移
        //    半個格子中心間距（<see cref="RasterizeThresholdInCellSpacings"/> 保證的），
        //    所以新的所在格必定是「上次那一格或它的鄰居」，比對7個候選就夠。
        //    全域桶搜尋是這一步最貴的東西——n=150 時每2步就要對22.5萬個標記做一次
        //    （格距只有14.9公里，板塊一步就走4公里），實測整個 PlateTectonics.Step
        //    每步要102ms，幾乎全部花在這裡。
        Parallel.For(0, _markerCount, marker =>
        {
            if (!_markerAlive[marker]) return;

            var x = _markerX[marker];
            var y = _markerY[marker];
            var z = _markerZ[marker];
            var from = _markerCell[marker];

            var best = from;
            var bestDot = x * grid.UnitVector[from * 3]
                + y * grid.UnitVector[from * 3 + 1]
                + z * grid.UnitVector[from * 3 + 2];

            var baseIndex = from * SimulationGrid.MaxNeighborsPerCell;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                var neighbor = grid.Neighbors[baseIndex + s];
                if (neighbor == SimulationGrid.NoNeighbor) continue;

                var dot = x * grid.UnitVector[neighbor * 3]
                    + y * grid.UnitVector[neighbor * 3 + 1]
                    + z * grid.UnitVector[neighbor * 3 + 2];
                if (dot <= bestDot) continue;
                bestDot = dot;
                best = neighbor;
            }

            _markerTarget[marker] = best;
            _markerCell[marker] = best;
        });

        // 2. 依所在格把標記分桶（CSR：先數、做前綴和、再填）。
        //    一格現在會有好幾個標記，不再是「先到先佔、後到的就撞上」。
        Array.Clear(_cellMarkerCount, 0, grid.CellCount);
        for (var marker = 0; marker < _markerCount; marker++)
        {
            if (_markerAlive[marker]) _cellMarkerCount[_markerTarget[marker]]++;
        }

        // **每格至少保留 MarkersPerCell 個位置**，不是剛好配現有的數量：
        // 張裂邊界要能「補到滿」，沒有預留空間的話新標記無處可放（2026-09-06 踩過）。
        var running = 0;
        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            _cellMarkerStart[cell] = running;
            running += Math.Max(_cellMarkerCount[cell], MarkersPerCell);
            _cellMarkerCount[cell] = 0; // 重新當成填入時的游標
        }

        for (var marker = 0; marker < _markerCount; marker++)
        {
            if (!_markerAlive[marker]) continue;
            var cell = _markerTarget[marker];
            _markersByCell[_cellMarkerStart[cell] + _cellMarkerCount[cell]++] = marker;
        }

        // 3. 太擠的格子：只有**真的在聚合邊界**才隱沒掉多出來的標記。
        //    不是聚合邊界的擁擠只是取樣的疏密不均，讓它擠著就好——格子的值是平均，
        //    多幾個標記不會讓地殼憑空變厚，反而是把資訊留著等下一輪自然散開。
        // 3a. 非聚合邊界的擁擠：那只是取樣錯配，不是隱沒。
        //     留下浮得最高的，其餘放進暫存區，稍後補給附近的錯配空格——
        //     同一塊地殼重新指派到正確的格子，天生守恆。
        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            if (state.Boundary[cell] == BoundaryType.Convergent) continue;
            var from0 = _cellMarkerStart[cell];
            while (_cellMarkerCount[cell] > MarkersPerCell)
            {
                var worst = 0;
                var worstLift = float.MaxValue;
                for (var i = 0; i < _cellMarkerCount[cell]; i++)
                {
                    var lift = SubductionBuoyancyOf(_markersByCell[from0 + i]);
                    if (lift >= worstLift) continue;
                    worstLift = lift;
                    worst = i;
                }

                var spare = _markersByCell[from0 + worst];
                if (_spareAtCell[cell] < 0) _spareAtCell[cell] = spare;
                else _markerAlive[spare] = false; // 同一格擠了三個以上，罕見

                _markersByCell[from0 + worst] = _markersByCell[from0 + _cellMarkerCount[cell] - 1];
                _cellMarkerCount[cell]--;
            }
        }

        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            if (state.Boundary[cell] != BoundaryType.Convergent) continue;

            var from = _cellMarkerStart[cell];

            // 聚合處的兩種結局，靠**浮力差**分辨（2026-09-06 第二次修正）：
            //
            // - 浮力差大 → **隱沒**：重的那塊沉下去（海洋潛入大陸、老海床潛入年輕的）
            // - 浮力差小 → **碰撞增厚**：誰都沉不下去，標記擠在一起就是造山
            //   （在「厚度＝總和÷N」的語意下，擁擠本身就是增厚，不需要另外寫規則）
            //
            // 中間一度只留「超過75公里上限才隱沒」，那等於把依浮力挑選的機制整個拿掉——
            // 重板塊因此不再優先被回收，只會累積標記、變厚、浮起來：實測重板塊的地殼
            // 從19.6公里長到32.9（一般海洋只有10.1），密度異常也從+250被稀釋到+27，
            // 「密度高沉得更低」的設定完全失效。
            // 只隱沒「超過目標數」的部分，而且要浮力差夠大才算隱沒。
            //
            // 曾經拿掉數量上限、改成純由浮力差決定，那會一路隱沒到只剩1個標記——
            // 因為混合的格子永遠找得到超過500公尺的浮力差（2026-09-06 實測，大陸厚度沒有改善，
            // 反而把標記掃光）。
            while (_cellMarkerCount[cell] > MarkersPerCell)
            {
                // 找浮得最低的（最容易隱沒）與最高的（上盤，接收增生物）
                var lowest = -1;
                var highest = -1;
                var lowestLift = float.MaxValue;
                var highestLift = float.MinValue;
                for (var i = 0; i < _cellMarkerCount[cell]; i++)
                {
                    var m = _markersByCell[from + i];
                    var lift = SubductionBuoyancyOf(m);
                    if (lift < lowestLift) { lowestLift = lift; lowest = i; }
                    if (lift > highestLift) { highestLift = lift; highest = i; }
                }
                if (lowest < 0 || highest < 0 || lowest == highest) break;

                // 浮力差不夠大就不是隱沒，是對撞增厚——留著讓它變厚，
                // 除非已經厚到超過地殼的物理上限。
                if (highestLift - lowestLift < SubductionBuoyancyContrastMeters
                    && CellThicknessFromMarkers(cell) <= Isostasy.MaxCrustThicknessKm)
                {
                    break;
                }

                var loser = _markersByCell[from + lowest];
                var winner = _markersByCell[from + highest];

                if (_markerCrust[loser] == CrustType.Continental
                    && _markerCrust[winner] == CrustType.Continental)
                {
                    // **兩塊大陸對撞：物質無處可去，只能往上下增厚（山脈＋山根）。**
                    //
                    // 這一段在改寫 Rasterize 時整個漏掉了（2026-09-06/07）：原本寫在
                    // ResolveCollision 裡，把擁擠處理內聯之後那個方法就沒人呼叫了，
                    // 大陸對撞從「吃掉對方一半的厚度」退化成只有增生的3%——**少了16倍**。
                    // 症狀是大陸長不大也長不厚：2億年後只有168格、碎成116塊、最大10格。
                    // 喜馬拉雅型造山是大陸變厚最主要的機制，缺了它大陸就只能靠島弧慢慢增生。
                    _markerThickness[winner] = MathF.Min(
                        _markerThickness[winner] + _markerThickness[loser] * ContinentalCollisionTransfer,
                        Isostasy.MaxCrustThicknessKm);

                }
                else
                {
                    // 一邊是海洋地殼：隱沒下去，但一部分會被刮下來增生到上盤
                    LastAccretionCrustKm += _markerThickness[loser] * AccretedFractionOfSubductedCrust;
                    LastSubductedCrustKm += _markerThickness[loser];
                    Accrete(winner, _markerThickness[loser]);
                }

                _markerAlive[loser] = false;

                // 從桶裡移除：把最後一個搬過來補洞
                _markersByCell[from + lowest] = _markersByCell[from + _cellMarkerCount[cell] - 1];
                _cellMarkerCount[cell]--;
            }
        }

        // 4. 完全沒有標記的格子。標記多於格子之後這會少很多，但仍要分辨兩種情況：
        //    真張裂 → 中洋脊新生地殼；其餘 → 取樣錯配，跟鄰居借。
        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            var existing = _cellMarkerCount[cell];

            // **張裂邊界要「補到滿」，不是只補完全空的格子**（2026-09-06，改成多標記時踩到）：
            // 中洋脊原本只在 count==0 時才生成新地殼，而一格多個標記之後空格幾乎消失
            // （錯配空格從86/步降到3.9/步），等於把造地殼的機制關掉了，隱沒卻照常運作——
            // 標記數從23,048一路掉到12,900、平均地殼厚只剩8.1公里、陸地1.8%。
            // 板塊真的在分開的地方，標記密度本來就會低於目標值，補到滿才是正確的海底擴張。
            if (state.Boundary[cell] == BoundaryType.Divergent && existing < MarkersPerCell)
            {
                for (var k = existing; k < MarkersPerCell; k++)
                {
                    var fresh = AllocateMarker();
                    if (fresh < 0) break;
                    SeedRidgeMarker(grid, state, plates, fresh, cell);
                    AppendToCell(cell, fresh);
                }
                continue;
            }

            if (existing > 0) continue;

            // 先看附近有沒有因為擁擠而被擠出來的標記，有就搬過來（守恆）
            if (TryTakeSpare(grid, cell, out var moved))
            {
                LastPairedFillCount++;
                _markerX[moved] = grid.UnitVector[cell * 3];
                _markerY[moved] = grid.UnitVector[cell * 3 + 1];
                _markerZ[moved] = grid.UnitVector[cell * 3 + 2];
                _markerCell[moved] = cell;
                _markerTarget[moved] = cell;
                _markerPlate[moved] = plates.PlateIdByCell[cell];
                AppendToCell(cell, moved);
                continue;
            }

            var marker = AllocateMarker();
            if (marker < 0) continue; // 容量用盡（正常不會發生），這格沿用上一輪的內容

            _markerX[marker] = grid.UnitVector[cell * 3];
            _markerY[marker] = grid.UnitVector[cell * 3 + 1];
            _markerZ[marker] = grid.UnitVector[cell * 3 + 2];
            _markerCell[marker] = cell;
            _markerTarget[marker] = cell;
            _markerPlate[marker] = plates.PlateIdByCell[cell];

            if (state.Boundary[cell] == BoundaryType.Divergent)
            {
                // 走不到這裡：上面的「補到滿」已經處理過張裂邊界。保留是為了萬一。
                // 中洋脊**製造**的地殼就是這7公里。
                // 不可以寫成「7 − 這格原本的厚度」：空格裡那個舊值是已經隨標記漂走的地殼，
                // 不是被摧毀的東西。那樣量出來的中洋脊會是負的（實測 −6.02 km³/年），
                // 讓人以為中洋脊在吃掉地殼，整份地殼收支也跟著對不起來（2026-09-06 自己踩的）。
                // 一個標記只代表這一格 1/MarkersPerCell 的地殼柱，所以換算成「格子當量」要除以 N。
        // 不除的話中洋脊的產量會被高估4倍（2026-09-06）。
        LastRidgeCrustKm += Isostasy.OceanicCrustThicknessKm / MarkersPerCell;
                _markerThickness[marker] = Isostasy.OceanicCrustThicknessKm;
                _markerAge[marker] = 0f;
                _markerOrganic[marker] = 0f; // 剛從地函冒出來的玄武岩上面不會有礁
                // **新生地殼要繼承這個板塊的密度異常**，否則重板塊會被自己擴張出來的普通地殼稀釋掉：
                // 重地殼浮力低、每次碰撞都輸、優先被隱沒，補進來的又是普通密度，
                // 實測2億年內重地殼從428格衰減到只剩10格，「重板塊」實質上消失了（2026-09-06）。
                _markerDensity[marker] =
                    Isostasy.OceanicCrustDensity + plates.Plates[_markerPlate[marker]].DensityAnomalyKgPerM3;
                _markerCrust[marker] = CrustType.Oceanic;
                _markerOceanicOnly[marker] =
                    plates.Plates[_markerPlate[marker]].IsOceanicOnly;

                // 中洋脊火山：速率的分母是「上次取樣到現在」而不是單一步長，
                // 否則會得到比島弧大兩個數量級的尖峰，畫出來只剩中洋脊在一閃一閃。
                state.VolcanicActivity[cell] +=
                    Isostasy.OceanicCrustThicknessKm / MathF.Max((float)_myrSinceLastRaster, 1e-6f);
            }
            else
            {
                LastMisfitFillCount++;
                BorrowFromNeighbours(grid, marker, cell);
            }

            AppendToCell(cell, marker);
        }

        // 4b. 還沒被配對用掉的暫存標記才真的丟棄。
        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            if (_spareAtCell[cell] < 0) continue;
            _markerAlive[_spareAtCell[cell]] = false;
            _spareAtCell[cell] = -1;
        }

        // 5. 把每一格的標記平均起來寫進格子。
        //    平均而不是取其中一個：一格有好幾個標記，取單一個等於丟掉其他標記帶的資訊。
        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            var count = _cellMarkerCount[cell];
            if (count <= 0) continue;

            // **取浮得最高的那個標記，不是取平均**（2026-09-06 實測後改）：
            // 平均是擴散性的——一格裡1個40公里的大陸標記配3個7公里的海洋標記，
            // 平均只有15公里，大陸就被抹掉了。實測用平均時大陸地殼厚度只有17~23公里
            // （低於25公里的大陸門檻），陸地永遠停在1.5~2.8%。
            //
            // 取最高的則跟這個模型原本的物理一致：**誰浮得高誰留在地表**
            // （見 ResolveCollision）。格子顯示的是「表面看得到的那一塊」，
            // 其餘標記仍然存在、仍然跟著板塊移動，等上面那塊離開就換它們露出來。
            var top = -1;
            var topLift = float.MinValue;
            var first = -1;

            for (var i = 0; i < count; i++)
            {
                var m = MarkerAt(cell, i);
                if (first < 0) first = m;
                var lift = SurfaceLiftOf(m);
                if (lift > topLift) { topLift = lift; top = m; }

                // 地殼歸屬於「現在覆蓋在它上面」的那個板塊——板塊邊界會掃過地殼，
                // 被掃到的那一塊就換了主人，這正是隱沒帶把地殼從一塊板塊轉交給另一塊的過程
                _markerPlate[m] = plates.PlateIdByCell[cell];
            }

            if (top < 0) continue;

            // **厚度 = 落在這一格的標記厚度總和 ÷ MarkersPerCell。**
            //
            // 每個標記代表這一格 1/MarkersPerCell 的地殼柱，所以擠進來的標記多就變厚
            // （聚合造山）、少就變薄（張裂）——增厚與變薄是**搬運本身的結果**，
            // 不需要另外寫規則，而且這是唯一質量一致的取法。
            //
            // 曾經改成「取浮得最高的那一塊」，那等於每個標記各自代表一整根地殼柱，
            // 一格4個標記在標記族群裡就是4倍的質量、格子卻只顯示1根——實測中洋脊與隱沒的
            // 通量雙雙變成單標記版的3~4倍，地殼被高速回收、什麼都累積不起來（2026-09-06）。
            var totalThickness = 0f;
            for (var i = 0; i < count; i++) totalThickness += _markerThickness[MarkerAt(cell, i)];
            state.CrustThickness[cell] = totalThickness / MarkersPerCell;

            // 年齡、密度、型別取浮得最高的那一塊——那是「表面看得到的」，
            // 跟厚度這種體積量不同，它們是表層性質。
            state.CrustAge[cell] = _markerAge[top];
            state.CrustDensity[cell] = _markerDensity[top];

            // **礁灰岩也取最高的那一塊，不可以像厚度那樣加總**（2026-09-07 踩過）：
            // 取樣用總和、而回寫是把格子的值**絕對地**寫給格子裡的**每一個**標記
            // （見 WriteGridBackToMarkers）。這個配對對厚度是刻意的——它有
            // MaxCrustThicknessKm 上限，而且擁擠處理會殺掉多餘的標記，所以收斂。
            // 礁體沒有上限，一格暫時擠進2個標記就翻倍，下一輪再翻，是**指數放大**：
            // 實測最大厚度從8公里衝到211公里。
            //
            // 物理上取最高的也才對：兩塊各帶著礁體的地殼撞在一起，礁體不會相加，
            // 沉下去的那一塊連同它的礁體一起被隱沒掉。
            state.OrganicThickness[cell] = _markerOrganic[top];
            state.Crust[cell] = _markerCrust[top];
            state.OceanicOnly[cell] = _markerOceanicOnly[top];

            // **不能變成大陸的地殼，也不能厚成大陸**（2026-09-07 踩過）。
            //
            // OceanicOnly 的本意是「這塊地殼太重，不會變成大陸」（見 SimulationState.OceanicOnly），
            // 但原本只在 TryMakeContinental 那道門擋下轉換、沒有擋住**變厚**本身。
            // 於是重板塊照樣被聚合處的標記堆疊加厚，過了25公里的大陸門檻卻不會被改類別，
            // 就一路長到42公里還掛在「海洋地殼」，變成站得比一般海洋還高的高原——
            // 實測重板塊平均水深589公尺、一般海洋908公尺，**密度最高的板塊反而浮得最高**，
            // 跟這整個設計的前提正好相反。
            //
            // 露餡的是厚度分布：一般海洋在25公里以上**一格都沒有**（過門檻就轉成大陸、
            // 離開這個統計群），重板塊卻有48%的格子在28公里以上。
            // 平均厚度25公里這個數字本身看不出異常，要跟一般海洋擺在一起比才看得出來。
            //
            // 超過門檻的物質在物理上是被隱沒／拆離回收掉的，不會長成山根。
            if (state.OceanicOnly[cell])
            {
                state.CrustThickness[cell] =
                    MathF.Min(state.CrustThickness[cell], Isostasy.ContinentalThresholdKm);
            }

            _sampledThickness[cell] = state.CrustThickness[cell];
            _sampledAge[cell] = state.CrustAge[cell];
            _sampledDensity[cell] = state.CrustDensity[cell];
            _ownerMarker[cell] = first;
        }

        CompactMarkers();
    }

    /// <summary>在中洋脊生成一個新的海洋地殼標記。</summary>
    private void SeedRidgeMarker(
        SimulationGrid grid, SimulationState state, PlateSet plates, int marker, int cell)
    {
        _markerX[marker] = grid.UnitVector[cell * 3];
        _markerY[marker] = grid.UnitVector[cell * 3 + 1];
        _markerZ[marker] = grid.UnitVector[cell * 3 + 2];
        _markerCell[marker] = cell;
        _markerTarget[marker] = cell;
        _markerPlate[marker] = plates.PlateIdByCell[cell];

        LastRidgeCrustKm += Isostasy.OceanicCrustThicknessKm;
        _markerThickness[marker] = Isostasy.OceanicCrustThicknessKm;
        _markerAge[marker] = 0f;
        _markerOrganic[marker] = 0f; // 剛從地函冒出來的玄武岩上面不會有礁
        // 新生地殼要繼承這個板塊的密度異常，否則重板塊會被自己擴張出來的普通地殼稀釋掉
        _markerDensity[marker] =
            Isostasy.OceanicCrustDensity + plates.Plates[_markerPlate[marker]].DensityAnomalyKgPerM3;
        _markerCrust[marker] = CrustType.Oceanic;
        _markerOceanicOnly[marker] = plates.Plates[_markerPlate[marker]].IsOceanicOnly;

        // 中洋脊火山：速率的分母是「上次取樣到現在」而不是單一步長，
        // 否則會得到比島弧大兩個數量級的尖峰，畫出來只剩中洋脊在一閃一閃。
        state.VolcanicActivity[cell] +=
            Isostasy.OceanicCrustThicknessKm / MathF.Max((float)_myrSinceLastRaster, 1e-6f);
    }

    /// <summary>
    /// 把新標記掛到某一格的清單上。
    ///
    /// 前綴和已經替每一格保留了至少 <see cref="MarkersPerCell"/> 個位置，所以直接接在桶尾即可。
    /// </summary>
    private void AppendToCell(int cell, int marker)
    {
        _markersByCell[_cellMarkerStart[cell] + _cellMarkerCount[cell]] = marker;
        _cellMarkerCount[cell]++;
    }

    /// <summary>
    /// 找附近（自己與鄰居）有沒有因為擁擠而被擠出來的暫存標記，有就取出來。
    /// 刻意只找一圈鄰居：搬得太遠等於把地殼瞬移，那比憑空生成更不合理。
    /// </summary>
    private bool TryTakeSpare(SimulationGrid grid, int cell, out int marker)
    {
        if (_spareAtCell[cell] >= 0)
        {
            marker = _spareAtCell[cell];
            _spareAtCell[cell] = -1;
            return true;
        }

        var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
        for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
        {
            var neighbor = grid.Neighbors[baseIndex + s];
            if (neighbor == SimulationGrid.NoNeighbor || _spareAtCell[neighbor] < 0) continue;
            marker = _spareAtCell[neighbor];
            _spareAtCell[neighbor] = -1;
            return true;
        }

        marker = -1;
        return false;
    }

    /// <summary>診斷用：有幾個錯配空格是靠「搬被擠出來的標記」補上的（守恆路徑）。</summary>
    public int LastPairedFillCount { get; private set; }

    /// <summary>由落在這一格的標記算出地殼厚度（總和÷目標標記數，見 Rasterize 第5段）。</summary>
    private float CellThicknessFromMarkers(int cell)
    {
        var total = 0f;
        var count = _cellMarkerCount[cell];
        for (var i = 0; i < count; i++) total += _markerThickness[MarkerAt(cell, i)];
        return total / MarkersPerCell;
    }

    /// <summary>取出某一格的第 <paramref name="index"/> 個標記。</summary>
    private int MarkerAt(int cell, int index) => _markersByCell[_cellMarkerStart[cell] + index];

    /// <summary>
    /// 地質作用（增生、造山、重力擴展、火山、侵蝕）改的是格子上的值，要寫回標記，
    /// 否則下一次取樣就會用舊的標記內容把這些改動整個蓋掉。
    ///
    /// **一格一個標記時要寫「絕對值」，不能寫差值**（2026-09-07 追出來的）。
    ///
    /// 差值寫回（FLIP）是為了保留「同一格裡多個標記之間的差異」才加的，
    /// 在 <see cref="MarkersPerCell"/> = 1 時那個差異根本不存在，而副作用很嚴重：
    /// 負的差值會把標記一路壓到 <see cref="Isostasy.MinCrustThicknessKm"/> 的下限再也回不來。
    /// 實測改回絕對值之後，**大陸從168格變成461格、最大陸塊從10格變成45格、
    /// 大陸厚度從31.4變成38.6公里**（地球是35~40）。
    ///
    /// 之後若要再走多標記，這裡要跟著改回差值——兩者是綁在一起的。
    /// </summary>
    public void WriteGridBackToMarkers(SimulationState state)
    {
        Parallel.For(0, state.CellCount, cell =>
        {
            var count = _cellMarkerCount[cell];
            if (count <= 0) return;

            var deltaThickness = state.CrustThickness[cell] - _sampledThickness[cell];
            var deltaAge = state.CrustAge[cell] - _sampledAge[cell];
            var deltaDensity = state.CrustDensity[cell] - _sampledDensity[cell];

            for (var i = 0; i < count; i++)
            {
                var marker = MarkerAt(cell, i);
                _markerThickness[marker] = state.CrustThickness[cell];
                _markerAge[marker] = state.CrustAge[cell];
                _markerDensity[marker] = state.CrustDensity[cell];
                _markerOrganic[marker] = state.OrganicThickness[cell];
                // 型別與旗標是類別值，沒有差值可言，直接跟著格子走
                _markerCrust[marker] = state.Crust[cell];
                _markerOceanicOnly[marker] = state.OceanicOnly[cell];
            }

            _sampledThickness[cell] = state.CrustThickness[cell];
            _sampledAge[cell] = state.CrustAge[cell];
            _sampledDensity[cell] = state.CrustDensity[cell];
        });
    }


    /// <summary>
    /// 取樣錯配的空格：從鄰居的標記借地殼，借多少就扣多少，總量守恆。
    ///
    /// 借不到（鄰居也都沒有標記，正常不會發生）就給最薄的海洋地殼，
    /// 那是唯一不會憑空製造大量地殼的退路。
    /// </summary>
    private void BorrowFromNeighbours(SimulationGrid grid, int marker, int cell)
    {
        var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
        var donors = 0;
        var thickness = 0f;
        var age = 0f;
        var density = 0f;
        var organic = 0f;

        for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
        {
            var neighbor = grid.Neighbors[baseIndex + s];
            if (neighbor == SimulationGrid.NoNeighbor) continue;
            // **要用分桶找標記，不能用 _ownerMarker**（2026-09-07 踩過）：
            // _ownerMarker 在 Rasterize 開頭就被清成 -1，要到第5段（寫進格子）才填回去，
            // 而這個方法是在第4段（補空格）呼叫的——那時它全部是 -1，
            // 於是 donors 永遠是 0、每一次都掉進下面「借不到就給最薄地殼」的退路。
            // 實測每步觸發14次、跟「錯配空格裡沒被配對走的數量」完全吻合（50×28%），
            // 4,000步下來把海洋地殼打成3公里的次數平均每格10次——
            // 中洋脊明明生出7公里，海洋地殼的中位數卻只有3.8。
            if (_cellMarkerCount[neighbor] <= 0) continue;
            var donor = MarkerAt(neighbor, 0);

            donors++;
            thickness += _markerThickness[donor];
            age += _markerAge[donor];
            density += _markerDensity[donor];
            organic += _markerOrganic[donor];
        }

        if (donors == 0)
        {
            LastNoDonorCount++;
            _markerThickness[marker] = Isostasy.MinCrustThicknessKm;
            _markerAge[marker] = 0f;
            _markerDensity[marker] = Isostasy.OceanicCrustDensity;
            _markerOrganic[marker] = 0f;
            _markerCrust[marker] = CrustType.Oceanic;
            _markerOceanicOnly[marker] = false;
            return;
        }

        var borrowed = thickness / donors;
        _markerThickness[marker] = borrowed;
        _markerAge[marker] = age / donors;
        _markerDensity[marker] = density / donors;
        _markerOrganic[marker] = organic / donors;

        var perDonor = borrowed / donors * MisfitDeductionFraction;
        var continental = 0;
        var oceanicOnly = 0;
        for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
        {
            var neighbor = grid.Neighbors[baseIndex + s];
            if (neighbor == SimulationGrid.NoNeighbor) continue;
            if (_cellMarkerCount[neighbor] <= 0) continue;
            var donor = MarkerAt(neighbor, 0);

            _markerThickness[donor] = MathF.Max(
                Isostasy.MinCrustThicknessKm, _markerThickness[donor] - perDonor);
            if (_markerCrust[donor] == CrustType.Continental) continental++;
            if (_markerOceanicOnly[donor]) oceanicOnly++;
        }

        // 型別與旗標跟多數鄰居一致
        _markerCrust[marker] = continental * 2 > donors ? CrustType.Continental : CrustType.Oceanic;
        _markerOceanicOnly[marker] = oceanicOnly * 2 > donors;
    }

    /// <summary>
    /// 找一個空的標記槽並標成存活。滿了回傳 -1。
    ///
    /// **一定要在這裡就設 <c>_markerAlive</c>**（2026-09-06 踩過）：
    /// 漏掉的話新生的標記一出生就是死的，<see cref="CompactMarkers"/> 立刻把它擠掉，
    /// 而 <c>_remap</c> 對死標記從來沒賦值，於是 <c>_ownerMarker</c> 會殘留上一輪的舊索引、
    /// 多個格子共用同一個標記——<see cref="WriteGridBackToMarkers"/> 是平行的，
    /// 那就變成真正的資料競爭。症狀是同種子跑出不同的星球，而且因為執行緒分割在同一台機器上
    /// 通常是固定的，**失敗位置每次都一樣、看起來完全不像競爭**。
    /// </summary>
    private int AllocateMarker()
    {
        if (_markerCount < _markerX.Length)
        {
            _markerAlive[_markerCount] = true;
            return _markerCount++;
        }

        for (var i = 0; i < _markerCount; i++)
        {
            if (_markerAlive[i]) continue;
            _markerAlive[i] = true;
            return i;
        }
        return -1;
    }

    /// <summary>
    /// 把死掉（隱沒掉）的標記從陣列中擠掉，讓標記總數不會隨時間單向增長。
    /// <c>_ownerMarker</c> 會跟著更新，因為它存的是索引。
    /// </summary>
    private void CompactMarkers()
    {
        var write = 0;
        for (var read = 0; read < _markerCount; read++)
        {
            // 死掉的標記要明確標成 -1，不能留著上一輪的殘值——那會讓 _ownerMarker 指到別人身上
            if (!_markerAlive[read])
            {
                _remap[read] = -1;
                continue;
            }
            if (write != read)
            {
                _markerX[write] = _markerX[read];
                _markerY[write] = _markerY[read];
                _markerZ[write] = _markerZ[read];
                _markerThickness[write] = _markerThickness[read];
                _markerAge[write] = _markerAge[read];
                _markerDensity[write] = _markerDensity[read];
                _markerOrganic[write] = _markerOrganic[read];
                _markerCrust[write] = _markerCrust[read];
                _markerOceanicOnly[write] = _markerOceanicOnly[read];
                _markerCell[write] = _markerCell[read];
                _markerPlate[write] = _markerPlate[read];
                _markerAlive[write] = true;
            }
            _remap[read] = write;
            write++;
        }

        for (var cell = 0; cell < _ownerMarker.Length; cell++)
        {
            var marker = _ownerMarker[cell];
            if (marker >= 0) _ownerMarker[cell] = _remap[marker];
        }

        // **分桶陣列也存的是索引，一定要一起重新對應**（2026-09-06 踩過）：
        // 漏掉的話，取樣之後每一格的標記清單都指到別人身上，
        // 地質作用的回寫就會寫到錯誤的標記。症狀是「同種子跑出不同的星球」——
        // 那個測試就是為了抓這類問題放的，而且它比任何統計量都早發現。
        for (var cell = 0; cell < _ownerMarker.Length; cell++)
        {
            var start = _cellMarkerStart[cell];
            var count = _cellMarkerCount[cell];
            var kept = 0;
            for (var i = 0; i < count; i++)
            {
                var remapped = _remap[_markersByCell[start + i]];
                if (remapped < 0) continue; // 這一輪被隱沒掉了
                _markersByCell[start + kept++] = remapped;
            }
            _cellMarkerCount[cell] = kept;
        }

        Array.Fill(_markerAlive, false, write, _markerCount - write);
        _markerCount = write;
    }

    /// <summary>
    /// 兩塊地殼被擠到同一格時，誰留在地表。這就是隱沒的判準，物理依據是**浮力**：
    ///
    /// - 大陸 vs 海洋：大陸留下（大陸地殼太輕，沉不下去，這也是大陸能保存數十億年的原因）
    /// - 大陸 vs 大陸：誰都沉不下去 → 對撞增厚，這就是喜馬拉雅型造山
    /// - 海洋 vs 海洋：**厚的留下**；一樣厚才比年齡（老的比較冷、密度大，會先沉）
    ///
    /// **「厚的留下」這條不能省，島弧會不會保存下來全看它**（2026-09-05 踩過）：
    /// 原本這裡只比年齡、而且贏家會把自己的厚度直接蓋到目標格上，於是聚合邊界上
    /// <see cref="ApplyConvergence"/> 好不容易增厚的島弧，只要碰到一塊比較年輕的普通海洋地殼
    /// 就被打回7公里。增厚只發生在聚合邊界，那裡正好也是碰撞最密集的地方，
    /// 結果是一邊長一邊被砍：跑5億年最大厚度只在10~14公里之間震盪、平均值完全不動、
    /// 一塊陸地都長不出來，而且沒有任何錯誤訊息。
    ///
    /// 用厚度當判準不是為了讓測試過，是因為浮力本來就取決於厚度而不是年齡——
    /// 「大陸不會沉」其實就是這條規則的特例，原本只是被寫死成型別判斷、沒有推廣到厚度。
    /// </summary>
    /// <param name="incoming">後落到這一格的標記。</param>
    /// <param name="occupant">已經佔住這一格的標記。</param>
    /// <param name="target">兩者擠在一起的格子。</param>
    /// <param name="isRealConvergence">
    /// 這一格是不是真的在聚合邊界上。**不是的話只是取樣的錯配**，
    /// 兩個標記剛好落進同一格而已，不能當成隱沒去增厚上盤，否則板塊內部到處都會憑空造山。
    /// </param>
    private void ResolveCollision(int incoming, int occupant, int target, bool isRealConvergence)
    {
        var incomingCrust = _markerCrust[incoming];
        var occupantCrust = _markerCrust[occupant];

        if (!isRealConvergence)
        {
            // 取樣錯配：留下浮得高的那個，另一個**先留著**放進多餘暫存區，等一下配給附近的空格。
            //
            // **不可以直接丟掉**（2026-09-06 踩過）：錯配的碰撞與錯配的空格其實是同一件事的兩面
            // ——球面旋轉映射到離散網格不是完美的排列，所以有些格子拿到兩個標記、有些拿到零個。
            // 「多的丟掉、少的憑空生」等於同時開了一個沒被計數的沉與一個沒被計數的源，
            // 而那個源實測注入 50~351 km³/年，是整顆星球失控變成厚地殼的引擎。
            // 把兩者配對就天生守恆：同一塊地殼，只是重新指派到正確的格子。
            var keepIncoming = SubductionBuoyancyOf(incoming) > SubductionBuoyancyOf(occupant);
            var spare = keepIncoming ? occupant : incoming;
            if (keepIncoming) _ownerMarker[target] = incoming;

            if (_spareAtCell[target] < 0) _spareAtCell[target] = spare;
            else _markerAlive[spare] = false; // 同一格擠了三個以上，罕見
            return;
        }

        if (incomingCrust == CrustType.Continental && occupantCrust == CrustType.Continental)
        {
            // 兩塊大陸對撞：誰都沉不下去，物質無處可去，只能往上下增厚（山脈＋山根）
            _markerThickness[occupant] = MathF.Min(
                _markerThickness[occupant] + _markerThickness[incoming] * 0.5f,
                Isostasy.MaxCrustThicknessKm);
            _markerAlive[incoming] = false;
            return;
        }

        // 誰留在地表就是誰浮得高——直接比均衡高度，厚度、密度、年齡三個因素會自動一起考慮進去，
        // 不需要再寫一串優先順序的規則（「大陸不會沉」本來就是這條的必然結果，因為大陸又厚又輕）。
        var incomingLift = SubductionBuoyancyOf(incoming);
        var occupantLift = SubductionBuoyancyOf(occupant);
        var incomingWins = MathF.Abs(incomingLift - occupantLift) > BuoyancyToleranceMeters
            ? incomingLift > occupantLift
            : _markerAge[incoming] < _markerAge[occupant]; // 浮力相當才比年齡（老的比較冷、密度大）

        // 輸的那塊隱沒下去，但不是整塊消失——一部分會被刮下來堆到上盤（增生楔／增生地體）
        var (winner, loser) = incomingWins ? (incoming, occupant) : (occupant, incoming);

        LastAccretionCrustKm += _markerThickness[loser] * AccretedFractionOfSubductedCrust;
        LastSubductedCrustKm += _markerThickness[loser];
        Accrete(winner, _markerThickness[loser]);
        _markerAlive[loser] = false;
        _ownerMarker[target] = winner;
    }

    /// <summary>這個標記的地殼浮得多高（公尺）。就是均衡高度，用來決定碰撞時誰隱沒。</summary>
    /// <summary>
    /// 這個標記的地表高度。用來決定**誰露在表面**（同一格擠了好幾個標記時看得到哪一個）。
    ///
    /// **不要拿它來判斷誰會隱沒**——那是另一件事，用 <see cref="SubductionBuoyancyOf"/>。
    /// 混用是 2026-09-07 修掉的一個概念錯誤：地表高度是整根柱子的均衡結果，
    /// 能不能隱沒則是整個岩石圈相對於軟流圈的淨密度，兩個不同的積分。
    /// </summary>
    private float SurfaceLiftOf(int marker) => Isostasy.BedrockElevation(
        _markerCrust[marker], _markerThickness[marker], _markerAge[marker], _markerDensity[marker]);

    /// <summary>
    /// 這個標記的岩石圈淨浮力（正值浮、負值沉）。**隱沒與碰撞的勝負一律用這個判**。
    ///
    /// 它跟地表高度最關鍵的差別是：海洋岩石圈有一個**符號翻轉的年齡**（約18百萬年）——
    /// 年輕的浮、老的沉。地表高度只是單調變深，表達不出「年輕洋殼抗拒隱沒」這件事。
    /// 見 <see cref="Isostasy.LithosphereBuoyancyMeters"/>。
    /// </summary>
    private float SubductionBuoyancyOf(int marker) => Isostasy.LithosphereBuoyancyMeters(
        _markerCrust[marker], _markerThickness[marker], _markerDensity[marker], _markerAge[marker]);

    /// <summary>
    /// 把隱沒掉的地殼的一部分堆到留在地表的那一格上。
    ///
    /// **這是大陸能「集中」而不是攤平成一層的關鍵**（2026-09-05 補上）：
    /// 在加這段之前，隱沒的地殼是整塊憑空消失的，於是增厚只能靠
    /// <see cref="ApplyConvergence"/> 均勻地灑在全部約1,400個聚合格上，
    /// 絕大多數永遠到不了25公里的大陸門檻，只是把海底從7公里墊到8.6公里。
    /// 質量平衡其實一直是對的（10億年產出0.059 km³/年，與地球按面積縮放後的0.152同一量級），
    /// 錯的是**分布**——10億年後陸地只佔0.14%，而同樣的地殼量若集中成35公里厚可以蓋掉5.7%。
    ///
    /// 增生是**質量轉移**而不是憑空產生（上盤拿到的正好是下盤失去的一部分），
    /// 所以天生就會集中在同一個邊界上反覆累積，這正是地球上大陸如何從島弧長起來的。
    /// </summary>
    /// <param name="winner">留在地表的標記。</param>
    /// <param name="subductedThicknessKm">被隱沒掉的那塊地殼有多厚（公里）。</param>
    private void Accrete(int winner, float subductedThicknessKm) =>
        _markerThickness[winner] = MathF.Min(
            _markerThickness[winner] + subductedThicknessKm * AccretedFractionOfSubductedCrust,
            Isostasy.MaxCrustThicknessKm);

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

            var myPlate = plates.PlateIdByCell[cell];
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
                if (plates.PlateIdByCell[neighbor] == myPlate) continue;

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

            _boundaryOtherPlate[cell] = plates.PlateIdByCell[strongestNeighbor];

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

            var plateA = plates.PlateIdByCell[seed];
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
                    var theirPlate = plates.PlateIdByCell[neighbor];
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
    /// 誰是上盤由密度決定（跟 <see cref="ResolveCollision"/> 同一套判準）：
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

    private void ApplyConvergence(SimulationGrid grid, SimulationState state, PlateSet plates, double deltaMyr)
    {
        // 先把地殼類型拍一張快照，平行迴圈裡讀鄰居一律讀快照。
        //
        // **這不只是為了正確，是為了不要有資料競爭**：迴圈會寫 state.Crust[cell]（島弧變成大陸地殼），
        // 同時又要讀 state.Crust[neighbor]，而那個鄰居可能正被別的執行緒寫。
        // 症狀非常難查——同一個種子在機器閒的時候結果一致、負載一高就分岔，
        // 所以「同種子演化相同」的測試單獨跑會過、跟其他測試一起跑才會失敗（2026-09-05 踩過）。
        // 用快照同時也比較物理：每一格的判斷都基於同一個時刻的世界，
        // 而不是取決於鄰居有沒有剛好先被算到。
        Array.Copy(state.Crust, _crustSnapshot, state.CellCount);
        Parallel.For(0, state.CellCount, cell => _liftSnapshot[cell] = Isostasy.BedrockElevation(
            state.Crust[cell], state.CrustThickness[cell], state.CrustAge[cell], state.CrustDensity[cell]));

        // 1. 每條線段這一步供應多少地殼。這裡只算預算，還不動地殼。
        Array.Clear(_orogenBudget, 0, state.CellCount);
        Parallel.For(0, state.CellCount, cell =>
        {
            if (state.Boundary[cell] != BoundaryType.Convergent) return;

            var neighbor = _convergingNeighbor[cell];
            if (neighbor < 0) return;

            var mine = _crustSnapshot[cell];
            var theirs = _crustSnapshot[neighbor];

            var isOverriding = (mine, theirs) switch
            {
                (CrustType.Continental, CrustType.Oceanic) => true,
                (CrustType.Oceanic, CrustType.Continental) => false,
                (CrustType.Continental, CrustType.Continental) => true, // 對撞，兩邊都增厚

                // **都是海洋：浮得高的當上盤，不是年輕的**（2026-09-07 改）。
                //
                // 年齡只是密度的代理變數（老的比較冷、比較重），但這個模型裡密度是**明寫的**
                // ——重板塊有 DensityAnomalyKgPerM3。用年齡判斷等於忽略那個異常。
                //
                // 改用均衡高度也讓兩個層級一致：標記層級的 ResolveCollision 早就是
                // 「誰浮得高誰留在地表」，只有這裡還在用另一套規則。
                //
                // **老實說：這個改動單獨量不出效果**（重板塊厚度 25.0 vs 25.0 公里）。
                // 留著是因為「忽略明寫的密度異常、改用年齡這個代理變數」本身就是錯的，
                // 不是因為它修好了什麼。
                _ => _liftSnapshot[cell] >= _liftSnapshot[neighbor],
            };

            if (!isOverriding) return;

            // **速率取整條線段的平均，不是這一格自己的**：逐格的值是從「自己最強的那個鄰居」
            // 算出來的，在格子尺度上很跳；一條隱沒帶整體被推擠的速率才是有物理意義的量。
            // 權重則來自線段的長度與離端點的距離（見 TraceConvergentSegments）。
            var convergenceKm = _segmentMeanConvergence[cell] / 1000.0 * deltaMyr;
            var addedKm = convergenceKm * ThickeningPerConvergence * _convergenceWeight[cell];
            if (addedKm <= 0) return;

            _orogenBudget[cell] = addedKm;

            // **島弧火山留在線上，不跟著攤開。** 變形帶是寬的，火山弧卻是窄的
            // （日本、安地斯的火山鏈只有幾十公里寬）——脫水熔融發生在板片到達特定深度的那條線上。
            state.VolcanicActivity[cell] += (float)(addedKm / Math.Max(deltaMyr, 1e-9));
        });

        // 2. 從線往**自己這一側的內陸**泛洪，畫出造山帶的範圍。
        //    只往同一個板塊擴散——「線後方」指的就是上盤那一側。
        var spacingKm = grid.AverageCellSpacingMeters / 1000.0;
        var maxHops = Math.Max(1, (int)(OrogenWidthKm / spacingKm));
        var bandCells = BuildOrogenBand(grid, state, plates, maxHops);
        LastOrogenCellCount = bandCells;

        // 3. 每條線段的預算，攤到它所屬的帶上。權重在線上最大、往內陸線性遞減到0。
        Array.Clear(_segmentBudget, 0, LastSegmentCount);
        Array.Clear(_segmentProfileSum, 0, LastSegmentCount);
        for (var cell = 0; cell < state.CellCount; cell++)
        {
            if (_orogenBudget[cell] > 0) _segmentBudget[_segmentId[cell]] += _orogenBudget[cell];

            var segment = _orogenSegment[cell];
            if (segment < 0) continue;
            _segmentProfileSum[segment] += OrogenProfile(_orogenHops[cell], maxHops);
        }

        Array.Clear(_arcAdded, 0, state.CellCount);
        Parallel.For(0, state.CellCount, cell =>
        {
            var segment = _orogenSegment[cell];
            if (segment < 0) return;

            var profileSum = _segmentProfileSum[segment];
            if (profileSum <= 0) return;

            var share = OrogenProfile(_orogenHops[cell], maxHops) / profileSum;
            var addedKm = _segmentBudget[segment] * share;
            if (addedKm <= 0) return;

            _arcAdded[cell] = addedKm;
            state.CrustThickness[cell] = MathF.Min(
                state.CrustThickness[cell] + (float)addedKm, Isostasy.MaxCrustThicknessKm);

            // 岩漿分異：島弧的岩漿是分異過的，加上去的物質比原本的海洋地殼輕，
            // 所以地殼一邊變厚一邊**變輕**。這是海洋地殼能真的轉成大陸地殼的化學面，
            // 只增厚而密度不變的話那只是一塊比較厚的海洋地殼。
            var addedFraction = (float)(addedKm / Math.Max(state.CrustThickness[cell], 1e-6f));
            state.CrustDensity[cell] +=
                (Isostasy.ContinentalCrustDensity - state.CrustDensity[cell]) * MathF.Min(addedFraction, 1f);

            // 增生到夠厚就變成大陸地殼——全海洋星球長出陸地的關鍵一步
            if (_crustSnapshot[cell] == CrustType.Oceanic
                && state.CrustThickness[cell] >= Isostasy.ContinentalThresholdKm)
            {
                state.TryMakeContinental(cell);
            }
        });
    }

    /// <summary>
    /// 造山帶內的分配曲線：線上最大、往內陸線性遞減到帶緣為0。
    /// 真實的變形強度也是從隱沒帶往內陸遞減的（安地斯的地殼厚度從海岸往東遞減）。
    /// </summary>
    private static double OrogenProfile(int hops, int maxHops) =>
        Math.Max(0.0, 1.0 - (double)hops / (maxHops + 1));

    /// <summary>
    /// 從聚合線往上盤內陸泛洪，畫出造山帶的範圍，並記下每一格離線幾格、屬於哪一條線段。
    ///
    /// 只往**同一個板塊**擴散——「線後方」指的就是上盤那一側；跨過線就是另一塊板塊，
    /// 那邊是隱沒下去的一方，不會被抬升。
    ///
    /// 走訪是逐層的廣度優先，種子依格子索引排序放入，所以結果與執行緒排程無關。
    /// </summary>
    /// <returns>落在造山帶內的格數。</returns>
    private int BuildOrogenBand(SimulationGrid grid, SimulationState state, PlateSet plates, int maxHops)
    {
        Array.Fill(_orogenSegment, -1, 0, grid.CellCount);
        Array.Fill(_orogenHops, 0, 0, grid.CellCount);

        var count = 0;
        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            if (_orogenBudget[cell] <= 0) continue;
            _orogenSegment[cell] = _segmentId[cell];
            _orogenHops[cell] = 0;
            _orogenQueue[count++] = cell;
        }

        for (var head = 0; head < count; head++)
        {
            var cell = _orogenQueue[head];
            var hops = _orogenHops[cell];
            if (hops >= maxHops) continue;

            var plate = plates.PlateIdByCell[cell];
            var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
            for (var n = 0; n < SimulationGrid.MaxNeighborsPerCell; n++)
            {
                var neighbor = grid.Neighbors[baseIndex + n];
                if (neighbor == SimulationGrid.NoNeighbor) continue;
                if (_orogenSegment[neighbor] >= 0) continue;
                if (plates.PlateIdByCell[neighbor] != plate) continue;

                // **只往大陸地殼裡擴散**（2026-09-07 量測後加的）。
                //
                // 縮短量攤進海洋地殼是沒有用的：以1公里/百萬年跑5,000萬年是50公里的縮短量，
                // 全部由15公里寬的最前排吸收，那一格縮短超過100%、厚度翻倍、越過大陸門檻；
                // 攤到400公里寬的帶上只縮短12.5%，7公里變成7.9公里，永遠不會變成大陸。
                // **均勻分攤的縮短造不出大陸，集中的縮短才行。**
                // 實測把帶開放給海洋地殼時，大陸格數 22,965 → 14,962（流速0.05）→ 12,413（0.40），
                // 三種流速全都是稀釋。
                //
                // 但抬升發生在**既有的大陸**上就完全正確：西藏是寬闊高原，是因為印度撞進來時
                // 後面本來就已經是大陸。而且大陸岩石圈遠比海洋岩石圈軟——
                // 這正是高原長在大陸上、而海洋上的島弧總是窄的原因。
                if (_crustSnapshot[neighbor] != CrustType.Continental) continue;

                _orogenSegment[neighbor] = _orogenSegment[cell];
                _orogenHops[neighbor] = hops + 1;
                _orogenQueue[count++] = neighbor;
            }
        }

        return count;
    }

    /// <summary>
    /// 重力擴展（造山帶重力崩解，orogenic collapse）：地殼厚到一定程度就撐不住自己的重量，
    /// 會像一坨太厚的糖漿一樣往側向流開。這是西藏之所以是一片**寬闊高原**而不是一根尖峰的原因，
    /// 也是為什麼地球的大陸地殼厚度普遍收斂在35~40公里、而不是有些地方薄有些地方無限厚。
    ///
    /// **這一段是「大陸會長大」而不只是「長高」的關鍵**（2026-09-05 補上）：
    /// 在加這段之前，質量平衡其實是對的——10億年下來平均厚度增加1.38公里，換算成0.059 km³/年，
    /// 跟地球的1.8 km³/年按本星球表面積（地球的8.45%）縮放後的0.152 km³/年同一個數量級。
    /// problem 不在產量，在**分布**：只有14個格子越過大陸門檻，然後它們每次聚合都繼續贏、
    /// 一路增厚到75公里的上限就飽和，把所有成長囤在自己身上，旁邊的格子一格都分不到。
    /// 結果是10億年後陸地只佔0.23%（地球是29%）。把超厚的部分擴散給鄰居，
    /// 才會把「厚度」換成「面積」。
    /// </summary>
    /// <summary>
    /// 造山帶內地殼側向流動的速率（每百萬年抹平厚度差的比例）。
    ///
    /// ## 為什麼只在造山帶內流
    ///
    /// 這是使用者提出的「推動的時候，線的後面一片區塊也要一起抬升」實際生效的那一步。
    /// 縮短量原本全部由最前排那一格吸收——因為板塊是**剛體**旋轉，整塊板塊每個標記都用
    /// 同一個歐拉旋轉在動，後面完全不變形。真實的板塊只有內部是剛性的，
    /// 邊界附近會內部變形，所以造山帶是寬帶：安地斯約500公里、西藏約1,000公里。
    ///
    /// **先前試過「用厚度差驅動流動」而且失敗過**（見 <see cref="GravitationalSpreadingThresholdKm"/>
    /// 的說明）：失敗原因是它作用在**每一個**地方，於是正常的大陸邊緣永遠在流失——
    /// 一塊35公里的大陸旁邊必然貼著7公里的海洋地殼，落差28公里超過任何合理門檻，
    /// 實測起始4.55%的陸地到6億年只剩0.35%。
    ///
    /// 這裡的差別是**把它限制在活躍的造山帶內**（<c>_orogenSegment</c> 標出來的那條帶）。
    /// 物理上正是這樣：活躍造山帶的地殼是熱的、軟的、會流（西藏的地殼流 channel flow，
    /// 也是西藏成為一片寬闊高原而不是一根尖峰的原因）；穩定的克拉通邊緣是冷的、硬的，不流。
    ///
    /// 也試過用重力擴展的門檻來加寬（把 40 降到 34、28），那條路**行不通**：
    /// 一格40公里的地殼分給6個7公里的海洋鄰居，鄰居只變成7.5公里、離25公里的大陸門檻還很遠，
    /// 而來源自己掉下去了——實測大陸格數 22,965 → 19,771 → 18,568，是稀釋不是加寬。
    /// 差別在於造山帶內的鄰居本來就厚，流過去才推得過門檻。
    /// </summary>
    private const double OrogenFlowRatePerMyr = 0.15;

    /// <summary>
    /// 造山帶內的地殼側向流動：把最前排吸收掉的縮短量攤到線後方整條帶上。
    ///
    /// 只在同一條造山帶內、而且只從厚流到薄，所以是守恆的重分配，不會憑空生出地殼。
    /// 帶外一律不動——那是這個作法跟先前失敗那版最關鍵的差別（見 <see cref="OrogenFlowRatePerMyr"/>）。
    /// </summary>
    private void FlowWithinOrogenBand(SimulationGrid grid, SimulationState state, double deltaMyr)
    {
        Array.Copy(state.CrustThickness, _spreadBuffer, grid.CellCount);

        // 上限0.1跟重力擴展同一個理由：一格最多6個鄰居，每個都流出這個比例的話總流出0.6倍，
        // 再大就會流過頭、在相鄰兩格之間來回震盪而不是擴散開。
        var factor = (float)Math.Min(OrogenFlowRatePerMyr * deltaMyr, 0.1);

        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            var segment = _orogenSegment[cell];
            if (segment < 0) continue;

            var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                var neighbor = grid.Neighbors[baseIndex + s];
                if (neighbor == SimulationGrid.NoNeighbor) continue;
                if (_orogenSegment[neighbor] != segment) continue;

                var difference = state.CrustThickness[cell] - state.CrustThickness[neighbor];
                if (difference <= 0f) continue;

                var flow = difference * factor;
                _spreadBuffer[cell] -= flow;
                _spreadBuffer[neighbor] += flow;
            }
        }

        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            if (_orogenSegment[cell] < 0) continue;
            state.CrustThickness[cell] = MathF.Min(_spreadBuffer[cell], Isostasy.MaxCrustThicknessKm);

            // 流過來的地殼一樣可以厚到變成大陸地殼——這就是「線後面一片區塊跟著抬升」
            if (state.Crust[cell] == CrustType.Oceanic
                && state.CrustThickness[cell] >= Isostasy.ContinentalThresholdKm)
            {
                state.TryMakeContinental(cell);
            }
        }
    }

    private void SpreadOverthickenedCrust(SimulationGrid grid, SimulationState state, double deltaMyr)
    {
        Array.Copy(state.CrustThickness, _spreadBuffer, grid.CellCount);

        // 上限0.1是數值穩定性的要求：一格最多有6個鄰居，每個都流出這個比例的話總流出量是0.6倍，
        // 再大就會流過頭、在相鄰兩格之間來回震盪而不是擴散開。
        var factor = (float)Math.Min(GravitationalSpreadingRatePerMyr * deltaMyr, 0.1);

        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            // 只有**超出自身均衡厚度**的部分會流，不是跟鄰居比。
            var excess = state.CrustThickness[cell] - GravitationalSpreadingThresholdKm;
            if (excess <= 0f) continue;

            var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                var neighbor = grid.Neighbors[baseIndex + s];
                if (neighbor == SimulationGrid.NoNeighbor) continue;
                if (state.CrustThickness[neighbor] >= state.CrustThickness[cell]) continue;

                var flow = excess * factor;
                _spreadBuffer[cell] -= flow;
                _spreadBuffer[neighbor] += flow;
            }
        }

        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            state.CrustThickness[cell] = MathF.Min(_spreadBuffer[cell], Isostasy.MaxCrustThicknessKm);

            // 被擴展過來的地殼一樣可以厚到變成大陸地殼——大陸就是這樣往外長大的
            if (state.Crust[cell] == CrustType.Oceanic
                && state.CrustThickness[cell] >= Isostasy.ContinentalThresholdKm)
            {
                state.TryMakeContinental(cell);
            }
        }
    }

    /// <summary>
    /// 相鄰格子的中心間距（公尺）。由平均面積當正六邊形反推邊長，再乘√3
    /// （正六邊形的中心間距＝邊長×√3）。取樣門檻要用這個而不是邊長，理由見
    /// <see cref="RasterizeThresholdInCellSpacings"/>。
    /// </summary>
    private static double EstimateCellSpacingMeters(SimulationGrid grid)
    {
        var edge = Math.Sqrt(grid.AverageCellAreaSquareMeters / (3.0 * Math.Sqrt(3.0) / 2.0));
        return edge * Math.Sqrt(3.0);
    }

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
        var dot = kx * vx + ky * vy + kz * vz;
        var factor = dot * (1 - cos);

        return (
            vx * cos + crossX * sin + kx * factor,
            vy * cos + crossY * sin + ky * factor,
            vz * cos + crossZ * sin + kz * factor);
    }
}
