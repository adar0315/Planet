namespace Planet.Domain;

/// <summary>
/// Goldberg 多面體地塊的兩種幾何形狀：絕大多數是六角形，只有原始正二十面體的12個頂點會是五角形。
/// </summary>
public enum TileShape
{
    /// <summary>六角形地塊，有6個鄰居。</summary>
    Hexagon,

    /// <summary>五角形地塊，有5個鄰居（正二十面體的12個頂點，星球上恰好12格）。</summary>
    Pentagon,
}
