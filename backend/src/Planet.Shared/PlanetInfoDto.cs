namespace Planet.Shared;

/// <summary>
/// 描述「目前這顆星球實際上是什麼」，供前端取代原本寫死的常數。
///
/// 之前前端把星球半徑寫死成 1,851,000 公尺、Tile邊長寫死成 1000 公尺，
/// 但這些值後端是從細分頻率**推導**出來的——改了細分頻率，前端不會知道，
/// 只會安靜地用錯誤的比例畫格子。改由後端提供之後就只剩一份真相。
/// </summary>
/// <param name="RadiusMeters">星球半徑（公尺）。</param>
/// <param name="TileEdgeLengthMeters">單一地塊的邊長（公尺）。</param>
/// <param name="SubdivisionFrequency">測地線細分頻率。</param>
/// <param name="TotalTileCount">地塊總數。</param>
/// <param name="OverviewTextureWidth">星球總覽貼圖寬度（像素）。</param>
/// <param name="OverviewTextureHeight">星球總覽貼圖高度（像素）。</param>
/// <param name="WaterMaskWidth">陸海遮罩寬度（像素），對應 <c>GET /api/tiles/overview-water-mask</c>。</param>
/// <param name="WaterMaskHeight">陸海遮罩高度（像素）。</param>
/// <param name="GeneratedAtUtc">這顆星球生成完成的時間（UTC）；尚未生成時為 null。</param>
/// <param name="IsInferred">
/// true 代表這份記錄是依預設參數推測的（地塊在此功能加入前就已存在），
/// 下次重新生成後會變成 false。
/// </param>
public record PlanetInfoDto(
    double RadiusMeters,
    double TileEdgeLengthMeters,
    int SubdivisionFrequency,
    long TotalTileCount,
    int OverviewTextureWidth,
    int OverviewTextureHeight,
    int WaterMaskWidth,
    int WaterMaskHeight,
    DateTime? GeneratedAtUtc,
    bool IsInferred
);
