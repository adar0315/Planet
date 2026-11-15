using Microsoft.Extensions.DependencyInjection;
using Planet.Data;
using Planet.Domain;

namespace Planet.Service.Tests;

/// <summary>假的地塊倉儲：資料放在記憶體，只實作服務層測試會走到的查詢，其餘丟例外（走到了就代表測試的假設錯了）。</summary>
internal class FakeTileRepository(IReadOnlyList<Tile> tiles) : ITileRepository
{
    private readonly Dictionary<long, Tile> _byId = tiles.ToDictionary(t => t.TileId);

    /// <summary><see cref="GetTilesNearAsync"/> 收到的參數，依呼叫順序。</summary>
    public List<(double Lat, double Lon, int Count)> NearbyCalls { get; } = [];

    /// <summary>回傳全部地塊（不篩選），並記下參數。</summary>
    public Task<IReadOnlyList<Tile>> GetTilesNearAsync(double latitude, double longitude, int count, CancellationToken ct = default)
    {
        NearbyCalls.Add((latitude, longitude, count));
        return Task.FromResult(tiles);
    }

    /// <summary>跟資料庫一樣：不存在的編號略過、重複的編號只回一列。</summary>
    public Task<IReadOnlyList<Tile>> GetByIdsAsync(IReadOnlyCollection<long> tileIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Tile>>(tileIds.Distinct().Where(_byId.ContainsKey).Select(id => _byId[id]).ToList());

    public Task<bool> HasAnyTilesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public virtual Task ClearAllTilesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public virtual Task BulkInsertAsync(IEnumerable<Tile> tiles, Action<long>? onProgress = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task ReplaceAllWithSimulationAsync(IEnumerable<Tile> tiles, IReadOnlyList<PlanetCell> cells, Action<long>? onProgress = null, CancellationToken ct = default) => throw new NotSupportedException();
    public virtual Task ClearPlanetCellsAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<PlanetCell>> GetAllPlanetCellsAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<PlanetCell?> GetPlanetCellAsync(int cellId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Tile?> GetByIdAsync(long tileId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Tile?> GetByCoordinateAsync(int faceIndex, int row, int col, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<Tile>> GetByCoordinatesAsync(IReadOnlyCollection<(int FaceIndex, int Row, int Col)> coordinates, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>假的玩家修改倉儲。</summary>
internal sealed class FakeConstructionRepository : IConstructionRepository
{
    /// <summary>目前存著的修改；測試直接往裡面加。</summary>
    public List<Construction> Items { get; } = [];

    private readonly object _gate = new();

    /// <summary>
    /// 每次存取前先等這麼久，模擬真的資料庫。預設 0（同步完成）。
    /// 測並行的時候要設：同步完成的假倉儲讓每個請求一口氣跑完，請求之間根本沒有交錯的機會，
    /// 服務有沒有加鎖結果都一樣——拿掉鎖的那次破壞就是這樣沒被抓到的。
    /// </summary>
    public TimeSpan Latency { get; set; }

    private async Task Wait()
    {
        if (Latency > TimeSpan.Zero) await Task.Delay(Latency);
    }

    public async Task<IReadOnlyList<Construction>> GetByParentsAsync(IReadOnlyCollection<long> parentTileIds, CancellationToken ct = default)
    {
        await Wait();
        lock (_gate) return Items.Where(c => parentTileIds.Contains(c.ParentTileId)).ToList();
    }

    public async Task<IReadOnlyList<Construction>> GetByIdsAsync(IReadOnlyCollection<long> subTileIds, CancellationToken ct = default)
    {
        await Wait();
        lock (_gate) return Items.Where(c => subTileIds.Contains(c.SubTileId)).ToList();
    }

    public async Task<Construction?> GetAsync(long subTileId, CancellationToken ct = default)
    {
        await Wait();
        lock (_gate) return Items.FirstOrDefault(c => c.SubTileId == subTileId);
    }

    public async Task SaveAsync(Construction construction, CancellationToken ct = default)
    {
        await Wait();
        lock (_gate)
        {
            Items.RemoveAll(c => c.SubTileId == construction.SubTileId);
            Items.Add(construction);
        }
    }
}

internal static class FakeScopes
{
    /// <summary>自己決定要註冊哪些服務的 scope 工廠。</summary>
    public static IServiceScopeFactory Build(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>建一個只有這兩個假倉儲的 DI 容器，交出服務層要的 scope 工廠。</summary>
    public static IServiceScopeFactory For(ITileRepository tiles, IConstructionRepository constructions) =>
        new ServiceCollection()
            .AddScoped(_ => tiles)
            .AddScoped(_ => constructions)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
}

/// <summary>假的星球中繼資料倉儲：記下每一次寫入。</summary>
internal sealed class FakeMetadataRepository : IPlanetMetadataRepository
{
    /// <summary>依順序記下的寫入。</summary>
    public List<PlanetMetadata> Upserts { get; } = [];

    public Task<PlanetMetadata?> GetAsync(CancellationToken ct = default) =>
        Task.FromResult(Upserts.Count == 0 ? null : Upserts[^1]);

    public Task UpsertAsync(PlanetMetadata metadata, CancellationToken ct = default)
    {
        lock (Upserts) Upserts.Add(metadata);
        return Task.CompletedTask;
    }
}
