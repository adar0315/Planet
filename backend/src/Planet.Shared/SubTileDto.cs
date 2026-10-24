using System.Text.Json.Serialization;

namespace Planet.Shared;

/// <summary>
/// 近看細分出來的小六角（水文地貌規劃 H7）：約 250 公尺一格，只在玩家附近即時產生、不存資料庫。
/// 欄位意義跟 <see cref="TileDto"/> 相同，另外多了河道的資訊，讓前端依季節決定哪些小格是水。
/// </summary>
/// <param name="SubTileId">小格編號（從一兆開始，不會跟地塊撞號）。</param>
/// <param name="ParentTileId">屬於哪一個地塊。</param>
/// <param name="Shape">形狀。</param>
/// <param name="Latitude">緯度（度）。</param>
/// <param name="Longitude">經度（度）。</param>
/// <param name="Terrain">地表覆蓋類型。</param>
/// <param name="HeightCategory">覆蓋物的高度／水深分級。</param>
/// <param name="ElevationLevel">海拔等級（每級 5 公尺）。</param>
/// <param name="SeaLevel">水面等級（海與陸地 0、湖是湖面、河是河面）。</param>
/// <param name="NeighborIds">鄰居小格的編號（同一批以外的是 null），順序同地塊的鄰居推導。</param>
/// <param name="SourceCellId">最近的模擬粗格（查四季的積雪、結冰）。</param>
/// <param name="RiverCellId">附近那條河是從哪一個粗格流出來的（查當月的流量）；附近沒有河是 null。</param>
/// <param name="RiverDistanceMeters">到那條河中心線的距離（公尺）。</param>
/// <param name="RiverWidthMeters">那條河的平均河寬（公尺）。</param>
/// <param name="Floodplain">在泛濫平原上：洪峰時會淹水。</param>
public record SubTileDto(
    long SubTileId,
    long ParentTileId,
    string Shape,
    double Latitude,
    double Longitude,
    string Terrain,
    int HeightCategory,
    int ElevationLevel,
    int SeaLevel,
    long?[] NeighborIds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? SourceCellId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? RiverCellId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    float RiverDistanceMeters = 0f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    float RiverWidthMeters = 0f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool Floodplain = false
);

/// <summary>
/// 一個模擬粗格的四季（水文地貌規劃 H8）：12 個月的氣溫、降水、流量比例，以及哪幾個月積雪、結冰。
/// 前端依遊戲時間在相鄰兩個月之間內插。
/// </summary>
/// <param name="CellId">粗格編號。</param>
/// <param name="Temperature">12 個月的氣溫（攝氏）。</param>
/// <param name="Precipitation">12 個月的降水（相對尺度，12 個月平均＝年平均）。</param>
/// <param name="DischargeRatio">12 個月的河流量相對全年平均的比例（當月河寬＝平均河寬 × √比例）。</param>
/// <param name="SnowMonths">哪幾個月地面被雪蓋住（第 m 位元＝第 m 個月，0 是 1 月）。</param>
/// <param name="FrozenMonths">哪幾個月結冰（河、湖、海冰）。</param>
/// <param name="FlowRegime">水情：0 不是河、1 常流河、2 季節河、3 間歇河。</param>
public record CellSeasonsDto(
    int CellId,
    float[] Temperature,
    float[] Precipitation,
    float[] DischargeRatio,
    int SnowMonths,
    int FrozenMonths,
    int FlowRegime
);
