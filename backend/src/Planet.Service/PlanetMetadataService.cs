using System.Text.Json;
using Planet.Data;
using Planet.Domain;
using Planet.Generation;

namespace Planet.Service;

/// <summary>
/// 「目前這顆星球實際上是什麼」的單一權威來源（Singleton，內含記憶體快取）。
///
/// 刻意跟 <see cref="TerrainOptionsStore"/> 分開，兩者代表完全不同的東西：
///   * <see cref="TerrainOptionsStore"/> ＝ **下次**重新生成要用的參數（可隨時改，改了不影響現況）
///   * 這裡的 AppliedOptions ＝ **實際產生出資料庫裡那批地塊**的參數（只在生成完成時更新）
///
/// 混用這兩者正是「星球總覽顯示一顆不存在的星球」的成因：總覽貼圖是即時取樣算出來的，
/// 之前用的是「下次要用的參數」，所以改了參數但還沒重新生成時，
/// 總覽畫的地形跟走動視角看到的（來自資料庫）根本是兩顆不同的星球。
/// </summary>
public class PlanetMetadataService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PlanetMetadataService> _logger;
    private readonly Lock _lock = new();
    private PlanetMetadata? _cached;

    /// <summary>建立星球記錄服務。</summary>
    public PlanetMetadataService(IServiceScopeFactory scopeFactory, ILogger<PlanetMetadataService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>讀取星球記錄（有快取）。資料庫還沒有星球時回傳 null。</summary>
    public async Task<PlanetMetadata?> GetAsync(CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;

        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPlanetMetadataRepository>();
        var metadata = await repository.GetAsync(ct);

        lock (_lock)
        {
            _cached ??= metadata;
            return _cached;
        }
    }

    /// <summary>
    /// 取得「實際產生目前這批地塊」的地形參數。沒有記錄時退回
    /// <see cref="TerrainGenerationOptions.Default"/>——那也是唯一合理的猜測。
    /// </summary>
    public async Task<TerrainGenerationOptions> GetAppliedTerrainOptionsAsync(CancellationToken ct = default)
    {
        var metadata = await GetAsync(ct);
        if (metadata is null || string.IsNullOrWhiteSpace(metadata.TerrainOptionsJson))
        {
            return TerrainGenerationOptions.Default;
        }

        try
        {
            return JsonSerializer.Deserialize<TerrainGenerationOptions>(metadata.TerrainOptionsJson)
                   ?? TerrainGenerationOptions.Default;
        }
        catch (JsonException ex)
        {
            // 記錄壞掉（例如手動改過DB）時退回預設值並留下紀錄，不要讓整個總覽功能掛掉。
            _logger.LogWarning(ex, "星球記錄裡的地形參數JSON無法解析，改用預設參數。");
            return TerrainGenerationOptions.Default;
        }
    }

    /// <summary>星球生成完成後記錄既成事實。</summary>
    /// <param name="generator">實際用來生成的產生器（幾何尺寸從它身上取得）。</param>
    /// <param name="options">實際使用的地形參數。</param>
    /// <param name="isInferred">true 代表這是對既有資料的推測，而非剛剛真的生成過。</param>
    /// <param name="ct">取消權杖。</param>
    public async Task RecordAsync(
        GoldbergPlanetGenerator generator,
        TerrainGenerationOptions options,
        bool isInferred = false,
        CancellationToken ct = default)
    {
        var metadata = new PlanetMetadata
        {
            SubdivisionFrequency = GoldbergPlanetGenerator.SubdivisionFrequency,
            RadiusMeters = generator.PlanetRadiusMeters,
            TileEdgeLengthMeters = GoldbergPlanetGenerator.TargetHexagonEdgeLengthMeters,
            TotalTileCount = generator.TotalTileCount,
            TerrainOptionsJson = JsonSerializer.Serialize(options),
            GeneratedAtUtc = DateTime.UtcNow,
            IsInferred = isInferred,
        };

        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPlanetMetadataRepository>();
        await repository.UpsertAsync(metadata, ct);

        lock (_lock)
        {
            _cached = metadata;
        }
    }
}
