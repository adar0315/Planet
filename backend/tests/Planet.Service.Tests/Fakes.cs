using Microsoft.Extensions.DependencyInjection;
using Planet.Data;
using Planet.Domain;

namespace Planet.Service.Tests;

/// <summary>假的地塊倉儲：資料放在記憶體，只實作服務層測試會走到的查詢，其餘丟例外（走到了就代表測試的假設錯了）。</summary>
internal sealed class FakeTileRepository(IReadOnlyList<Tile> tiles) : ITileRepository
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
    public Task ClearAllTilesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task BulkInsertAsync(IEnumerable<Tile> tiles, Action<long>? onProgress = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task ReplaceAllWithSimulationAsync(IEnumerable<Tile> tiles, IReadOnlyList<PlanetCell> cells, Action<long>? onProgress = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task ClearPlanetCellsAsync(CancellationToken ct = default) => throw new NotSupportedException();
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

    public Task<IReadOnlyList<Construction>> GetByParentsAsync(IReadOnlyCollection<long> parentTileIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Construction>>(Items.Where(c => parentTileIds.Contains(c.ParentTileId)).ToList());

    public Task<IReadOnlyList<Construction>> GetByIdsAsync(IReadOnlyCollection<long> subTileIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Construction>>(Items.Where(c => subTileIds.Contains(c.SubTileId)).ToList());

    public Task<Construction?> GetAsync(long subTileId, CancellationToken ct = default) =>
        Task.FromResult(Items.FirstOrDefault(c => c.SubTileId == subTileId));

    public Task SaveAsync(Construction construction, CancellationToken ct = default)
    {
        Items.RemoveAll(c => c.SubTileId == construction.SubTileId);
        Items.Add(construction);
        return Task.CompletedTask;
    }
}

internal static class FakeScopes
{
    /// <summary>建一個只有這兩個假倉儲的 DI 容器，交出服務層要的 scope 工廠。</summary>
    public static IServiceScopeFactory For(ITileRepository tiles, IConstructionRepository constructions) =>
        new ServiceCollection()
            .AddScoped(_ => tiles)
            .AddScoped(_ => constructions)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
}
