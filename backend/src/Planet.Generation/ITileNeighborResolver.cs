using Planet.Domain;

namespace Planet.Generation;

/// <summary>
/// 依 Tile 的 FaceIndex/Row/Col 即時算出鄰居座標，不將鄰接關係存進資料庫。
/// 六角形有6個鄰居，五角形有5個；跨面時需處理正二十面體面與面之間的座標轉換。
/// </summary>
public interface ITileNeighborResolver
{
    /// <summary>
    /// 算出指定地塊的所有鄰居座標（六角形6個、五角形5個），已正規化成各自的最小面索引表示
    /// （跨面邊界/角點上的座標可能有多種等價表示，一律取面索引最小的那個，確保跟資料庫裡實際存在的
    /// Tile記錄一致）。
    /// </summary>
    /// <param name="tile">要查詢鄰居的地塊。</param>
    /// <returns>鄰居的 (FaceIndex, Row, Col) 座標列表，用來查回實際的 Tile 記錄。</returns>
    IReadOnlyList<(int FaceIndex, int Row, int Col)> GetNeighborCoordinates(Tile tile);
}
