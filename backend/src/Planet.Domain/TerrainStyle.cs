namespace Planet.Domain;

/// <summary>
/// 一種地形（<see cref="TerrainType"/>）的顯示樣式：中英文名稱＋依時段（清晨/白天/黃昏/夜晚）變化的顏色。
/// 存成資料表（而不是寫死在程式碼裡）是為了讓「改名稱、調顏色」不需要重新編譯/部署後端或前端——
/// 直接改這張表的資料即可，前後端都是執行期讀取。新增地形「類型」本身（TerrainType列舉、
/// TerrainGenerator的生成規則）仍然需要改程式碼，這張表只管顯示樣式。
/// </summary>
public class TerrainStyle
{
    /// <summary>對應的地形類型，主鍵。</summary>
    public TerrainType Terrain { get; set; }

    /// <summary>英文名稱（例如 "Grassland"）。</summary>
    public string NameEn { get; set; } = "";

    /// <summary>繁體中文名稱（例如 "草地"）。</summary>
    public string NameZh { get; set; } = "";

    /// <summary>清晨（日出後第一小時）的顏色，格式 <c>#rrggbb</c>。</summary>
    public string ColorDawn { get; set; } = "#ffffff";

    /// <summary>白天的顏色，格式 <c>#rrggbb</c>。</summary>
    public string ColorDay { get; set; } = "#ffffff";

    /// <summary>黃昏（日落前一小時）的顏色，格式 <c>#rrggbb</c>。</summary>
    public string ColorDusk { get; set; } = "#ffffff";

    /// <summary>夜晚的顏色，格式 <c>#rrggbb</c>。</summary>
    public string ColorNight { get; set; } = "#ffffff";
}
