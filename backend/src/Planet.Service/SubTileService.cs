using Planet.Data;
using Planet.Domain;
using Planet.Generation;
using Planet.Shared;
using Planet.Simulation;

namespace Planet.Service;

/// <summary>
/// 近看細分（水文地貌規劃 H7）與四季資料（H8）。小格只在玩家附近即時產生、不存資料庫：
/// 幾何由 <see cref="SubTileGrid"/>（跟地塊同一套鄰居推導），地貌由放大時同一支 <see cref="SimulatedTerrainSampler"/> 取樣，
/// 所以小格拼起來跟地塊、總覽貼圖都一致。
/// </summary>
public sealed class SubTileService
{
    /// <summary>一次最多要幾個地塊的小格（約 2,500 個地塊＝半徑 40 公里、四萬個小格；前端細分半徑 36 公里約 1,900 個）。</summary>
    public const int MaxParentsPerRequest = 2_500;

    /// <summary>一次最多要幾個粗格的四季。</summary>
    public const int MaxCellsPerRequest = 5_000;

    /// <summary>離河中心線這麼遠以內（公尺，加上半個河寬）才把河的資訊送給前端；再遠的小格不會因為季節變成水。</summary>
    private const double RiverReportMeters = 3_000;

    /// <summary>用來建立 scoped 的資料庫存取物件（本服務是 Singleton）。</summary>
    private readonly IServiceScopeFactory _scopeFactory;
    /// <summary>星球記錄服務，提供地形來源。</summary>
    private readonly PlanetMetadataService _metadata;
    /// <summary>正式細分頻率下的小格網格。</summary>
    private readonly SubTileGrid _grid = new(GoldbergPlanetGenerator.SubdivisionFrequency);

    /// <summary>建立服務。</summary>
    public SubTileService(IServiceScopeFactory scopeFactory, PlanetMetadataService metadata)
    {
        _scopeFactory = scopeFactory;
        _metadata = metadata;
    }

    /// <summary>
    /// 這些地塊底下的小格（含彼此的鄰居），已經疊上玩家的修改（編輯模式）：
    /// 地形修改直接改掉地表欄位，建築物放在 <see cref="SubTileDto.Structure"/>。
    /// </summary>
    /// <param name="parentIds">地塊編號。</param>
    /// <param name="ct">取消權杖。</param>
    public async Task<IReadOnlyList<SubTileDto>> GetSubTilesAsync(IReadOnlyCollection<long> parentIds, CancellationToken ct)
    {
        IReadOnlyList<Tile> parents;
        IReadOnlyList<Construction> constructions;
        using (var scope = _scopeFactory.CreateScope())
        {
            parents = await scope.ServiceProvider.GetRequiredService<ITileRepository>().GetByIdsAsync(parentIds, ct);
            constructions = await scope.ServiceProvider.GetRequiredService<IConstructionRepository>()
                .GetByParentsAsync(parents.Select(p => p.TileId).ToList(), ct);
        }
        var (subs, neighbors, raw) = await SampleAsync(parents, ct);

        var indexById = new Dictionary<long, int>(subs.Count);
        for (var i = 0; i < subs.Count; i++) indexById[subs[i].SubTileId] = i;
        // 河道從水源往低處挖（不高過兩岸、不往高處流），玩家的地形修改再疊在挖好的河道上
        var neighbourIndices = new IReadOnlyList<int>[subs.Count];
        for (var i = 0; i < subs.Count; i++)
        {
            var list = new List<int>(6);
            foreach (var id in neighbors[i])
                if (id is { } neighbourId && indexById.TryGetValue(neighbourId, out var j)) list.Add(j);
            neighbourIndices[i] = list;
        }
        var shaped = RiverChannelShaping.Shape(raw, neighbourIndices);
        var surfaces = shaped.ToArray();
        var edits = new Dictionary<int, Construction>();
        foreach (var construction in constructions)
        {
            if (!indexById.TryGetValue(construction.SubTileId, out var i)) continue;
            edits[i] = construction;
            if (construction.TerrainEdit is { } edit)
                surfaces[i] = ConstructionRules.Apply(shaped[i], edit, LandNeighbourLevel(i, neighbors, raw, indexById));
        }

        var result = new SubTileDto[subs.Count];
        for (var i = 0; i < subs.Count; i++)
        {
            edits.TryGetValue(i, out var construction);
            result[i] = ToDto(subs[i], neighbors[i], surfaces[i], construction);
        }
        return result;
    }

    /// <summary>
    /// 一個小格原本（沒有玩家修改）的地表，給編輯規則判斷能不能蓋；不屬於這個地塊時回傳 null。
    /// 相鄰陸地的平均高度只看同一個地塊底下的鄰居（判斷能不能蓋用不到它）。
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
        var (subs, _, raw) = await SampleAsync(parents, ct);
        for (var i = 0; i < subs.Count; i++)
            if (subs[i].SubTileId == subTileId) return raw[i];
        return null;
    }

    /// <summary>小格的幾何與原本的地表（模擬取樣，或程序化星球照抄地塊）。</summary>
    private async Task<(IReadOnlyList<SubTile> Subs, IReadOnlyList<long?[]> Neighbors, SubTileSurface[] Raw)> SampleAsync(
        IReadOnlyList<Tile> parents, CancellationToken ct)
    {
        var source = await _metadata.GetAppliedTerrainSourceAsync(ct);
        var (subs, neighbors) = _grid.ChildrenOf(parents);
        var parentById = parents.ToDictionary(t => t.TileId);
        var raw = new SubTileSurface[subs.Count];
        Parallel.For(0, subs.Count, new ParallelOptions { CancellationToken = ct }, i =>
        {
            var sub = subs[i];
            if (source is SimulatedTerrainSampler simulated)
            {
                var sample = simulated.SampleDetailed(sub.Latitude, sub.Longitude);
                var (level, water) = SimulatedTerrainSampler.Levels(sample);
                var nearRiver = sample.RiverCellId >= 0 && sample.RiverDistanceMeters <= RiverReportMeters + sample.RiverWidthMeters / 2;
                raw[i] = new SubTileSurface(
                    sample.Terrain, sample.HeightCategory, level, water ?? 0,
                    nearRiver ? sample.RiverCellId : null,
                    nearRiver ? (float)sample.RiverDistanceMeters : 0f,
                    nearRiver ? (float)sample.RiverWidthMeters : 0f,
                    sample.Floodplain,
                    sample.SourceCellId);
            }
            else
            {
                // 程序化的星球沒有比一格更細的資料：小格照抄地塊
                var parent = parentById[sub.ParentTileId];
                raw[i] = new SubTileSurface(parent.Terrain, parent.HeightCategory, parent.ElevationLevel, parent.SeaLevel,
                    null, 0f, 0f, false);
            }
        });
        return (subs, neighbors, raw);
    }

    /// <summary>相鄰陸地小格（同一批、原本的地表）的平均海拔等級；沒有相鄰陸地時是 null。</summary>
    private static int? LandNeighbourLevel(
        int index, IReadOnlyList<long?[]> neighbors, SubTileSurface[] raw, Dictionary<long, int> indexById)
    {
        var sum = 0;
        var count = 0;
        foreach (var id in neighbors[index])
        {
            if (id is not { } neighbourId || !indexById.TryGetValue(neighbourId, out var j)) continue;
            if (ConstructionRules.IsWater(raw[j])) continue;
            sum += raw[j].ElevationLevel;
            count++;
        }
        return count == 0 ? null : (int)Math.Round((double)sum / count);
    }

    /// <summary>把小格、鄰居、地表狀態與玩家修改組成 API 回應的 DTO。</summary>
    /// <param name="sub">小格幾何。</param>
    /// <param name="neighbors">鄰居小格編號（不在這批的為 <see langword="null"/>）。</param>
    /// <param name="surface">取樣出來（並套用修改後）的地表狀態。</param>
    /// <param name="construction">玩家在這一格的修改；沒有時為 <see langword="null"/>。</param>
    /// <returns>小格 DTO。</returns>
    private static SubTileDto ToDto(SubTile sub, long?[] neighbors, SubTileSurface surface, Construction? construction) =>
        new(
            sub.SubTileId, sub.ParentTileId, sub.Shape.ToString(),
            Math.Round(sub.Latitude, 6), Math.Round(sub.Longitude, 6),
            surface.Terrain.ToString(), surface.HeightCategory, surface.ElevationLevel, surface.SeaLevel, neighbors,
            surface.SourceCellId,
            surface.RiverCellId,
            surface.RiverDistanceMeters,
            surface.RiverWidthMeters,
            surface.Floodplain,
            construction?.Structure?.ToString(),
            construction?.TerrainEdit?.ToString());

    /// <summary>這些粗格的四季（程序化的星球沒有，回傳空的）。</summary>
    /// <param name="cellIds">粗格編號。</param>
    /// <param name="ct">取消權杖。</param>
    public async Task<IReadOnlyList<CellSeasonsDto>> GetSeasonsAsync(IReadOnlyCollection<int> cellIds, CancellationToken ct)
    {
        if (await _metadata.GetAppliedTerrainSourceAsync(ct) is not SimulatedTerrainSampler simulated) return [];
        var cells = simulated.Cells;
        return cellIds
            .Where(id => id >= 0 && id < cells.Count)
            .Distinct()
            .Select(id =>
            {
                var cell = cells[id];
                return new CellSeasonsDto(
                    id,
                    PlanetMaterializer.FromBytes(cell.MonthlyTemperature),
                    PlanetMaterializer.FromBytes(cell.MonthlyPrecipitation),
                    PlanetMaterializer.FromBytes(cell.MonthlyDischargeRatio),
                    cell.SnowMonths,
                    cell.FrozenMonths,
                    cell.FlowRegime);
            })
            .ToList();
    }
}
