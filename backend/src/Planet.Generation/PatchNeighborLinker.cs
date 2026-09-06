using Planet.Domain;

namespace Planet.Generation;

/// <summary>
/// 把一批地塊（一個「patch」）內部的鄰接關係連起來：對每個地塊算出它的鄰居，
/// 並換成同一批裡的 TileId。完全在記憶體內完成，不查資料庫。
///
/// 存在的理由：前端要能「按鍵當下就移動」，就必須自己知道腳下這格的鄰居是誰，不能每走一步
/// 都等伺服器回答。但跨面／五角形的鄰居座標推導是整個專案最難的一段程式碼（已經踩過三次
/// 座標handedness的坑），**絕對不應該再用TypeScript複製一份到前端**。
/// 折衷做法就是這支：演算法仍然只有伺服器這一份，伺服器把算好的結果一起送給前端。
///
/// 之所以不用額外查資料庫，是因為 <see cref="TileNeighborResolver.GetNeighborCoordinates"/>
/// 回傳的座標已經過正規化（取面索引最小的表示法），跟 <see cref="GoldbergPlanetGenerator"/>
/// 當初寫進資料庫時用的規則完全相同，所以拿座標直接對這批地塊自己的字典查表就是精確比對。
/// </summary>
public static class PatchNeighborLinker
{
    /// <summary>
    /// 算出 <paramref name="patch"/> 內每個地塊的鄰居 TileId。
    /// </summary>
    /// <param name="patch">一批地塊，回傳結果的順序與這個清單一一對應。</param>
    /// <param name="resolver">鄰居座標解析器。</param>
    /// <returns>
    /// 與 <paramref name="patch"/> 等長的清單；每個元素是該地塊的鄰居 TileId 陣列
    /// （六角形6個、五角形5個，順序即 resolver 的輸出順序）。
    /// 值為 <c>null</c> 表示該鄰居存在於星球上、但不在這批地塊裡（patch邊緣）。
    /// </returns>
    public static IReadOnlyList<long?[]> LinkWithinPatch(IReadOnlyList<Tile> patch, ITileNeighborResolver resolver)
    {
        var idByCoordinate = new Dictionary<(int FaceIndex, int Row, int Col), long>(patch.Count);
        foreach (var tile in patch)
        {
            idByCoordinate[(tile.FaceIndex, tile.Row, tile.Col)] = tile.TileId;
        }

        var result = new long?[patch.Count][];
        for (var i = 0; i < patch.Count; i++)
        {
            var coordinates = resolver.GetNeighborCoordinates(patch[i]);
            var neighbors = new long?[coordinates.Count];
            for (var k = 0; k < coordinates.Count; k++)
            {
                neighbors[k] = idByCoordinate.TryGetValue(coordinates[k], out var id) ? id : null;
            }
            result[i] = neighbors;
        }

        return result;
    }
}
