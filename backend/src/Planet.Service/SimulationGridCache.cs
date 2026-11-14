using Planet.Simulation;

namespace Planet.Service;

/// <summary>
/// 模擬粗網格的共用快取（2026-11-14）。網格建好之後不會再變（鄰接表、取樣用的桶都是唯讀或執行緒安全的），
/// 而地形來源（<see cref="PlanetMetadataService"/>）與線上的模擬（<see cref="SimulationService"/>）要的是同一顆星球、
/// 同一個頻率的網格——原本各建一份，n=150 一份要一到四秒，記憶體也是兩份。
/// </summary>
public sealed class SimulationGridCache
{
    /// <summary>保護 <see cref="_grids"/>；建網格也在鎖裡，同一個網格不會被兩個請求同時建。</summary>
    private readonly Lock _lock = new();

    /// <summary>（半徑、頻率）→ 網格。實務上只會有一兩個：星球半徑固定，頻率只有放大時用的那一個與預設值。</summary>
    private readonly Dictionary<(double RadiusMeters, int Frequency), SimulationGrid> _grids = [];

    /// <summary>取得（沒有就建）這顆星球在這個頻率下的網格。</summary>
    /// <param name="planetRadiusMeters">星球半徑（公尺）。</param>
    /// <param name="frequency">細分頻率，預設 <see cref="SimulationGrid.DefaultFrequency"/>。</param>
    public SimulationGrid Get(double planetRadiusMeters, int frequency = SimulationGrid.DefaultFrequency)
    {
        lock (_lock)
        {
            var key = (planetRadiusMeters, frequency);
            if (!_grids.TryGetValue(key, out var grid))
            {
                grid = SimulationGrid.Build(planetRadiusMeters, frequency);
                _grids[key] = grid;
            }
            return grid;
        }
    }
}
