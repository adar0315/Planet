using Planet.Simulation;

namespace Planet.Service;

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
///
/// **只跑 v2 的大陸劇本**（板塊座標系＋使用者草圖的大陸配置，2026-10-20 使用者決定，見 v2 規劃 P9）。
/// v1（標記法、隨機板塊）的程式與測試保留，但服務不再建 v1 的引擎；v1 的舊存檔列出來會標示不相容。
/// </summary>
public class SimulationService
{
    /// <summary>大陸劇本的種子（草圖配置的雜訊、地函熱柱都由它決定；校準都是在 42 上做的）。</summary>
    public const int ScenarioSeed = 42;

    /// <summary>每隔多少年自動存一個檢查點（「從某一步分岔」用，2026-10-20 使用者選定）。</summary>
    public const double CheckpointIntervalYears = 5_000_000;

    /// <summary>檢查點最多留幾個（每個約 32 MB），超過就刪最舊的。</summary>
    public const int MaxCheckpoints = 10;

    /// <summary>這一輪的標記（重設、載入時換新），檢查點的名字帶著它：分岔之後的檢查點不會蓋掉原本那條時間線的。</summary>
    private string _runTag = NewRunTag();

    /// <summary>產生一輪的標記（UTC 時間 MMddHHmmss）。</summary>
    /// <returns>標記字串。</returns>
    private static string NewRunTag() => DateTime.UtcNow.ToString("MMddHHmmss");

    /// <summary>建立 v2 劇本的引擎。</summary>
    /// <param name="grid">網格。</param>
    private static SimulationEngine CreateEngine(SimulationGrid grid) =>
        new(grid, ScenarioSeed, crustTransport: CrustTransport.PlateFrame, continentLayout: ContinentLayoutParameters.Default);

    /// <summary>星球記錄服務。</summary>
    private readonly PlanetMetadataService _metadataService;
    /// <summary>存檔的檔案存取（名稱檢查、寫入、列表、刪除都在那裡）。</summary>
    private readonly SnapshotStore _snapshots;
    /// <summary>保護模擬狀態的鎖：引擎與其下的模型都不是執行緒安全的，所有取用都要在這把鎖裡。</summary>
    private readonly Lock _lock = new();

    /// <summary>目前的模擬網格；<see langword="null"/> 表示尚未建立。</summary>
    private SimulationGrid? _grid;
    /// <summary>目前的模擬引擎；<see langword="null"/> 表示尚未建立。</summary>
    private SimulationEngine? _engine;
    /// <summary>把模擬欄位畫成貼圖的渲染器。</summary>
    private SimulationFieldRenderer? _renderer;

    /// <summary>建立模擬服務。</summary>
    /// <param name="metadataService">
    /// 用來取得**星球實際半徑**。粗網格的拓樸雖然是低頻率的，但代表的必須是同一顆星球，
    /// 所以半徑要向這裡拿，不能用 <c>GoldbergPlanetGenerator</c> 在粗頻率下自己算的那個
    /// （那是「六角形邊長固定1公里」反推的，n=150 只會得到196公里的小星球）。
    /// </param>
    /// <param name="snapshots">存檔的檔案存取。</param>
    public SimulationService(PlanetMetadataService metadataService, SnapshotStore snapshots)
    {
        _metadataService = metadataService;
        _snapshots = snapshots;
    }

    /// <summary>取得目前的模擬概況。</summary>
    /// <param name="ct">取消權杖。</param>
    public async Task<SimulationStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        var (grid, engine, _) = await EnsureInitializedAsync(ct);

        lock (_lock)
        {
            return BuildStatus(grid, engine);
        }
    }

    /// <summary>
    /// 固定儀表（v2 規劃 P0）：每個階段驗收要一起看的全部數字，一次拿到。
    /// 見 <see cref="SimulationDiagnostics"/> 的說明——只看其中一兩項一定會被騙。
    /// </summary>
    /// <param name="resetTimings">回傳之後把每步耗時歸零，從下一步開始重新累積。</param>
    /// <param name="ct">取消權杖。</param>
    public async Task<SimulationDashboardDto> GetDashboardAsync(bool resetTimings = false, CancellationToken ct = default)
    {
        var (grid, engine, _) = await EnsureInitializedAsync(ct);
        lock (_lock)
        {
            var dashboard = new SimulationDashboardDto(
                BuildStatus(grid, engine),
                ContinentConfiguration.Analyze(grid, engine.State, engine.StopCriteria),
                OceanTopology.Analyze(grid, engine.State),
                SimulationDiagnostics.Hypsometry(engine.State),
                SimulationDiagnostics.LandMassBuckets(grid, engine.State),
                SimulationDiagnostics.Mechanisms(engine),
                engine.Timings.AverageMillisecondsPerStep(),
                engine.Timings.StepsMeasured,
                SimulationDiagnostics.StateHash(engine).ToString("x16"));
            if (resetTimings) engine.Timings.Reset();
            return dashboard;
        }
    }

    /// <summary>組出概況。呼叫端必須已經持有 <see cref="_lock"/>。</summary>
    private static SimulationStatusDto BuildStatus(SimulationGrid grid, SimulationEngine engine)
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
            SimulationFieldRenderer.Height,
            engine.LastContinentReport);
    }

    /// <summary>推進模擬。</summary>
    /// <param name="steps">要跑幾步。</param>
    /// <param name="ct">取消權杖。</param>
    /// <returns>推進之後的概況。</returns>
    public async Task<SimulationStatusDto> StepAsync(int steps, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(steps);

        var (_, engine, _) = await EnsureInitializedAsync(ct);
        var triggered = false;
        long stepCount = 0;
        var remaining = steps;

        // 在每個檢查點的邊界分段跑：一次推進好幾百步（播放每次最多 200 步＝10 Myr、手動可以到 2,000 步）時，
        // 只在請求結束才判斷的話中間跨過的邊界全部漏掉（P9 第一次試：從 5 Myr 一口氣跑到停止條件，只存到 41 Myr 那一個）
        while (remaining > 0 && !triggered)
        {
            bool checkpoint;
            double elapsed;
            lock (_lock)
            {
                var before = engine.State.ElapsedYears;
                var boundary = (Math.Floor(before / CheckpointIntervalYears) + 1) * CheckpointIntervalYears;
                var toBoundary = (int)Math.Ceiling((boundary - before) / engine.YearsPerStep);
                // 即時觀看：陸地配置一成立就停（v2 規劃的停止條件），不要一路跑過去
                remaining -= engine.StepUntilStopCondition(Math.Clamp(toBoundary, 1, remaining));
                triggered = engine.StopConditionTriggered;
                stepCount = engine.State.StepCount;
                elapsed = engine.State.ElapsedYears;
                checkpoint = Math.Floor(elapsed / CheckpointIntervalYears) > Math.Floor(before / CheckpointIntervalYears);
            }

            if (checkpoint)
            {
                await SaveAsync($"cp-{_runTag}-{elapsed / 1e6:000}myr", ct, SnapshotKind.Checkpoint);
                _snapshots.PruneCheckpoints(MaxCheckpoints);
            }
        }

        if (!triggered) return await GetStatusAsync(ct);

        // 自動存檔：那個配置可能只維持一陣子（板塊構造是循環的），錯過了要再等一整個旋迴
        var saveName = $"auto-stop-step{stepCount}";
        await SaveAsync(saveName, ct, SnapshotKind.StopCondition);
        return (await GetStatusAsync(ct)) with { StoppedByCondition = true, AutoSaveName = saveName };
    }

    /// <summary>把目前的模擬狀態畫成等距柱狀投影 RGBA 貼圖。</summary>
    /// <param name="field">要顯示哪個欄位。</param>
    /// <param name="ct">取消權杖。</param>
    public async Task<byte[]> RenderAsync(SimulationField field, CancellationToken ct = default)
    {
        var (_, engine, renderer) = await EnsureInitializedAsync(ct);
        lock (_lock)
        {
            return renderer.Render(engine.State, field, engine.Hydrology);
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

    /// <summary>
    /// 把又小又矮的孤立碎島沉回水面下（交付地形前的表面處理）。
    /// </summary>
    /// <param name="ct">取消權杖。</param>
    /// <returns>清了幾塊、留了幾塊。</returns>
    public async Task<SmallIslandCleanupReport> CleanUpSmallIslandsAsync(CancellationToken ct = default)
    {
        var (_, engine, _) = await EnsureInitializedAsync(ct);
        lock (_lock)
        {
            return engine.CleanUpSmallIslands();
        }
    }

    /// <summary>把目前的模擬狀態存成檔案（旁邊寫一份說明檔）。</summary>
    /// <param name="name">存檔名稱（英數、底線、連字號）。</param>
    /// <param name="ct">取消權杖。</param>
    /// <param name="kind">存檔怎麼來的。</param>
    /// <returns>存檔的位元組數。</returns>
    /// <exception cref="ArgumentException">名稱不合規。</exception>
    public async Task<long> SaveAsync(string name, CancellationToken ct = default, SnapshotKind kind = SnapshotKind.Manual)
    {
        _snapshots.PathOf(name); // 名稱不合規的話，在建引擎之前就擋掉
        var (_, engine, _) = await EnsureInitializedAsync(ct);

        // 整個寫入都在引擎的鎖裡：寫到一半引擎被推進的話，存下來的是前後不一致的狀態
        lock (_lock)
        {
            return _snapshots.Write(
                name, kind, engine.State.StepCount, engine.State.ElapsedYears,
                stream => SimulationSnapshot.Write(engine, stream));
        }
    }

    /// <summary>
    /// 把存檔讀進一份**獨立的**引擎，不動線上正在跑的模擬。放大成地塊要在複本上做——
    /// 碎島清除會改地形，不能改到使用者正在看的那顆星球。
    /// </summary>
    /// <param name="name">存檔名稱。</param>
    /// <param name="ct">取消權杖。</param>
    /// <exception cref="FileNotFoundException">找不到這個存檔。</exception>
    public async Task<(SimulationGrid Grid, SimulationEngine Engine)> LoadDetachedAsync(string name, CancellationToken ct = default)
    {
        await using var stream = _snapshots.OpenRead(name);

        // 網格建好之後不會再變（鄰接表、取樣用的桶都是唯讀或執行緒安全的），可以跟線上的模擬共用
        var (grid, _, _) = await EnsureInitializedAsync(ct);
        var engine = CreateEngine(grid);
        SimulationSnapshot.Restore(engine, stream);
        return (grid, engine);
    }

    /// <summary>
    /// 從檔案把模擬狀態讀回來，取代目前的內容。
    ///
    /// 讀進一份**新建的**引擎、成功之後才換上去：直接讀進線上的引擎的話，存檔讀到一半失敗
    /// （檔案壞掉、格式不合）會留下一個半新半舊的狀態，而且沒有東西可以還原。
    /// </summary>
    /// <param name="name">存檔名稱。</param>
    /// <param name="ct">取消權杖。</param>
    /// <returns>讀回來之後的概況。</returns>
    /// <exception cref="FileNotFoundException">找不到這個存檔。</exception>
    public async Task<SimulationStatusDto> LoadAsync(string name, CancellationToken ct = default)
    {
        var (_, restored) = await LoadDetachedAsync(name, ct);
        lock (_lock)
        {
            _engine = restored;
            _runTag = NewRunTag();
        }

        return await GetStatusAsync(ct);
    }

    /// <summary>列出所有存檔。</summary>
    /// <returns>每個存檔的名稱、大小與存檔時間。</returns>
    public IReadOnlyList<SnapshotInfo> ListSnapshots() => _snapshots.List();

    /// <summary>刪除一個存檔（連同說明檔）。</summary>
    /// <param name="name">存檔名稱。</param>
    /// <exception cref="FileNotFoundException">找不到這個存檔。</exception>
    public void DeleteSnapshot(string name) => _snapshots.Delete(name);

    /// <summary>
    /// 把模擬重設回大陸劇本的起點。網格與貼圖對應表沿用，**引擎整個重建**：
    /// <see cref="SimulationEngine.Reset"/> 不會把每個子模型都歸零（例如氣候的緯度帶溫度從上一次的解出發），
    /// 重設之後跑出來的星球跟剛啟動時的不一樣（P9 實測：同樣第 819 步觸發，存檔內容卻不同）。重建約幾秒鐘。
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            if (_grid is not null) _engine = CreateEngine(_grid); // 還沒初始化過的話，下次取用時本來就會是起始狀態
            _runTag = NewRunTag();
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
                    _engine = CreateEngine(grid);
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
