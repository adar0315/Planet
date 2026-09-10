using Planet.Simulation;

namespace Planet.Service;

/// <summary>模擬目前的概況，給前端顯示與除錯用。</summary>
/// <param name="Frequency">粗網格的細分頻率。</param>
/// <param name="CellCount">格數。</param>
/// <param name="PlanetRadiusMeters">星球半徑（公尺），跟正式的2,000萬格星球是同一顆。</param>
/// <param name="AverageCellEdgeMeters">粗網格每格的大致邊長（公尺），方便對照「一格代表多大範圍」。</param>
/// <param name="ElapsedYears">模擬已經跑了多少年。</param>
/// <param name="StepCount">已經跑了幾步。</param>
/// <param name="YearsPerStep">每一步代表多少年。</param>
/// <param name="PlateCount">板塊數量。</param>
/// <param name="PlumeCount">目前活躍的地函熱柱數量。</param>
/// <param name="LandFraction">
/// 陸地佔全球表面的比例（0~1）。**現階段還不能直接跟地球的0.29比**——
/// 海平面還是固定值、水量並不守恆（見 <c>SimulationEngine.ApplyIsostasyAndFillWater</c>），
/// 那個基準線要等階段3的水循環才成立。
/// </param>
/// <param name="TextureWidth">模擬貼圖的寬度（像素）。前端**不要寫死**，一律從這裡拿。</param>
/// <param name="TextureHeight">模擬貼圖的高度（像素）。</param>
public record SimulationStatusDto(
    int Frequency,
    int CellCount,
    double PlanetRadiusMeters,
    double AverageCellEdgeMeters,
    double ElapsedYears,
    long StepCount,
    double YearsPerStep,
    int PlateCount,
    int PlumeCount,
    double LandFraction,
    int TextureWidth,
    int TextureHeight);

/// <summary>
/// 地質演化模擬的持有者（Singleton）。狀態常駐記憶體，**每步不寫資料庫**——
/// 模擬是即時的，每步落地會被 I/O 淹沒（見 docs/geological-simulation-plan.md）。
///
/// 目前是階段2：板塊運動、隱沒、增生、火山（中洋脊／島弧／地函熱柱）都已經在跑。
/// 之所以先把「看得見」做完才寫模擬邏輯，是因為地質模擬出錯時症狀通常是
/// 「地形看起來怪怪的」，沒有畫面可看的話根本無從判斷是模型錯還是程式錯。
///
/// **所有取用引擎的操作都在同一把鎖裡**：<see cref="SimulationEngine"/> 本身不是執行緒安全的
/// （內部有大量重複使用的暫存緩衝），而這是 Singleton，同時會有推進與繪製兩種請求打進來。
/// </summary>
public class SimulationService
{
    private readonly PlanetMetadataService _metadataService;
    private readonly Lock _lock = new();

    private SimulationGrid? _grid;
    private SimulationEngine? _engine;
    private SimulationFieldRenderer? _renderer;

    /// <summary>建立模擬服務。</summary>
    /// <param name="metadataService">
    /// 用來取得**星球實際半徑**。粗網格的拓樸雖然是低頻率的，但代表的必須是同一顆星球，
    /// 所以半徑要向這裡拿，不能用 <c>GoldbergPlanetGenerator</c> 在粗頻率下自己算的那個
    /// （那是「六角形邊長固定1公里」反推的，n=150 只會得到196公里的小星球）。
    /// </param>
    public SimulationService(PlanetMetadataService metadataService)
    {
        _metadataService = metadataService;
    }

    /// <summary>取得目前的模擬概況。</summary>
    /// <param name="ct">取消權杖。</param>
    public async Task<SimulationStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        var (grid, engine, _) = await EnsureInitializedAsync(ct);

        lock (_lock)
        {
            // 每格大致邊長：由平均面積反推（當成正六邊形，面積 = (3√3/2)·邊長²）。
            // 只是給人看的參考值，不是拿來做物理計算的。
            var edge = Math.Sqrt(grid.AverageCellAreaSquareMeters / (3.0 * Math.Sqrt(3.0) / 2.0));

            return new SimulationStatusDto(
                grid.Frequency,
                grid.CellCount,
                grid.PlanetRadiusMeters,
                edge,
                engine.State.ElapsedYears,
                engine.State.StepCount,
                engine.YearsPerStep,
                engine.Plates.Plates.Length,
                engine.Plumes.Plumes.Length,
                engine.LandFraction(),
                SimulationFieldRenderer.Width,
                SimulationFieldRenderer.Height);
        }
    }

    /// <summary>推進模擬。</summary>
    /// <param name="steps">要跑幾步。</param>
    /// <param name="ct">取消權杖。</param>
    /// <returns>推進之後的概況。</returns>
    public async Task<SimulationStatusDto> StepAsync(int steps, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(steps);

        var (_, engine, _) = await EnsureInitializedAsync(ct);
        lock (_lock)
        {
            engine.Step(steps);
        }

        return await GetStatusAsync(ct);
    }

    /// <summary>把目前的模擬狀態畫成等距柱狀投影 RGBA 貼圖。</summary>
    /// <param name="field">要顯示哪個欄位。</param>
    /// <param name="ct">取消權杖。</param>
    public async Task<byte[]> RenderAsync(SimulationField field, CancellationToken ct = default)
    {
        var (_, engine, renderer) = await EnsureInitializedAsync(ct);
        lock (_lock)
        {
            return renderer.Render(engine.State, field);
        }
    }

    /// <summary>
    /// 分析目前的海洋連通性：內海有沒有被封死、水域裡有幾條獨立環路、最窄的水道多寬。
    ///
    /// 這是「水道串通」那道後天修飾的**量測端**——要先知道哪裡不通才知道要開哪裡。
    /// </summary>
    /// <param name="ct">取消權杖。</param>
    /// <returns>連通性報告。</returns>
    public async Task<OceanTopologyReport> AnalyzeOceanAsync(CancellationToken ct = default)
    {
        var (grid, engine, _) = await EnsureInitializedAsync(ct);
        lock (_lock)
        {
            return OceanTopology.Analyze(grid, engine.State);
        }
    }

    /// <summary>把模擬重設回起始狀態（平坦、裸露、全部覆蓋海水）。網格與對應表會沿用，不重建。</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _engine?.Reset(); // 還沒初始化過的話，下次取用時本來就會是起始狀態
        }
    }

    /// <summary>
    /// 確保網格、狀態與貼圖對應表都已建好。三者都只建一次：
    /// 建網格（含鄰接表）n=150 實測約0.8秒，像素對應表也要算一次，
    /// 每次請求重建的話即時模擬根本跑不動。
    /// </summary>
    private async Task<(SimulationGrid Grid, SimulationEngine Engine, SimulationFieldRenderer Renderer)> EnsureInitializedAsync(CancellationToken ct)
    {
        // 先在鎖外面拿半徑：那是 async 的（會查DB），不能在 lock 裡面 await。
        if (_grid is null)
        {
            var metadata = await _metadataService.GetAsync(ct);
            var radius = metadata?.RadiusMeters
                ?? throw new InvalidOperationException(
                    "找不到星球中繼資料，無法得知星球半徑。模擬的粗網格必須代表跟正式星球同一顆星球，" +
                    "所以半徑一定要從 PlanetMetadata 來，不能用生成器在粗頻率下自己算的值。");

            lock (_lock)
            {
                // 雙重檢查：等半徑的期間可能已經有別的請求建好了
                if (_grid is null)
                {
                    var grid = SimulationGrid.Build(radius);
                    _engine = new SimulationEngine(grid);
                    _renderer = new SimulationFieldRenderer(grid);
                    _grid = grid; // 最後才指派：其他執行緒是靠這個欄位判斷有沒有初始化完成
                }
            }
        }

        lock (_lock)
        {
            return (_grid!, _engine!, _renderer!);
        }
    }
}
