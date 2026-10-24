using System.Diagnostics;
using Planet.Data;
using Planet.Generation;
using Planet.Simulation;

namespace Planet.Service;

/// <summary>
/// 把一個模擬存檔放大成 2,000 萬地塊（v2 規劃 P1「端到端管線」）。
///
/// 流程：讀存檔到一份獨立的引擎 → 在粗網格上清碎島（比放大之後便宜得多）→ 每個粗格分類地形
/// （<see cref="PlanetMaterializer"/>）→ 依原本的 Goldberg 幾何產生全部地塊、從粗格取樣
/// （<see cref="SimulatedTerrainSampler"/>）→ 寫進暫存表、一次切換。
///
/// 地塊的幾何（TileId、FaceIndex/Row/Col、經緯度）跟程序化生成的完全相同，只有地形欄位換掉——
/// 前端、鄰居推導、玩家存的位置全部不受影響。
///
/// 跟 <see cref="PlanetRegenerationService"/> 互斥：兩者都會整批改寫地塊。
/// </summary>
public class PlanetMaterializationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SimulationService _simulation;
    private readonly PlanetMetadataService _metadataService;
    private readonly OverviewTextureGenerator _overviewTextureGenerator;
    private readonly SeedingStatus _seedingStatus;
    private readonly ILogger<PlanetMaterializationService> _logger;
    private readonly Lock _lock = new();

    /// <summary>建立服務。</summary>
    public PlanetMaterializationService(
        IServiceScopeFactory scopeFactory,
        SimulationService simulation,
        PlanetMetadataService metadataService,
        OverviewTextureGenerator overviewTextureGenerator,
        SeedingStatus seedingStatus,
        ILogger<PlanetMaterializationService> logger)
    {
        _scopeFactory = scopeFactory;
        _simulation = simulation;
        _metadataService = metadataService;
        _overviewTextureGenerator = overviewTextureGenerator;
        _seedingStatus = seedingStatus;
        _logger = logger;
    }

    /// <summary>目前（或最近一次）放大作業的狀態。</summary>
    public MaterializationStatus Status { get; private set; } = new();

    /// <summary>
    /// 在背景開始放大，立刻回傳。已經有放大或重新生成在進行時回傳 false。
    /// </summary>
    /// <param name="snapshotName">模擬存檔名稱。</param>
    public bool TryStart(string snapshotName)
    {
        lock (_lock)
        {
            if (Status.IsRunning || _seedingStatus.IsSeeding) return false;
            Status = new MaterializationStatus { IsRunning = true, SnapshotName = snapshotName, Phase = "讀取存檔" };
        }

        _ = RunAsync(snapshotName);
        return true;
    }

    private async Task RunAsync(string snapshotName)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var (grid, engine) = await _simulation.LoadDetachedAsync(snapshotName);
            var hash = SimulationDiagnostics.StateHash(engine).ToString("x16");

            Update(s => s with { Phase = "清除碎島" });
            var cleanup = SmallIslandCleanup.Apply(grid, engine.State);

            // 12 個月的氣候與水文（水文地貌規劃 H6）：河寬、水情、每月的積雪結冰都存進粗格。碎島清除之後才算，跟寫進去的海陸一致
            Update(s => s with { Phase = "解四季" });
            var seasons = engine.Hydrology is null ? ((SeasonalClimate, SeasonalHydrologyResult)?)null : engine.SolveSeasons(12);

            Update(s => s with { Phase = "分類粗格" });
            var cells = PlanetMaterializer.BuildCells(grid, engine.State, engine.Hydrology, seasons);
            var sampler = new SimulatedTerrainSampler(grid, cells) { TileRadiusMeters = GoldbergPlanetGenerator.TargetHexagonEdgeLengthMeters };
            var generator = new GoldbergPlanetGenerator(GoldbergPlanetGenerator.SubdivisionFrequency, sampler);

            Update(s => s with { Phase = "寫入地塊", TotalTiles = generator.TotalTileCount, SunkIslandCells = cleanup.SunkCells });
            _logger.LogInformation(
                "開始把存檔 {Snapshot}（第 {Step} 步）放大成 {Total:N0} 個地塊，碎島清除沉了 {Sunk} 格。",
                snapshotName, engine.State.StepCount, generator.TotalTileCount, cleanup.SunkCells);

            using (var scope = _scopeFactory.CreateScope())
            {
                var tiles = scope.ServiceProvider.GetRequiredService<ITileRepository>();
                await tiles.ReplaceAllWithSimulationAsync(
                    generator.GenerateTiles(), cells,
                    written => Update(s => s with { TilesWritten = written }));
            }

            Update(s => s with { Phase = "記錄來源", TilesWritten = generator.TotalTileCount });
            await _metadataService.RecordAsync(
                generator, TerrainGenerationOptions.Default,
                simulation: new SimulationSource(
                    grid.Frequency, snapshotName, engine.State.StepCount, engine.State.ElapsedYears, hash));
            _overviewTextureGenerator.Invalidate();

            Update(s => s with { IsRunning = false, Phase = "完成", ElapsedSeconds = stopwatch.Elapsed.TotalSeconds });
            _logger.LogInformation("放大完成，花了 {Seconds:F0} 秒。", stopwatch.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            Update(s => s with { IsRunning = false, Error = ex.Message, ElapsedSeconds = stopwatch.Elapsed.TotalSeconds });
            _logger.LogError(ex, "放大存檔 {Snapshot} 失敗。舊的地塊沒有被動到（暫存表還沒切換）。", snapshotName);
        }
    }

    private void Update(Func<MaterializationStatus, MaterializationStatus> change)
    {
        lock (_lock)
        {
            Status = change(Status);
        }
    }
}

/// <summary>放大作業的狀態。</summary>
public sealed record MaterializationStatus
{
    /// <summary>是否正在進行。</summary>
    public bool IsRunning { get; init; }

    /// <summary>來源存檔名稱。</summary>
    public string? SnapshotName { get; init; }

    /// <summary>目前在哪個步驟。</summary>
    public string? Phase { get; init; }

    /// <summary>已寫入的地塊數。</summary>
    public long TilesWritten { get; init; }

    /// <summary>地塊總數。</summary>
    public long TotalTiles { get; init; }

    /// <summary>碎島清除沉掉的粗格數。</summary>
    public int SunkIslandCells { get; init; }

    /// <summary>花了幾秒（完成或失敗時才有）。</summary>
    public double ElapsedSeconds { get; init; }

    /// <summary>失敗的原因；成功或還在進行時是 null。</summary>
    public string? Error { get; init; }
}
