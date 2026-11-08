namespace Planet.Domain;

/// <summary>
/// 小格上的河道改成「從水源往低處挖」（2026-11-08 使用者要求：像挖河道那樣，先決定水源出口，然後往低處挖）。
///
/// 模擬取樣出來的河床高度是沿著粗格整理成一路往下游降的（LandformSampler），跟小格的兩岸無關：
/// 兩岸比河床低的地方河會高出地面，河從高地邊緣往下走的地方會變成一根長柱。
///
/// 這裡在一批小格裡重新挖：
/// <list type="number">
/// <item>水源：河床越高越上游（取樣出來的河床本來就一路往下游降），所以依原本的河床高度由高到低處理。</item>
/// <item>每一格的河面＝「原本的河床」「最低的那一岸再低 <see cref="ConstructionRules.ChannelWaterDepthLevels"/> 級」
///   「已經挖好的上游鄰格」三者取最低：河永遠不高過兩岸；岸低的地方往下挖，下游也不會再升回去（水不往高處流）。</item>
/// </list>
/// 河寬裡沒有碰到岸的格子（寬河的中間）不看岸，只受上游限制。
/// 河道的位置不變，只改高度；河的資料（哪條河、寬度、距離）留著，前端照樣依季節畫成水或乾河床。
/// </summary>
public static class RiverChannelShaping
{
    /// <summary>
    /// 這一格是不是河（有河的資料、落在畫成河的那一帶、不是海）。
    /// 取樣常把河道畫成「有水面的淡水」（河床加一級水面）：這種格子也是河，不能當成湖跳過——
    /// 2026-11-08 第一版跳過了，高地邊緣的水柱一根都沒改到（河床 162 級、旁邊 1 級）。
    /// </summary>
    /// <param name="surface">地表。</param>
    public static bool IsRiverBand(SubTileSurface surface) =>
        surface.RiverCellId is not null
        && surface.RiverWidthMeters > 0
        && surface.RiverDistanceMeters <= Math.Max(surface.RiverWidthMeters / 2, ConstructionRules.VisibleRiverHalfWidthMeters)
        && surface.Terrain is not (TerrainType.Seawater or TerrainType.CoralReef);

    /// <summary>
    /// 從水源往低處挖河道，回傳新的一份地表（不在河上的格子原樣）。
    /// </summary>
    /// <param name="surfaces">這一批小格的地表。</param>
    /// <param name="neighbours">每一格的鄰居在這一批裡的索引（批次外的鄰居不列）。</param>
    public static SubTileSurface[] Shape(IReadOnlyList<SubTileSurface> surfaces, IReadOnlyList<IReadOnlyList<int>> neighbours)
    {
        var count = surfaces.Count;
        var result = surfaces.ToArray();
        var river = new bool[count];
        var order = new List<int>();
        for (var i = 0; i < count; i++)
        {
            river[i] = IsRiverBand(surfaces[i]);
            if (river[i]) order.Add(i);
        }
        // 由上游往下游：原本的河床由高到低（同高時依編號，結果固定）
        order.Sort((a, b) => surfaces[b].ElevationLevel != surfaces[a].ElevationLevel
            ? surfaces[b].ElevationLevel.CompareTo(surfaces[a].ElevationLevel)
            : a.CompareTo(b));

        var dug = new bool[count];
        foreach (var i in order)
        {
            var level = surfaces[i].ElevationLevel;
            foreach (var j in neighbours[i])
            {
                if (river[j])
                {
                    // 已經挖好的上游：水不往高處流
                    if (dug[j]) level = Math.Min(level, result[j].ElevationLevel);
                }
                else if (!ConstructionRules.IsWater(surfaces[j]))
                {
                    // 岸：河面比最低的那一岸再低一點
                    level = Math.Min(level, surfaces[j].ElevationLevel - ConstructionRules.ChannelWaterDepthLevels);
                }
            }
            // 不低於海面（0 級）：入海口的岸就在海面上，河不能變成泡在海裡
            level = Math.Max(level, 0);
            var original = surfaces[i];
            var drop = original.ElevationLevel - level;
            result[i] = original with
            {
                ElevationLevel = level,
                // 有水面的河格：水面跟著河床一起降，水深不變
                SeaLevel = original.SeaLevel > original.ElevationLevel ? original.SeaLevel - drop : original.SeaLevel,
            };
            dug[i] = true;
        }
        return result;
    }
}
