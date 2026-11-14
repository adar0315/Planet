using Planet.Domain;
using Planet.Generation;
using Planet.Simulation;

namespace Planet.Service;

/// <summary>
/// 小格「原本的地表」：還沒挖河道、還沒疊上玩家修改的樣子。
/// 模擬放大來的星球向 <see cref="SimulatedTerrainSampler"/> 取樣（跟放大成地塊時同一支，所以小格拼起來跟地塊、總覽貼圖一致）；
/// 程序化的星球沒有比一格更細的資料，小格照抄地塊。
/// </summary>
public static class SubTileSurfaceSampler
{
    /// <summary>離河中心線這麼遠以內（公尺，加上半個河寬）才把河的資訊送給前端；再遠的小格不會因為季節變成水。</summary>
    private const double RiverReportMeters = 3_000;

    /// <summary>取樣一批小格。</summary>
    /// <param name="source">目前這顆星球的地形來源。</param>
    /// <param name="subTiles">小格。</param>
    /// <param name="parents">小格所屬的地塊（程序化的星球要照抄地塊）。</param>
    /// <param name="ct">取消權杖。</param>
    /// <returns>每個小格的地表，順序同 <paramref name="subTiles"/>。</returns>
    public static SubTileSurface[] Sample(
        ITerrainGenerator source, IReadOnlyList<SubTile> subTiles, IReadOnlyList<Tile> parents, CancellationToken ct = default)
    {
        var surfaces = new SubTileSurface[subTiles.Count];
        if (source is SimulatedTerrainSampler simulated)
        {
            // 取樣是唯讀的，可以平行
            Parallel.For(0, subTiles.Count, new ParallelOptions { CancellationToken = ct },
                i => surfaces[i] = FromSample(simulated.SampleDetailed(subTiles[i].Latitude, subTiles[i].Longitude)));
            return surfaces;
        }

        var parentById = new Dictionary<long, Tile>(parents.Count);
        foreach (var parent in parents) parentById[parent.TileId] = parent;
        for (var i = 0; i < subTiles.Count; i++)
        {
            var parent = parentById[subTiles[i].ParentTileId];
            surfaces[i] = new SubTileSurface(
                parent.Terrain, parent.HeightCategory, parent.ElevationLevel, parent.SeaLevel, null, 0f, 0f, false);
        }
        return surfaces;
    }

    /// <summary>把取樣結果換成小格的地表欄位（高度換成等級；離河太遠的不帶河的資訊）。</summary>
    /// <param name="sample">取樣結果。</param>
    private static SubTileSurface FromSample(LandformSample sample)
    {
        var (level, water) = SimulatedTerrainSampler.Levels(sample);
        var nearRiver = sample.RiverCellId >= 0
                        && sample.RiverDistanceMeters <= RiverReportMeters + sample.RiverWidthMeters / 2;
        return new SubTileSurface(
            sample.Terrain, sample.HeightCategory, level, water ?? 0,
            nearRiver ? sample.RiverCellId : null,
            nearRiver ? (float)sample.RiverDistanceMeters : 0f,
            nearRiver ? (float)sample.RiverWidthMeters : 0f,
            sample.Floodplain,
            sample.SourceCellId);
    }
}
