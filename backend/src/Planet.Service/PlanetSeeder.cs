using Planet.Data;
using Planet.Generation;

namespace Planet.Service;

/// <summary>
/// 服務啟動時檢查資料庫是否已有地圖資料，沒有的話才透過 <see cref="PlanetRegenerationService"/>
/// 在背景生成整個星球（用目前 <see cref="TerrainOptionsStore"/> 裡的參數）。
/// IHostedService本身是Singleton，ITileRepository是Scoped（依賴Scoped的DbContext），
/// 所以不能直接建構子注入，要透過IServiceScopeFactory自己開一個scope。
///
/// ## ⚠️ 連不上資料庫時要重試，不可以讓例外往外拋
///
/// 這裡踩過一次坑（2026-09-06）：原本 <see cref="StartAsync"/> 直接 await 資料庫查詢，
/// 連不上就丟出未處理的例外、整個行程結束。而 NSSM 的設定是
/// <c>AppExit Default = Restart</c>、<c>AppThrottle = 1500</c>——**程式一結束就在1.5秒後重開**，
/// 於是變成「啟動→連不上→崩潰→重開」的無窮迴圈，`service-stderr.log` 累積了20次同樣的失敗，
/// 而且服務會卡在「一直重複關開」、連要停下來移除都得跟每1.5秒重生一次的行程搶。
///
/// 這不是假想的情境：這台機器上 SQL Server 與 PlanetService 都是開機自動啟動，
/// **PlanetService 很可能比 SQL Server 先起來**，所以每次重開機都會遇到一次。
///
/// 因此改成：<see cref="StartAsync"/> 立刻回傳（不擋住主機啟動，Kestrel 照常開始聽），
/// 真正的檢查放到背景重試，直到成功或服務關閉為止。這樣資料庫晚一點才起來也能自己接上，
/// 不需要人工重啟服務。等待期間 <c>GET /api/status</c> 會回報還沒 ready 以及最後一次的錯誤訊息。
/// </summary>
public class PlanetSeeder : IHostedService
{
    /// <summary>第一次重試前等多久。</summary>
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 重試間隔的上限。每次失敗把間隔加倍到這個值為止——
    /// 開機時 SQL Server 通常一兩分鐘內就會起來，用指數退避可以在「早點接上」與
    /// 「資料庫真的掛了時不要洗版 log」之間取得平衡。
    /// </summary>
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PlanetRegenerationService _regenerationService;
    private readonly TerrainOptionsStore _optionsStore;
    private readonly SeedingStatus _status;
    private readonly PlanetMetadataService _metadataService;
    private readonly ILogger<PlanetSeeder> _logger;

    /// <summary>背景重試迴圈的取消來源，服務關閉時會取消。</summary>
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>背景重試的工作，<see cref="StopAsync"/> 要等它收尾。</summary>
    private Task? _seedingLoop;

    /// <summary>建立星球資料播種服務。</summary>
    public PlanetSeeder(
        IServiceScopeFactory scopeFactory,
        PlanetRegenerationService regenerationService,
        TerrainOptionsStore optionsStore,
        SeedingStatus status,
        PlanetMetadataService metadataService,
        ILogger<PlanetSeeder> logger)
    {
        _scopeFactory = scopeFactory;
        _regenerationService = regenerationService;
        _optionsStore = optionsStore;
        _status = status;
        _metadataService = metadataService;
        _logger = logger;
    }

    /// <summary>
    /// 服務啟動時呼叫。**立刻回傳**，實際的檢查與生成在背景進行（見類別說明）。
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _seedingLoop = Task.Run(() => RunUntilSeededAsync(_shutdown.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 一直重試到成功為止。連不上資料庫是**可以復原的**（等它起來就好），所以不放棄；
    /// 但每次失敗都會記錄下來，並寫進 <see cref="SeedingStatus.ErrorMessage"/> 讓前端看得到原因。
    /// </summary>
    private async Task RunUntilSeededAsync(CancellationToken ct)
    {
        var delay = InitialRetryDelay;
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            attempt++;
            try
            {
                await SeedAsync(ct);
                _status.ErrorMessage = null;
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // 服務正在關閉，不是錯誤
            }
            catch (Exception ex)
            {
                _status.ErrorMessage = $"連線資料庫失敗（第{attempt}次），{delay.TotalSeconds:F0}秒後重試：{ex.Message}";
                _logger.LogWarning(ex,
                    "星球資料檢查失敗（第{Attempt}次），{Delay}秒後重試。資料庫還沒起來的話這是正常的。",
                    attempt, delay.TotalSeconds);
            }

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // 指數退避，到上限為止
            var next = delay.TotalSeconds * 2;
            delay = TimeSpan.FromSeconds(Math.Min(next, MaxRetryDelay.TotalSeconds));
        }
    }

    /// <summary>資料庫已有資料就略過，否則在背景開始生成整個星球。</summary>
    private async Task SeedAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var tileRepository = scope.ServiceProvider.GetRequiredService<ITileRepository>();

        if (await tileRepository.HasAnyTilesAsync(ct))
        {
            _logger.LogInformation("地圖資料已存在，略過生成。");
            _status.IsReady = true;

            // 地塊存在但沒有對應的星球記錄，代表這批資料是在「記錄星球參數」這個功能之前生成的。
            // 依預設參數補一筆並標記成推測值（IsInferred），讓總覽貼圖與前端至少有一份可用的事實；
            // 下次重新生成時就會被真實值覆蓋。
            if (await _metadataService.GetAsync(ct) is null)
            {
                var generator = new GoldbergPlanetGenerator(
                    GoldbergPlanetGenerator.SubdivisionFrequency,
                    TerrainGenerationOptions.Default);
                await _metadataService.RecordAsync(
                    generator, TerrainGenerationOptions.Default, isInferred: true, ct: ct);
                _logger.LogInformation("既有地圖資料沒有星球記錄，已依預設參數補寫一筆（標記為推測值）。");
            }
            return;
        }

        _logger.LogInformation("資料庫無地圖資料，開始在背景生成星球...");
        // clearExisting=false：已經確認過表是空的，不需要再TRUNCATE一次。
        _regenerationService.StartInBackground(_optionsStore.Current, clearExisting: false);
    }

    /// <summary>
    /// 服務關閉時呼叫：取消背景的重試迴圈。
    /// 生成作業本身一旦開始就會做完（無法安全中途取消寫入一半的資料），所以只停重試。
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _shutdown.CancelAsync();
        if (_seedingLoop is not null)
        {
            // 這裡的等待不能無限期：關不掉的話 NSSM 會在逾時後強制砍掉行程
            await _seedingLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        _shutdown.Dispose();
    }
}
