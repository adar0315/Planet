using Planet.Simulation.Representation;

namespace Planet.Simulation;

/// <summary>地殼怎麼跟著板塊移動（見 <see cref="PlateTectonics"/> 的 <c>Transport</c>）。</summary>
public enum CrustTransport
{
    /// <summary>v1：標記法（marker-in-cell）。目前的預設，正式服務用這個。</summary>
    Markers,

    /// <summary>
    /// v2：板塊座標系（P2 選定，見 <c>PlateFrameCrust</c>）。板塊內部零雜訊，
    /// 但 P4 之前**沒有碰撞規則**——誰蓋過誰只看板塊歸屬，大陸也會被消耗掉。
    /// </summary>
    PlateFrame,
}

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
/// - 兩個標記落在同一格 → 聚合，依浮力決定誰隱沒（見 <see cref="Rasterize"/> 步驟3的聚合格迴圈）
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
/// 1. 碰撞的勝負要看**浮力（厚度）**而不只是年齡 —— <see cref="SubductionBuoyancyOf"/>，
///    用在 <see cref="Rasterize"/> 步驟3的聚合格迴圈
/// 2. 隱沒的地殼要有一部分**增生**到上盤，而不是整塊消失 —— <see cref="Accrete"/>
/// 3. 搬動不能有量化誤差，否則假的張裂／聚合會淹沒真的 —— 見上面的 marker-in-cell 那節
/// 4. 板塊配置要在**無淨旋轉參考系**下，否則整片地殼一起掃過網格製造雜訊
///    —— <c>PlateSet.RemoveNetRotation</c>
/// 5. 起始地殼要有**大尺度的不均勻**當作大陸的核心 —— <c>SimulationState.CreateInitialOceanWorld</c>
/// </summary>
public sealed partial class PlateTectonics
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
    internal const float BuoyancyToleranceMeters = 5f;

    /// <summary>
    /// 聚合處要有多大的均衡高度差（公尺）才算「隱沒」而不是「對撞增厚」。
    ///
    /// 取500：海洋地殼與大陸地殼的高度差是數千公尺，一定算隱沒；
    /// 兩塊條件相近的地殼擠在一起則是造山，不該讓其中一塊憑空消失。
    /// </summary>
    internal const float SubductionBuoyancyContrastMeters = 500f;

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
    internal const float AccretedFractionOfSubductedCrust = 0.03f;

    /// <summary>
    /// 兩塊大陸對撞時，被擠掉的那一塊有多少厚度轉移到留下的那一塊。
    /// 取0.5：大陸地殼太輕、誰都沉不下去，物質只能往上下擠成山脈與山根——
    /// 這是大陸變厚最主要的機制（喜馬拉雅型造山），跟隱沒的3%增生完全不同量級。
    /// </summary>
    internal const float ContinentalCollisionTransfer = 0.5f;

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


    /// <summary>模擬網格。</summary>
    private readonly SimulationGrid _grid;

    /// <summary>板塊座標系（<see cref="CrustTransport.PlateFrame"/> 時才有；標記法時是 null）。</summary>
    private readonly PlateFrameCrust? _frames;

    /// <summary>這一步長出新海洋地殼的格子（板塊座標系用，給中洋脊火山）。</summary>
    private readonly bool[]? _newCrustCells;

    /// <summary>地殼怎麼跟著板塊移動。</summary>
    public CrustTransport Transport { get; }

    /// <summary>診斷用（板塊座標系）：上一步長出新海洋地殼的格數。</summary>
    public int LastFrameNewCrustCells => _frames?.LastNewCrustCells ?? 0;


    /// <summary>診斷用（板塊座標系）：上一步被蓋過而消耗掉的點數。</summary>
    public int LastFrameConsumedPoints => _frames?.LastConsumedPoints ?? 0;

    /// <summary>診斷用：板塊座標系各種增厚來源的累計（見 PlateFrameCrust.UpliftSourcesKm），標記法是 null。</summary>
    internal double[]? FrameUpliftSourcesKm => _frames?.UpliftSourcesKm;

    /// <summary>診斷用（板塊座標系）：上一步被消耗掉的**大陸**地殼點數（P4 加碰撞規則之前大陸也會被吃掉）。</summary>
    public int LastFrameConsumedContinentalPoints => _frames?.LastConsumedContinentalPoints ?? 0;

    /// <summary>診斷用（板塊座標系）：上一步大陸碰上海洋、由海洋那一方隱沒的次數（大陸不隱沒）。</summary>
    public int LastFrameContinentOverrides => _frames?.LastContinentOverrides ?? 0;

    /// <summary>
    /// 診斷用（板塊座標系）：上一步新生地殼落在哪種邊界上（索引是 <see cref="BoundaryType"/>）。
    /// 正常的話幾乎都在張裂邊界上；落在轉形或非邊界上的是邊界幾何的副作用。
    /// </summary>
    public int[] LastFrameNewCrustByBoundary { get; } = new int[4];

    /// <summary>
    /// 大陸配置的劇本（見 <see cref="ContinentScenario"/>），沒有就是 null。只能配板塊座標系。
    /// 設定時順便告訴板塊座標系哪些板塊對不准自動焊接、哪個板塊不會被消耗。
    /// </summary>
    internal ContinentScenario? Scenario
    {
        get => _scenario;
        set
        {
            _scenario = value;
            if (_frames is null) return;
            _frames.WeldBlocked = value is null ? null : (a, b) => value.IsWeldBlocked(a, b, _elapsedMyr);
            _frames.Unconsumable = value is null ? null : (p, other) => value.IsUnconsumable(p, other, _elapsedMyr);
            _frames.AndeanSubductingPlate = value is null ? null : p => p == ContinentLayout.PacificPlate && _elapsedMyr < value.EndMyr;
        }
    }

    /// <summary><see cref="Scenario"/> 的儲存欄位。</summary>
    private ContinentScenario? _scenario;

    /// <summary>這一步開始時經過了多少百萬年（劇本與焊接封鎖用）。</summary>
    private double _elapsedMyr;

    /// <summary>某個板塊目前是否處於休眠（劇本指定在某時間點之前不動；標記法永遠為 false）。</summary>
    /// <param name="plate">板塊編號。</param>
    /// <returns>是否休眠。</returns>
    internal bool IsPlateDormant(int plate) => _frames?.IsDormant(plate) ?? false;

    /// <summary>
    /// 每格顯示的地殼點鍵（板塊座標系時；標記法是 null）。要「跟著地殼走」的東西用它，
    /// 例如裂縫火山的噴發口（見 <see cref="FissureVolcanism.Step"/>）。
    /// </summary>
    public int[]? CrustPointKeys
    {
        get
        {
            if (_frames is null) return null;
            _frames.WritePointKeys(_pointKeys!);
            return _pointKeys;
        }
    }

    /// <summary><see cref="CrustPointKeys"/> 的重複使用緩衝（板塊座標系時才配置）。</summary>
    private readonly int[]? _pointKeys;

    /// <summary>讀檔之後呼叫：板塊座標系要把每格實際的歸屬寫回 <see cref="PlateSet.OwnerByCell"/>（標記法什麼都不做）。</summary>
    internal void AfterRestore(PlateSet plates) => _frames?.PublishOwnership(plates);

    /// <summary>公尺／百萬年，每步重算</summary>
    private readonly double[] _convergenceRate;
    /// <summary>這格主要在跟哪一格聚合，-1 表示沒有。</summary>
    private readonly int[] _convergingNeighbor;
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

    /// <summary>地殼類型快照。<c>ApplyConvergence</c> 的平行迴圈會寫自己的地殼類型、又要讀鄰居的，所以讀鄰居一律讀這份。</summary>
    private readonly CrustType[] _crustSnapshot;

    /// <summary>
    /// 聚合判定用的基岩高度快照。跟 <c>_crustSnapshot</c> 同樣的理由——
    /// <c>ApplyConvergence</c> 的平行迴圈會寫自己的厚度、又要讀鄰居的厚度。
    /// </summary>
    private readonly float[] _liftSnapshot;
    /// <summary>重力擴展的 out-of-place 暫存。</summary>
    private readonly float[] _spreadBuffer;
    /// <summary>診斷用：每格島弧增厚了多少。</summary>
    private readonly double[] _arcAdded;

    // 標記（marker）：地殼的性質掛在這裡，位置是連續的，見類別說明。
    // 容量給格數的兩倍：聚合處標記會擠在一起、張裂處會新生，總數在格數上下浮動，留一倍餘裕。
    /// <summary>標記位置的 X 分量（單位球上）。</summary>
    private readonly double[] _markerX;
    /// <summary>標記位置的 Y 分量（單位球上）。</summary>
    private readonly double[] _markerY;
    /// <summary>標記位置的 Z 分量（單位球上）。</summary>
    private readonly double[] _markerZ;
    /// <summary>標記攜帶的地殼厚度（公里）。</summary>
    private readonly float[] _markerThickness;
    /// <summary>標記攜帶的地殼年齡（百萬年）。</summary>
    private readonly float[] _markerAge;
    /// <summary>標記攜帶的地殼密度（<see cref="SimulationState.CrustDensity"/>）。</summary>
    private readonly float[] _markerDensity;
    /// <summary>
    /// 礁灰岩的厚度（公尺）。它是地殼柱的一部分，所以**必須跟地殼一起漂**，
    /// 不能像以前那樣掛在格子上——見 CoralReefs 類別說明裡的「化石紀錄」那一段。
    /// </summary>
    private readonly float[] _markerOrganic;
    /// <summary>標記攜帶的地殼類型。</summary>
    private readonly CrustType[] _markerCrust;
    /// <summary>永遠不能變成大陸地殼，見 SimulationState.OceanicOnly。</summary>
    private readonly bool[] _markerOceanicOnly;
    /// <summary>標記所屬的板塊。</summary>
    private readonly int[] _markerPlate;
    /// <summary>標記是否還存在（隱沒或合併後設為 false，壓實時移除）。</summary>
    private readonly bool[] _markerAlive;
    /// <summary>這個標記這一輪落在哪一格。</summary>
    private readonly int[] _markerTarget;
    /// <summary>上一輪落在哪一格，局部搜尋的起點。</summary>
    private readonly int[] _markerCell;
    /// <summary>壓實時的「舊索引 → 新索引」。</summary>
    private readonly int[] _remap;
    /// <summary>目前使用中的標記數（陣列前段有效）。</summary>
    private int _markerCount;

    // 每格的標記清單（CSR：start/count 指進 _markersByCell），每次取樣重建
    /// <summary>每格在 <see cref="_markersByCell"/> 中的起始位置。</summary>
    private readonly int[] _cellMarkerStart;
    /// <summary>每格的標記數。</summary>
    private readonly int[] _cellMarkerCount;
    /// <summary>依格子排序的標記索引。</summary>
    private readonly int[] _markersByCell;

    // 取樣當下寫進格子的值。地質作用改完格子之後要用「差值」寫回標記，
    // 直接覆蓋會讓同一格裡的所有標記變成一模一樣，多個標記就白費了。
    /// <summary>取樣當下寫進格子的地殼厚度，寫回時用差值。</summary>
    private readonly float[] _sampledThickness;
    /// <summary>取樣當下寫進格子的地殼年齡，寫回時用差值。</summary>
    private readonly float[] _sampledAge;
    /// <summary>取樣當下寫進格子的地殼密度，寫回時用差值。</summary>
    private readonly float[] _sampledDensity;

    /// <summary>診斷用：最近一步各來源新增的地殼厚度總和（公里·格），用來找出誰在超產。</summary>
    public double LastRidgeCrustKm { get; private set; }

    /// <summary>診斷用：島弧增厚新增的地殼厚度總和（公里·格）。</summary>
    public double LastArcCrustKm { get; private set; }

    /// <summary>診斷用：增生新增的地殼厚度總和（公里·格）。</summary>
    public double LastAccretionCrustKm { get; private set; }

    /// <summary>診斷用：隱沒消耗掉的地殼厚度總和（公里·格，正值代表移除）。</summary>
    public double LastSubductedCrustKm { get; private set; }

    /// <summary>
    /// 診斷用：聚合格三種結局各發生幾次——**海洋隱沒**。
    ///
    /// 這三個計數器要一起看。聚合處的擁擠只有三條出路：一邊是洋殼就隱沒
    /// （這一個）、兩邊都是大陸就對撞增厚（<see cref="LastContinentCollisionCount"/>）、
    /// 浮力差不夠大就什麼都不做、留著擠成山（<see cref="LastCollisionThickeningCount"/>）。
    /// **光看隱沒的體積看不出系統走哪條路**：體積下降可能是隱沒變少，
    /// 也可能是每次隱沒的量變小，兩者要修的地方完全不同。
    /// </summary>
    public int LastOceanSubductionCount { get; private set; }

    /// <summary>診斷用：聚合格三種結局之一——兩塊大陸對撞增厚（造山）。見 <see cref="LastOceanSubductionCount"/>。</summary>
    public int LastContinentCollisionCount { get; private set; }

    /// <summary>
    /// 診斷用：聚合格三種結局之一——**浮力差不夠大，不隱沒也不對撞，留著擠著**。
    ///
    /// 這一條是最容易被忽略的出路，因為它「什麼都沒做」，在任何質量通量的統計裡都是隱形的。
    /// 見 <see cref="LastOceanSubductionCount"/>。
    /// </summary>
    public int LastCollisionThickeningCount { get; private set; }

    /// <summary>診斷用：目前存活的標記數量。跟格數比對可以看出標記有沒有異常增生。</summary>
    public int MarkerCount => _markerCount;


    /// <summary>診斷用：取樣錯配的空格沿用舊厚度時，總共注入了多少地殼（公里·格）。</summary>
    public double LastMisfitFillCrustKm { get; private set; }

    /// <summary>診斷用：取樣錯配的空格有幾個。</summary>
    public int LastMisfitFillCount { get; private set; }

    /// <summary>診斷用：其中有幾個連一個鄰居標記都借不到（會退回最薄地殼）。</summary>
    public int LastNoDonorCount { get; private set; }


    /// <summary>
    /// 每一格「擠不下、暫時無主」的標記（-1 表示沒有）。取樣錯配造成的多餘標記先放這裡，
    /// 稍後配給附近的錯配空格，見 <see cref="Rasterize"/> 步驟3a 與 <see cref="TryTakeSpare"/>。
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
    /// <param name="transport">地殼怎麼跟著板塊移動，預設 v1 的標記法。</param>
    public PlateTectonics(SimulationGrid grid, int plateCount, CrustTransport transport = CrustTransport.Markers)
    {
        _grid = grid;
        Transport = transport;
        if (transport == CrustTransport.PlateFrame)
        {
            _frames = new PlateFrameCrust(grid.CellCount, plateCount);
            _newCrustCells = new bool[grid.CellCount];
            _pointKeys = new int[grid.CellCount];
            Dynamics.SpeedCapEnabled = true;
            Dynamics.CollisionResistanceEnabled = true;
        }
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
        if (_frames is not null)
        {
            _frames.Initialize(state, plates);
            // 大陸配置的劇本：還沒裂開的大島，板塊編號先休眠（否則 0 格的板塊會被當成死掉、馬上從別的板塊重生）
            if (_scenario is not null)
                foreach (var (plate, until) in _scenario.InitialDormancy) _frames.SetDormantUntil(plate, until);
            return;
        }

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
        _elapsedMyr = state.ElapsedYears / 1_000_000.0;

        // 劇本的強制焊接排在最前面：用的是上一步結束時的歸屬與旋轉，焊完之後這一步的邊界分類就看得到合併後的板塊
        if (Scenario is { } scenario && _frames is not null)
        {
            foreach (var (keep, gone) in scenario.DueWelds(_elapsedMyr, plates.OwnerByCell).ToList())
                _frames.ForceWeld(grid, plates, keep, gone);
            foreach (var (parent, child, region, sink) in scenario.DueSplits(_elapsedMyr, plates.OwnerByCell).ToList())
            {
                _frames.SplitOff(plates, parent, child, region);
                if (sink is not null) _frames.SinkToOcean(child, sink);
            }
        }
        LastRidgeCrustKm = 0;
        LastMisfitFillCrustKm = 0;
        LastMisfitFillCount = 0;
        LastPairedFillCount = 0;
        LastNoDonorCount = 0;
        LastArcCrustKm = 0;
        LastAccretionCrustKm = 0;
        LastSubductedCrustKm = 0;
        LastOceanSubductionCount = 0;
        LastContinentCollisionCount = 0;
        LastCollisionThickeningCount = 0;

        // 板塊的範圍與邊界類型只取決於板塊的幾何與速度，跟地殼無關，所以先算。
        // **順序很重要**：取樣時要靠邊界類型才能分辨「這個空格是真的張裂，還是只是取樣的錯配」，
        // 只有真張裂處才該生成新的海洋地殼。
        plates.Advance(grid, deltaMyr);
        ClassifyBoundaries(grid, state, plates);

        // **邊界分類完才更新板塊速度**：驅動力來自邊界（板片拉力在隱沒帶、洋脊推力在張裂帶），
        // 所以要先知道邊界在哪、哪一側在隱沒。解出來的速度下一步生效。
        Dynamics.UpdateVelocities(grid, state, plates, _convergingNeighbor, deltaMyr);
        Scenario?.ApplyVelocities(grid, state, plates, _elapsedMyr, (a, b) => _frames?.WereColliding(a, b) == true);

        if (_frames is not null) StepPlateFrames(grid, state, plates, deltaMyr);
        else MoveMarkersAndMaybeRasterize(grid, state, plates, deltaMyr);
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
    /// 板塊座標系的搬運：每一步都更新（沒有「漂移滿一格才取樣」的批次化），長出新地殼的格子算中洋脊火山。
    /// </summary>
    private void StepPlateFrames(SimulationGrid grid, SimulationState state, PlateSet plates, double deltaMyr)
    {
        _frames!.Step(grid, state, plates, deltaMyr, _newCrustCells);
        LastRidgeCrustKm = _frames.LastNewCrustCells * (double)Isostasy.OceanicCrustThicknessKm;
        LastSubductedCrustKm = _frames.LastConsumedCrustKm;
        LastAccretionCrustKm = _frames.LastAccretedCrustKm;
        LastOceanSubductionCount = _frames.LastSubductions;
        LastContinentCollisionCount = _frames.LastContinentCollisions;

        // 中洋脊火山：跟 v1 同一個量綱（新生地殼厚度 ÷ 經過的時間），只是這裡每步都更新
        var rate = Isostasy.OceanicCrustThicknessKm / MathF.Max((float)deltaMyr, 1e-6f);
        Array.Clear(LastFrameNewCrustByBoundary);
        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            if (!_newCrustCells![cell]) continue;
            state.VolcanicActivity[cell] += rate;
            LastFrameNewCrustByBoundary[(int)state.Boundary[cell]]++;
        }
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
