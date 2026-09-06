using Planet.Domain;
using Planet.Shared;

namespace Planet.Service;

/// <summary>
/// <see cref="Tile"/>（內部實體）轉 <see cref="TileDto"/>（前端契約）的共用轉換。
/// 集中在一處是為了讓經緯度的精度處理只有一份——REST 與 SignalR 回傳同一個Tile時，
/// 兩邊的數值必須完全一致，否則前端拿兩條路徑的資料互相比對座標時會對不起來。
/// </summary>
public static class TileMapping
{
    /// <summary>
    /// 經緯度序列化的小數位數。原本直接輸出 double 的完整精度（約17位有效數字），
    /// 但星球半徑1,851公里下 1e-6 度約等於 3.2 公分，對邊長1公里的格子而言已經是三萬倍的過剩精度；
    /// 而那些尾數是近乎隨機的數字、幾乎不可壓縮，每格白白多出約34個字元。
    /// </summary>
    private const int CoordinateDecimals = 6;

    /// <summary>把地塊實體轉成前端用的DTO。</summary>
    /// <param name="tile">來源地塊。</param>
    /// <param name="neighborIds">
    /// 選填，這個地塊的鄰居TileId（六角6個、五角5個），順序對應
    /// <see cref="Generation.ITileNeighborResolver.GetNeighborCoordinates"/> 的輸出；
    /// 其中的 null 代表「該鄰居確實存在，但不在這次回傳的範圍內」。
    /// </param>
    public static TileDto ToDto(this Tile tile, long?[]? neighborIds = null) => new(
        tile.TileId,
        tile.Shape.ToString(),
        tile.FaceIndex,
        Math.Round(tile.Latitude, CoordinateDecimals),
        Math.Round(tile.Longitude, CoordinateDecimals),
        tile.Terrain.ToString(),
        tile.HeightCategory,
        tile.ElevationLevel,
        tile.SeaLevel,
        neighborIds);
}
