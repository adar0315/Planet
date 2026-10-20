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
/// 陸地佔全球表面的比例（0~1）。水量守恆、海平面由水量反解（見 <c>SimulationEngine.SolveSeaLevel</c>），
/// 所以可以跟地球的 0.29 對照。
/// </param>
/// <param name="TextureWidth">模擬貼圖的寬度（像素）。前端**不要寫死**，一律從這裡拿。</param>
/// <param name="TextureHeight">模擬貼圖的高度（像素）。</param>
/// <param name="Continents">最近一次檢查的陸地配置（停止條件的判斷依據），還沒檢查過是 null。</param>
/// <param name="StoppedByCondition">這次推進是不是因為停止條件成立而提早停下。前端看到 true 就要停止播放。</param>
/// <param name="AutoSaveName">停止條件成立時自動存檔的名稱。</param>
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
    int TextureHeight,
    ContinentConfigurationReport? Continents = null,
    bool StoppedByCondition = false,
    string? AutoSaveName = null);

/// <summary>固定儀表（見 <see cref="SimulationService.GetDashboardAsync"/>）。</summary>
/// <param name="Status">概況。</param>
/// <param name="Continents">陸地配置（現算的，不是停止條件上次檢查的快取）。</param>
/// <param name="Ocean">海洋連通性。</param>
/// <param name="Hypsometry">高度分布（相對海平面，每 100 公尺一格）。</param>
/// <param name="LandMasses">陸塊大小分布。</param>
/// <param name="Mechanisms">各機制上一步的觸發量。</param>
/// <param name="Timings">每步耗時分項（平均毫秒）。</param>
/// <param name="TimingSteps">耗時累積了幾步。</param>
/// <param name="StateHash">整個狀態的雜湊，跨行程重現檢查用。</param>
public record SimulationDashboardDto(
    SimulationStatusDto Status,
    ContinentConfigurationReport Continents,
    OceanTopologyReport Ocean,
    IReadOnlyList<HypsometryBin> Hypsometry,
    IReadOnlyList<LandMassBucket> LandMasses,
    MechanismCounts Mechanisms,
    IReadOnlyList<StepTimingEntry> Timings,
    int TimingSteps,
    string StateHash);

/// <summary>存檔是怎麼來的。以字串序列化（前端與說明檔都看名稱，不看數字）。</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<SnapshotKind>))]
public enum SnapshotKind
{
    /// <summary>使用者手動存的（含分岔時另存的新名字）。</summary>
    Manual,

    /// <summary>跑的時候每 <see cref="SimulationService.CheckpointIntervalYears"/> 自動存的檢查點，只留最近幾個。</summary>
    Checkpoint,

    /// <summary>停止條件成立時自動存的。</summary>
    StopCondition,
}

/// <summary>一個存檔的概況。</summary>
/// <param name="Name">存檔名稱（不含副檔名）。</param>
/// <param name="SizeBytes">檔案大小（位元組）。</param>
/// <param name="SavedAtUtc">存檔時間（UTC）。</param>
/// <param name="Kind">存檔怎麼來的（舊版沒有說明檔的存檔是 null）。</param>
/// <param name="StepCount">存檔時的步數（舊版存檔是 null）。</param>
/// <param name="ElapsedYears">存檔時的年代（年，舊版存檔是 null）。</param>
/// <param name="Compatible">這個版本讀不讀得回來（舊格式、v1 的存檔是 false，列出來但不能載入或放大）。</param>
public record SnapshotInfo(
    string Name,
    long SizeBytes,
    DateTime SavedAtUtc,
    SnapshotKind? Kind = null,
    long? StepCount = null,
    double? ElapsedYears = null,
    bool Compatible = true);

/// <summary>存檔旁邊的說明檔（<c>名稱.json</c>）：列表不必把幾十 MB 的存檔讀進來就知道步數與年代。</summary>
/// <param name="Kind">存檔怎麼來的。</param>
/// <param name="StepCount">步數。</param>
/// <param name="ElapsedYears">年代（年）。</param>
internal record SnapshotSidecar(SnapshotKind Kind, long StepCount, double ElapsedYears);

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

    private static string NewRunTag() => DateTime.UtcNow.ToString("MMddHHmmss");

    /// <summary>建立 v2 劇本的引擎。</summary>
    /// <param name="grid">網格。</param>
    private static SimulationEngine CreateEngine(SimulationGrid grid) =>
        new(grid, ScenarioSeed, crustTransport: CrustTransport.PlateFrame, continentLayout: ContinentLayoutParameters.Default);

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
                PruneCheckpoints();
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

    /// <summary>
    /// 存檔放在服務執行檔旁邊的 <c>snapshots</c> 目錄。
    ///
    /// 不做成設定值：這是「不要因為服務重啟就丟掉幾千步的星球」用的，
    /// 放在部署目錄旁邊最不容易在重新 publish 時被清掉（publish 不會刪除不認識的子目錄）。
    /// </summary>
    private static string SnapshotDirectory =>
        Path.Combine(AppContext.BaseDirectory, "snapshots");

    /// <summary>
    /// 檢查存檔名稱。**只允許英數、底線與連字號**。
    ///
    /// 這是路徑穿越的防線：名稱會直接接到目錄後面，
    /// <c>..\..\appsettings.Production.json</c> 這種名稱可以寫到部署目錄外面去。
    /// 用白名單而不是黑名單——黑名單漏一個就破功。
    /// </summary>
    /// <param name="name">存檔名稱。</param>
    /// <returns>完整路徑。</returns>
    /// <exception cref="ArgumentException">名稱不合規。</exception>
    private static string SnapshotPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64
            || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-'))
        {
            throw new ArgumentException(
                "存檔名稱只能用英數、底線與連字號，長度1~64。", nameof(name));
        }

        return Path.Combine(SnapshotDirectory, name + ".planet");
    }

    /// <summary>把目前的模擬狀態存成檔案（旁邊寫一份說明檔，見 <see cref="SnapshotSidecar"/>）。</summary>
    /// <param name="name">存檔名稱（英數、底線、連字號）。</param>
    /// <param name="ct">取消權杖。</param>
    /// <param name="kind">存檔怎麼來的。</param>
    /// <returns>存檔的位元組數。</returns>
    public async Task<long> SaveAsync(string name, CancellationToken ct = default, SnapshotKind kind = SnapshotKind.Manual)
    {
        var path = SnapshotPath(name);
        var (_, engine, _) = await EnsureInitializedAsync(ct);

        Directory.CreateDirectory(SnapshotDirectory);

        // 先寫到暫存檔再換過去：中途失敗的話原本的存檔還在，
        // 不會變成「有一個讀不回來的半截檔案」。
        var temporary = path + ".tmp";
        lock (_lock)
        {
            using (var stream = File.Create(temporary))
            {
                SimulationSnapshot.Write(engine, stream);
            }

            File.Move(temporary, path, overwrite: true);
            File.WriteAllText(SidecarPath(path), System.Text.Json.JsonSerializer.Serialize(
                new SnapshotSidecar(kind, engine.State.StepCount, engine.State.ElapsedYears)));
        }

        return new FileInfo(path).Length;
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
        var path = SnapshotPath(name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到存檔「{name}」。", path);
        }

        // 網格建好之後不會再變（鄰接表、取樣用的桶都是唯讀或執行緒安全的），可以跟線上的模擬共用
        var (grid, _, _) = await EnsureInitializedAsync(ct);
        var engine = CreateEngine(grid);
        await using var stream = File.OpenRead(path);
        SimulationSnapshot.Restore(engine, stream);
        return (grid, engine);
    }

    /// <summary>從檔案把模擬狀態讀回來，覆蓋目前的內容。</summary>
    /// <param name="name">存檔名稱。</param>
    /// <param name="ct">取消權杖。</param>
    /// <returns>讀回來之後的概況。</returns>
    /// <exception cref="FileNotFoundException">找不到這個存檔。</exception>
    public async Task<SimulationStatusDto> LoadAsync(string name, CancellationToken ct = default)
    {
        var path = SnapshotPath(name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到存檔「{name}」。", path);
        }

        var (_, engine, _) = await EnsureInitializedAsync(ct);
        lock (_lock)
        {
            using var stream = File.OpenRead(path);
            SimulationSnapshot.Restore(engine, stream);
            _runTag = NewRunTag();
        }

        return await GetStatusAsync(ct);
    }

    /// <summary>列出所有存檔。</summary>
    /// <returns>每個存檔的名稱、大小與存檔時間。</returns>
    public IReadOnlyList<SnapshotInfo> ListSnapshots()
    {
        if (!Directory.Exists(SnapshotDirectory)) return [];

        return Directory.EnumerateFiles(SnapshotDirectory, "*.planet")
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(Describe)
            .ToList();
    }

    private static SnapshotInfo Describe(FileInfo file)
    {
        bool compatible;
        try
        {
            using var stream = file.OpenRead();
            compatible = SimulationSnapshot.IsCurrentFormat(stream);
        }
        catch (IOException)
        {
            compatible = false; // 正在寫入（另一個請求在存檔）
        }

        SnapshotSidecar? sidecar = null;
        var sidecarPath = SidecarPath(file.FullName);
        if (File.Exists(sidecarPath))
        {
            try
            {
                sidecar = System.Text.Json.JsonSerializer.Deserialize<SnapshotSidecar>(File.ReadAllText(sidecarPath));
            }
            catch (System.Text.Json.JsonException)
            {
                // 說明檔壞了就當成沒有，存檔本身還是列得出來
            }
        }

        return new SnapshotInfo(
            Path.GetFileNameWithoutExtension(file.Name), file.Length, file.LastWriteTimeUtc,
            sidecar?.Kind, sidecar?.StepCount, sidecar?.ElapsedYears, compatible);
    }

    private static string SidecarPath(string snapshotPath) => Path.ChangeExtension(snapshotPath, ".json");

    /// <summary>刪除一個存檔（連同說明檔）。</summary>
    /// <param name="name">存檔名稱。</param>
    /// <exception cref="FileNotFoundException">找不到這個存檔。</exception>
    public void DeleteSnapshot(string name)
    {
        var path = SnapshotPath(name);
        if (!File.Exists(path)) throw new FileNotFoundException($"找不到存檔「{name}」。", path);
        lock (_lock)
        {
            File.Delete(path);
            File.Delete(SidecarPath(path));
        }
    }

    /// <summary>檢查點超過 <see cref="MaxCheckpoints"/> 個就刪最舊的（只看說明檔標成檢查點的，手動存檔不動）。</summary>
    private void PruneCheckpoints()
    {
        var stale = ListSnapshots()
            .Where(s => s.Kind == SnapshotKind.Checkpoint)
            .OrderByDescending(s => s.SavedAtUtc)
            .Skip(MaxCheckpoints)
            .ToList();
        foreach (var snapshot in stale) DeleteSnapshot(snapshot.Name);
    }

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
