namespace Planet.Domain;

/// <summary>
/// 描述「資料庫裡這顆星球實際上是什麼」的單筆記錄（永遠只有一列，<see cref="Id"/> 固定為 1）。
///
/// 存在的理由：在這之前，「星球的幾何尺寸」與「用哪組地形參數生成的」這兩件事分別散落在
/// 前端寫死的常數、後端的編譯期常數、以及一個純記憶體的 Singleton 裡，
/// 沒有任何機制保證它們跟資料庫裡真正的地塊一致。實際造成兩個問題：
///
/// 1. 改了地形參數但還沒重新生成時，「星球總覽」會依新參數即時算出貼圖，
///    畫出一顆資料庫裡並不存在的星球，而且沒有任何提示。
/// 2. 前端把星球半徑寫死成 1,851,000 公尺，但這個值後端是從細分頻率推導出來的——
///    哪天改了細分頻率，前端會安靜地用錯誤的比例畫格子。
///
/// 把這些事實寫進資料庫本身（也就是被描述的那個東西旁邊），就只剩一份真相。
/// 這筆記錄只在「整顆星球生成完成」時更新，代表的是**既成事實**，
/// 不是「下次要用的設定」——後者放在 TerrainOptionsStore，兩者刻意分開。
/// </summary>
public class PlanetMetadata
{
    /// <summary>固定為 1（這張表永遠只有一列）。</summary>
    public int Id { get; set; } = 1;

    /// <summary>生成這顆星球時使用的測地線細分頻率。</summary>
    public int SubdivisionFrequency { get; set; }

    /// <summary>星球半徑（公尺），由細分頻率反推而得（見 GoldbergPlanetGenerator）。</summary>
    public double RadiusMeters { get; set; }

    /// <summary>單一地塊的邊長（公尺）。</summary>
    public double TileEdgeLengthMeters { get; set; }

    /// <summary>地塊總數。</summary>
    public long TotalTileCount { get; set; }

    /// <summary>
    /// 實際產生這批地塊的地形參數（<c>TerrainGenerationOptions</c> 的 JSON）。
    /// 存成 JSON 而不是逐欄位展開，是為了讓之後增減參數不必每次都改 schema——
    /// 這份資料只會被整份讀寫，不需要對個別參數下查詢條件。
    /// </summary>
    public string TerrainOptionsJson { get; set; } = string.Empty;

    /// <summary>這顆星球生成完成的時間（UTC）。</summary>
    public DateTime GeneratedAtUtc { get; set; }

    /// <summary>
    /// true 代表這筆記錄是「推測」的：資料庫裡本來就有地塊、但沒有對應的記錄
    /// （地塊是在這個功能加入之前生成的），啟動時依預設參數補寫。
    /// 下次重新生成後就會變成 false。
    /// </summary>
    public bool IsInferred { get; set; }
}
