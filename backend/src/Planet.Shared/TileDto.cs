using System.Text.Json.Serialization;

namespace Planet.Shared;

/// <summary>
/// 前後端共用的地塊資料傳輸物件。REST API 與 SignalR Hub 回傳地塊資料一律用這個型別
/// （對應 Planet.Domain.Tile，但列舉值序列化成字串、只暴露前端需要的欄位）。
/// </summary>
/// <param name="TileId">全域唯一識別碼。</param>
/// <param name="Shape">地塊形狀（"Hexagon" 或 "Pentagon"）。</param>
/// <param name="FaceIndex">正二十面體原始面編號（0-19）。</param>
/// <param name="Latitude">緯度（度），北緯為正。</param>
/// <param name="Longitude">經度（度），東經為正。</param>
/// <param name="Terrain">地表覆蓋類型名稱（TerrainType 列舉值的字串表示）。</param>
/// <param name="HeightCategory">地表覆蓋物高度/深度細分等級，實際意義依 Terrain 而定。</param>
/// <param name="ElevationLevel">地形海拔等級，範圍 -500~+500，每級5公尺。</param>
/// <param name="SeaLevel">該位置的海平面等級（跟 ElevationLevel 同單位）。</param>
/// <param name="NeighborIds">
/// 這個地塊的鄰居 TileId：六角形固定6個、五角形固定5個，順序對應
/// <c>ITileNeighborResolver.GetNeighborCoordinates</c> 的輸出。
///
/// 陣列長度一律是完整的鄰居數，缺的位置用 <c>null</c> 表示「該鄰居確實存在，但不在這次回傳的
/// 地塊範圍內」（patch邊緣的格子會這樣）——刻意不壓縮成變長清單，否則就分不出
/// 「五角形本來就只有5個」與「六角形有一個鄰居在範圍外」。
///
/// 只有 <c>GET /api/tiles/nearby</c> 會填這個欄位（它一次回傳一整片地塊，能在記憶體內互相連結）；
/// 單一地塊查詢與 SignalR 回傳為 null，序列化時會整個省略。
/// </param>
public record TileDto(
    long TileId,
    string Shape,
    int FaceIndex,
    double Latitude,
    double Longitude,
    string Terrain,
    int HeightCategory,
    int ElevationLevel,
    int SeaLevel,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long?[]? NeighborIds = null
);
