using Planet.Shared;
using Planet.Simulation;

namespace Planet.Service;

/// <summary>
/// 模擬粗格的四季資料（水文地貌規劃 H8）：12 個月的氣溫、降水、流量比例，與積雪、結冰的月份。
/// 從 <see cref="SubTileService"/> 拆出來（2026-11-14）：它跟小格無關，只是剛好也是近看時要的資料。
/// </summary>
public sealed class CellSeasonsService
{
    /// <summary>一次最多要幾個粗格的四季。</summary>
    public const int MaxCellsPerRequest = 5_000;

    /// <summary>星球記錄服務，提供地形來源。</summary>
    private readonly PlanetMetadataService _metadata;

    /// <summary>建立服務。</summary>
    /// <param name="metadata">星球記錄服務。</param>
    public CellSeasonsService(PlanetMetadataService metadata) => _metadata = metadata;

    /// <summary>這些粗格的四季（程序化的星球沒有，回傳空的；不存在的編號略過）。</summary>
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
