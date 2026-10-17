namespace Planet.Domain;

/// <summary>
/// 一個五角形或六角形地塊。鄰居不存表，由 FaceIndex/Row/Col 依 Goldberg 多面體的細分規則即時算出
/// （見 Planet.Generation 中的 ITileNeighborResolver）。
/// </summary>
public class Tile
{
    /// <summary>全域唯一識別碼，生成時依掃描順序連續指派，不編碼任何座標資訊（查座標請用 FaceIndex/Row/Col）。</summary>
    public long TileId { get; set; }

    /// <summary>地塊幾何形狀（六角形或五角形）。</summary>
    public TileShape Shape { get; set; }

    /// <summary>正二十面體的原始面編號（0-19）。</summary>
    public int FaceIndex { get; set; }

    /// <summary>面內細分網格座標（列）。</summary>
    public int Row { get; set; }

    /// <summary>面內細分網格座標（欄）。</summary>
    public int Col { get; set; }

    /// <summary>緯度（度），北緯為正，範圍 -90~90。</summary>
    public double Latitude { get; set; }

    /// <summary>經度（度），東經為正，範圍 -180~180。</summary>
    public double Longitude { get; set; }

    /// <summary>地表覆蓋類型。</summary>
    public TerrainType Terrain { get; set; }

    /// <summary>
    /// 地表覆蓋物的高度/深度細分等級，實際意義依 Terrain 而定（見 TerrainType 的說明），
    /// 跟 ElevationLevel（地形本身的海拔）是兩個獨立屬性——一格可以是「海拔100級的高地上長著腰部高的草」。
    /// 沒有細分等級的地形（Mud/Loess/Rock等）固定為0。
    /// </summary>
    public int HeightCategory { get; set; }

    /// <summary>
    /// 地形海拔等級，相對 SeaLevel，範圍 -500~+500，每級5公尺（換算公尺：ElevationLevel * 5）。
    /// 正值＝海平面以上，負值＝海平面以下（海床深度）。
    /// </summary>
    public int ElevationLevel { get; set; }

    /// <summary>
    /// 該Tile位置的海平面等級（跟ElevationLevel同單位）。目前全部Tile統一為0（星球沒有做潮汐／大地
    /// 水準面起伏），但存成每格獨立欄位，之後如果要做「海平面上升」「局部潮汐」之類的機制，不需要改schema。
    /// 判斷「是否淹沒在水下」用 ElevationLevel &lt; SeaLevel。
    /// </summary>
    public int SeaLevel { get; set; }

    /// <summary>
    /// 這格的地形是從哪一個模擬粗格放大來的（<see cref="PlanetCell.CellId"/>）。
    /// 程序化生成的星球沒有來源，是 null。完整的地質狀態經由這個欄位查 <see cref="PlanetCell"/>。
    /// </summary>
    public int? SourceCellId { get; set; }
}
