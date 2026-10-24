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
    /// <summary>一次最多要幾個地塊的小格（約 1,500 個地塊＝半徑 20 公里、兩萬多個小格）。</summary>
    public const int MaxParentsPerRequest = 1_500;

    /// <summary>一次最多要幾個粗格的四季。</summary>
    public const int MaxCellsPerRequest = 5_000;

    /// <summary>離河中心線這麼遠以內（公尺，加上半個河寬）才把河的資訊送給前端；再遠的小格不會因為季節變成水。</summary>
    private const double RiverReportMeters = 3_000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PlanetMetadataService _metadata;
    private readonly SubTileGrid _grid = new(GoldbergPlanetGenerator.SubdivisionFrequency);

    /// <summary>建立服務。</summary>
    public SubTileService(IServiceScopeFactory scopeFactory, PlanetMetadataService metadata)
    {
        _scopeFactory = scopeFactory;
        _metadata = metadata;
    }

    /// <summary>這些地塊底下的小格（含彼此的鄰居）。</summary>
    /// <param name="parentIds">地塊編號。</param>
    /// <param name="ct">取消權杖。</param>
    public async Task<IReadOnlyList<SubTileDto>> GetSubTilesAsync(IReadOnlyCollection<long> parentIds, CancellationToken ct)
    {
        IReadOnlyList<Tile> parents;
        using (var scope = _scopeFactory.CreateScope())
        {
            parents = await scope.ServiceProvider.GetRequiredService<ITileRepository>().GetByIdsAsync(parentIds, ct);
        }
        var source = await _metadata.GetAppliedTerrainSourceAsync(ct);
        var (subs, neighbors) = _grid.ChildrenOf(parents);
        var parentById = parents.ToDictionary(t => t.TileId);
        var result = new SubTileDto[subs.Count];

        Parallel.For(0, subs.Count, new ParallelOptions { CancellationToken = ct }, i =>
        {
            var sub = subs[i];
            var latitude = Math.Round(sub.Latitude, 6);
            var longitude = Math.Round(sub.Longitude, 6);
            if (source is SimulatedTerrainSampler simulated)
            {
                var sample = simulated.SampleDetailed(sub.Latitude, sub.Longitude);
                var (level, water) = SimulatedTerrainSampler.Levels(sample);
                var nearRiver = sample.RiverCellId >= 0 && sample.RiverDistanceMeters <= RiverReportMeters + sample.RiverWidthMeters / 2;
                result[i] = new SubTileDto(
                    sub.SubTileId, sub.ParentTileId, sub.Shape.ToString(), latitude, longitude,
                    sample.Terrain.ToString(), sample.HeightCategory, level, water ?? 0, neighbors[i],
                    sample.SourceCellId,
                    nearRiver ? sample.RiverCellId : null,
                    nearRiver ? (float)sample.RiverDistanceMeters : 0f,
                    nearRiver ? (float)sample.RiverWidthMeters : 0f,
                    sample.Floodplain);
            }
            else
            {
                // 程序化的星球沒有比一格更細的資料：小格照抄地塊
                var parent = parentById[sub.ParentTileId];
                result[i] = new SubTileDto(
                    sub.SubTileId, sub.ParentTileId, sub.Shape.ToString(), latitude, longitude,
                    parent.Terrain.ToString(), parent.HeightCategory, parent.ElevationLevel, parent.SeaLevel, neighbors[i]);
            }
        });
        return result;
    }

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
