namespace Planet.Domain;

/// <summary>
/// 一個小格畫面上用到的地表欄位（意義同 <see cref="Tile"/>）。編輯規則只看這些，
/// 所以不必依賴傳給前端的 DTO，也能在沒有模擬存檔的情況下測試。
/// </summary>
/// <param name="Terrain">地表類型。</param>
/// <param name="HeightCategory">覆蓋物高度／水深細分等級。</param>
/// <param name="ElevationLevel">海拔等級（每級 5 公尺）。</param>
/// <param name="SeaLevel">水面等級；陸地是 0。</param>
/// <param name="RiverCellId">附近那條河從哪一個粗格流出來；沒有是 null。</param>
/// <param name="RiverDistanceMeters">到河中心線的距離（公尺）。</param>
/// <param name="RiverWidthMeters">河的平均寬度（公尺）；沒有河是 0。</param>
/// <param name="Floodplain">在泛濫平原上。</param>
/// <param name="SourceCellId">最近的模擬粗格（查四季）；程序化地形沒有。</param>
public readonly record struct SubTileSurface(
    TerrainType Terrain,
    int HeightCategory,
    int ElevationLevel,
    int SeaLevel,
    int? RiverCellId,
    float RiverDistanceMeters,
    float RiverWidthMeters,
    bool Floodplain,
    int? SourceCellId = null);

/// <summary>
/// 編輯模式的規則（2026-11-08）：什麼可以蓋在哪裡、挖河道與填平之後地形變成什麼樣子。
/// 只改畫面，不重算水系——挖出來的河道是一段靜止的水，下游的河不會跟著改道。
/// </summary>
public static class ConstructionRules
{
    /// <summary>
    /// 河道最少畫多寬（半寬，公尺）：前端 seasons.ts 的 MIN_VISIBLE_RIVER_HALF_WIDTH_METERS、
    /// 後端 LandformSampler.VisibleRiverHalfWidthMeters 是同一個值。這個範圍內的小格畫成河，所以算水。
    /// </summary>
    public const double VisibleRiverHalfWidthMeters = 220;

    /// <summary>挖出來的河道底部比原地面低幾級（每級 5 公尺，畫面上再乘 8 倍）。</summary>
    public const int ChannelBedDepthLevels = 4;

    /// <summary>挖出來的河道水面比原地面低幾級：水面略低於兩岸，看得出是挖下去的。</summary>
    public const int ChannelWaterDepthLevels = 1;

    /// <summary>
    /// 這一格是不是水：湖、海、珊瑚礁、水面高過地面的格子，以及畫成河的那一帶。
    /// 河的寬度會隨季節變，這裡用平均寬度判斷：乾季露出的河床也算河，不能蓋房子。
    /// </summary>
    /// <param name="surface">地表。</param>
    public static bool IsWater(SubTileSurface surface)
    {
        if (surface.Terrain is TerrainType.Freshwater or TerrainType.Seawater or TerrainType.CoralReef) return true;
        if (surface.ElevationLevel < surface.SeaLevel) return true;
        return surface.RiverWidthMeters > 0
               && surface.RiverDistanceMeters <= Math.Max(surface.RiverWidthMeters / 2, VisibleRiverHalfWidthMeters);
    }

    /// <summary>這種建築物能不能蓋在這個地表上；不能的話 <paramref name="reason"/> 是給玩家看的原因。</summary>
    /// <param name="surface">套用地形修改之後的地表。</param>
    /// <param name="structure">要蓋的建築物。</param>
    /// <param name="reason">不能蓋的原因。</param>
    public static bool CanPlace(SubTileSurface surface, StructureKind structure, out string? reason)
    {
        var water = IsWater(surface);
        reason = structure switch
        {
            StructureKind.Bridge when !water => "橋只能蓋在水上（河、湖、海或挖出來的河道）。",
            StructureKind.House when water => "房子只能蓋在陸地上。",
            StructureKind.Castle when water => "城堡只能蓋在陸地上。",
            StructureKind.Road when water => "路只能蓋在陸地上；水上請用橋。",
            _ => null,
        };
        return reason is null;
    }

    /// <summary>
    /// 套用地形修改之後的地表。
    /// </summary>
    /// <param name="raw">原本（模擬產生）的地表。</param>
    /// <param name="edit">地形修改；null 就是原樣。</param>
    /// <param name="landNeighbourLevel">
    /// 相鄰陸地小格的平均海拔等級（填平時填到跟兩岸一樣高）；沒有相鄰陸地時是 null，填到剛好露出水面。
    /// </param>
    public static SubTileSurface Apply(SubTileSurface raw, TerrainEditKind? edit, int? landNeighbourLevel = null)
    {
        switch (edit)
        {
            case TerrainEditKind.Channel:
                return raw with
                {
                    Terrain = TerrainType.Freshwater,
                    HeightCategory = 2, // 到腰部的水深（意義見 TerrainType.Freshwater）
                    ElevationLevel = raw.ElevationLevel - ChannelBedDepthLevels,
                    SeaLevel = raw.ElevationLevel - ChannelWaterDepthLevels,
                    RiverCellId = null,
                    RiverDistanceMeters = 0,
                    RiverWidthMeters = 0,
                    Floodplain = false,
                };
            case TerrainEditKind.Fill:
                {
                    // 至少填到水面（海、湖）；有相鄰陸地就跟兩岸一樣高（河床本來比兩岸低）
                    var waterSurface = raw.ElevationLevel < raw.SeaLevel ? raw.SeaLevel : raw.ElevationLevel;
                    var level = Math.Max(waterSurface, landNeighbourLevel ?? waterSurface);
                    return raw with
                    {
                        Terrain = TerrainType.Grassland,
                        HeightCategory = 0,
                        ElevationLevel = level,
                        SeaLevel = Math.Min(raw.SeaLevel, level),
                        RiverCellId = null,
                        RiverDistanceMeters = 0,
                        RiverWidthMeters = 0,
                        Floodplain = false,
                    };
                }
            default:
                return raw;
        }
    }

    /// <summary>
    /// 在一個小格上挖河道或填平之後，要存的地形修改。
    /// 對已經改過的格子做相反的事是「復原」：填平挖出來的河道回到原本的陸地，反之亦然。
    /// </summary>
    /// <param name="raw">原本的地表。</param>
    /// <param name="current">目前的地形修改。</param>
    /// <param name="structure">目前的建築物。</param>
    /// <param name="requested">要做的修改。</param>
    /// <param name="result">修改之後要存的值（null＝回到原樣）。</param>
    /// <param name="reason">不能做的原因。</param>
    public static bool TryEditTerrain(
        SubTileSurface raw, TerrainEditKind? current, StructureKind? structure, TerrainEditKind requested,
        out TerrainEditKind? result, out string? reason)
    {
        result = current;
        var water = IsWater(Apply(raw, current));
        if (requested == TerrainEditKind.Channel)
        {
            if (water) { reason = "這裡已經是水了。"; return false; }
            if (structure is not null) { reason = "上面有建築物，先拆掉才能挖河道。"; return false; }
            result = current == TerrainEditKind.Fill ? null : TerrainEditKind.Channel;
        }
        else
        {
            if (!water) { reason = "這裡不是水，沒有東西可以填。"; return false; }
            if (structure is not null) { reason = "上面有橋，先拆掉才能填平。"; return false; }
            result = current == TerrainEditKind.Channel ? null : TerrainEditKind.Fill;
        }
        reason = null;
        return true;
    }
}
