using Planet.Data;
using Planet.Generation;

namespace Planet.Service;

/// <summary>
/// 「清空並重新生成整個星球」的共用邏輯。服務啟動時（資料庫是空的，見 <see cref="PlanetSeeder"/>）跟透過
/// <see cref="Controllers.RegenerationController"/> 手動觸發，都走這裡，確保兩種情境的行為
/// （背景執行、進度回報到 <see cref="SeedingStatus"/>）完全一致，不用維護兩份重複邏輯。
/// </summary>
public class PlanetRegenerationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SeedingStatus _status;
    private readonly OverviewTextureGenerator _overviewTextureGenerator;
    private readonly PlanetMetadataService _metadataService;
    private readonly ILogger<PlanetRegenerationService> _logger;

    /// <summary>建立重新生成服務。</summary>
    public PlanetRegenerationService(
        IServiceScopeFactory scopeFactory,
        SeedingStatus status,
        OverviewTextureGenerator overviewTextureGenerator,
        PlanetMetadataService metadataService,
        ILogger<PlanetRegenerationService> logger)
    {
        _scopeFactory = scopeFactory;
        _status = status;
        _overviewTextureGenerator = overviewTextureGenerator;
        _metadataService = metadataService;
        _logger = logger;
    }

    /// <summary>是否已經有生成作業正在背景進行中（避免同時觸發第二次，寫入資料庫時打架）。</summary>
    public bool IsRunning => _status.IsSeeding;

    /// <summary>
    /// 在背景開始生成，立刻回傳、不等待完成（見 <see cref="PlanetSeeder"/> 說明：不能讓呼叫端等整個生成過程，
    /// 不然HTTP請求本身就要卡15分鐘）。呼叫前應該先確認 <see cref="IsRunning"/> 是false。
    /// </summary>
    /// <param name="options">要使用的地形生成參數。</param>
    /// <param name="clearExisting">
    /// true＝先TRUNCATE現有資料再生成（手動觸發的「重新生成」用，會覆蓋掉目前的星球）；
    /// false＝直接生成，不清空（只有在確定資料庫本來就是空的情況下才能用，例如服務啟動時的自動播種）。
    /// </param>
    public void StartInBackground(TerrainGenerationOptions options, bool clearExisting)
    {
        if (_status.IsSeeding)
        {
            throw new InvalidOperationException("已經有星球生成作業正在進行中，請等它完成後再試一次。");
        }

        _ = RunAsync(options, clearExisting);
    }

    private async Task RunAsync(TerrainGenerationOptions options, bool clearExisting)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var tileRepository = scope.ServiceProvider.GetRequiredService<ITileRepository>();

            if (clearExisting)
            {
                _logger.LogInformation("清空現有地圖資料，準備重新生成...");
                await tileRepository.ClearAllTilesAsync();
            }

            var generator = new GoldbergPlanetGenerator(GoldbergPlanetGenerator.SubdivisionFrequency, options);

            _status.ErrorMessage = null;
            _status.TilesWritten = 0;
            _status.TotalTiles = generator.TotalTileCount;
            _status.IsReady = false;
            _status.IsSeeding = true;
            _logger.LogInformation(
                "開始在背景生成星球（半徑約{Radius:N0}公尺，預計{Total:N0}格，種子{Seed}）...",
                generator.PlanetRadiusMeters, _status.TotalTiles, options.Seed);

            var tiles = generator.GenerateTiles();
            await tileRepository.BulkInsertAsync(tiles, written => _status.TilesWritten = written);

            // 記錄既成事實：這批地塊到底是用哪組參數、哪種幾何尺寸產生的。
            // 必須在 Invalidate 之前寫入，否則總覽有可能搶先用舊記錄重算一次。
            await _metadataService.RecordAsync(generator, options);
            _overviewTextureGenerator.Invalidate(); // 舊的星球總覽貼圖快取跟新地形對不上了，清掉讓下次請求重算。
            _status.TilesWritten = _status.TotalTiles;
            _status.IsSeeding = false;
            _status.IsReady = true;
            _logger.LogInformation("星球生成完成。");
        }
        catch (Exception ex)
        {
            _status.IsSeeding = false;
            _status.ErrorMessage = ex.Message;
            _logger.LogError(ex, "星球生成失敗。");
        }
    }
}
