using Planet.Data;
using Planet.Domain;
using Planet.Generation;
using Planet.Shared;

namespace Planet.Service;

/// <summary>
/// 近看細分（水文地貌規劃 H7）。小格只在玩家附近即時產生、不存資料庫。這個類別只負責把流程串起來：
/// <list type="number">
/// <item>查出地塊與玩家在這些地塊上的修改（資料庫）。</item>
/// <item>幾何：有哪些小格、彼此的鄰居（<see cref="SubTileGeometryCache"/>）。</item>
/// <item>原本的地表（<see cref="SubTileSurfaceSampler"/>）。</item>
/// <item>河道從水源往低處挖（<see cref="RiverChannelShaping"/>），再疊上玩家的地形修改（<see cref="ConstructionRules"/>）。</item>
/// <item>轉成給前端的 DTO。</item>
/// </list>
/// </summary>
public sealed class SubTileService
{
    /// <summary>一次最多要幾個地塊的小格（約 2,500 個地塊＝半徑 40 公里、四萬個小格；前端細分半徑 36 公里約 1,900 個）。</summary>
    public const int MaxParentsPerRequest = 2_500;

    /// <summary>經緯度送給前端時留幾位小數（1e-6 度約 3 公分，對 250 公尺的小格綽綽有餘）。</summary>
    private const int CoordinateDecimals = 6;

    /// <summary>用來建立 scoped 的資料庫存取物件（本服務是 Singleton）。</summary>
    private readonly IServiceScopeFactory _scopeFactory;
    /// <summary>星球記錄服務，提供地形來源。</summary>
    private readonly PlanetMetadataService _metadata;
    /// <summary>小格幾何（含快取）。</summary>
    private readonly SubTileGeometryCache _geometry;

    /// <summary>建立服務。</summary>
    /// <param name="scopeFactory">建立 scoped 資料庫存取物件用。</param>
    /// <param name="metadata">星球記錄服務。</param>
    /// <param name="geometry">小格幾何。</param>
    public SubTileService(IServiceScopeFactory scopeFactory, PlanetMetadataService metadata, SubTileGeometryCache geometry)
    {
        _scopeFactory = scopeFactory;
        _metadata = metadata;
        _geometry = geometry;
    }

    /// <summary>
    /// 這些地塊底下的小格（含彼此的鄰居），已經疊上玩家的修改（編輯模式）：
    /// 地形修改直接改掉地表欄位，建築物放在 <see cref="SubTileDto.Structure"/>。
    /// 找不到的地塊編號略過（不算錯誤：前端的地塊清單可能比資料庫舊）。
    /// </summary>
    /// <param name="parentIds">地塊編號。</param>
    /// <param name="ct">取消權杖。</param>
    public async Task<IReadOnlyList<SubTileDto>> GetSubTilesAsync(IReadOnlyCollection<long> parentIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parentIds);
        var (parents, constructions) = await LoadAsync(parentIds, ct);
        if (parents.Count == 0) return [];

        var batch = _geometry.Build(parents, ct);
        var raw = SubTileSurfaceSampler.Sample(await _metadata.GetAppliedTerrainSourceAsync(ct), batch.SubTiles, parents, ct);
        var (surfaces, edits) = ApplyOverlays(batch, raw, constructions);

        var result = new SubTileDto[batch.SubTiles.Count];
        for (var i = 0; i < result.Length; i++)
        {
            edits.TryGetValue(i, out var construction);
            result[i] = ToDto(batch.SubTiles[i], batch.NeighborIds[i], surfaces[i], construction);
        }
        return result;
    }

    /// <summary>
    /// 一個小格原本（沒有玩家修改、還沒挖河道）的地表，給編輯規則判斷能不能蓋；找不到地塊、或小格不屬於這個地塊時回傳 null。
    /// </summary>
    /// <param name="parentTileId">地塊編號。</param>
    /// <param name="subTileId">小格編號。</param>
    /// <param name="ct">取消權杖。</param>
    public async Task<SubTileSurface?> GetRawSurfaceAsync(long parentTileId, long subTileId, CancellationToken ct)
    {
        IReadOnlyList<Tile> parents;
        using (var scope = _scopeFactory.CreateScope())
        {
            parents = await scope.ServiceProvider.GetRequiredService<ITileRepository>().GetByIdsAsync([parentTileId], ct);
        }
        if (parents.Count == 0) return null;

        var batch = _geometry.Build(parents, ct);
        if (!batch.IndexById.TryGetValue(subTileId, out var index)) return null;
        // 只取樣要的那一格
        return SubTileSurfaceSampler.Sample(await _metadata.GetAppliedTerrainSourceAsync(ct), [batch.SubTiles[index]], parents, ct)[0];
    }

    /// <summary>查出地塊，以及玩家在這些地塊上的修改。</summary>
    /// <param name="parentIds">地塊編號。</param>
    /// <param name="ct">取消權杖。</param>
    private async Task<(IReadOnlyList<Tile> Parents, IReadOnlyList<Construction> Constructions)> LoadAsync(
        IReadOnlyCollection<long> parentIds, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var parents = await scope.ServiceProvider.GetRequiredService<ITileRepository>().GetByIdsAsync(parentIds, ct);
        if (parents.Count == 0) return (parents, []);
        var constructions = await scope.ServiceProvider.GetRequiredService<IConstructionRepository>()
            .GetByParentsAsync(parents.Select(p => p.TileId).ToList(), ct);
        return (parents, constructions);
    }

    /// <summary>
    /// 在原本的地表上依序疊兩層：河道從水源往低處挖（不高過兩岸、不往高處流），再疊玩家的地形修改。
    /// </summary>
    /// <param name="batch">這一批小格的幾何。</param>
    /// <param name="raw">原本的地表。</param>
    /// <param name="constructions">玩家的修改（可能包含不在這一批的小格，略過）。</param>
    /// <returns>疊完的地表，以及有修改的小格（索引 → 修改）。</returns>
    private static (SubTileSurface[] Surfaces, Dictionary<int, Construction> Edits) ApplyOverlays(
        SubTileBatch batch, SubTileSurface[] raw, IReadOnlyList<Construction> constructions)
    {
        var shaped = RiverChannelShaping.Shape(raw, batch.NeighborIndices);
        var surfaces = (SubTileSurface[])shaped.Clone();
        var edits = new Dictionary<int, Construction>(constructions.Count);
        foreach (var construction in constructions)
        {
            if (!batch.IndexById.TryGetValue(construction.SubTileId, out var i)) continue;
            edits[i] = construction;
            if (construction.TerrainEdit is { } edit)
                surfaces[i] = ConstructionRules.Apply(shaped[i], edit, LandNeighbourLevel(batch.NeighborIndices[i], raw));
        }
        return (surfaces, edits);
    }

    /// <summary>相鄰陸地小格（同一批、原本的地表）的平均海拔等級；沒有相鄰陸地時是 null。</summary>
    /// <param name="neighbours">鄰居在這一批裡的索引。</param>
    /// <param name="raw">原本的地表。</param>
    private static int? LandNeighbourLevel(IReadOnlyList<int> neighbours, SubTileSurface[] raw)
    {
        var sum = 0;
        var count = 0;
        foreach (var j in neighbours)
        {
            if (ConstructionRules.IsWater(raw[j])) continue;
            sum += raw[j].ElevationLevel;
            count++;
        }
        return count == 0 ? null : (int)Math.Round((double)sum / count);
    }

    /// <summary>把小格、鄰居、地表狀態與玩家修改組成 API 回應的 DTO。</summary>
    /// <param name="sub">小格幾何。</param>
    /// <param name="neighbors">鄰居小格編號（不在這批的為 <see langword="null"/>）。</param>
    /// <param name="surface">疊完河道與玩家修改之後的地表。</param>
    /// <param name="construction">玩家在這一格的修改；沒有時為 <see langword="null"/>。</param>
    /// <returns>小格 DTO。</returns>
    private static SubTileDto ToDto(SubTile sub, long?[] neighbors, SubTileSurface surface, Construction? construction) =>
        new(
            sub.SubTileId, sub.ParentTileId, sub.Shape.ToString(),
            Math.Round(sub.Latitude, CoordinateDecimals), Math.Round(sub.Longitude, CoordinateDecimals),
            surface.Terrain.ToString(), surface.HeightCategory, surface.ElevationLevel, surface.SeaLevel, neighbors,
            surface.SourceCellId,
            surface.RiverCellId,
            surface.RiverDistanceMeters,
            surface.RiverWidthMeters,
            surface.Floodplain,
            construction?.Structure?.ToString(),
            construction?.TerrainEdit?.ToString());
}
