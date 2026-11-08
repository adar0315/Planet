namespace Planet.Domain;

/// <summary>
/// 玩家在一個小格（約 250 公尺，見 Planet.Generation.SubTileGrid）上蓋的東西與改過的地形（編輯模式，2026-11-08）。
///
/// 小格是即時產生、不存資料庫的，所以修改另外存一張表，產生小格時再疊上去。
/// 一個小格最多一個建築物＋一個地形修改：河道挖好之後可以在上面架橋，所以兩者分開存。
///
/// 編號是幾何的（面、列、欄），跟地形無關：重新生成或重新放大星球之後，修改還留在同一個位置，
/// 但底下的地形可能已經不一樣（例如房子變成泡在海裡）。
/// </summary>
public class Construction
{
    /// <summary>小格編號（主鍵）。</summary>
    public long SubTileId { get; set; }

    /// <summary>小格屬於哪一個地塊：產生小格時依地塊一批查。</summary>
    public long ParentTileId { get; set; }

    /// <summary>蓋在上面的建築物；沒有就是 null。</summary>
    public StructureKind? Structure { get; set; }

    /// <summary>地形修改；沒有就是 null。</summary>
    public TerrainEditKind? TerrainEdit { get; set; }

    /// <summary>最後一次修改的時間（UTC）。</summary>
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>小格上的建築物。</summary>
public enum StructureKind
{
    /// <summary>房子：只能蓋在陸地。</summary>
    House = 1,

    /// <summary>城堡：只能蓋在陸地，佔滿一個小格。</summary>
    Castle = 2,

    /// <summary>橋：只能蓋在水上（河、湖、海、挖出來的河道）。</summary>
    Bridge = 3,

    /// <summary>道路：只能蓋在陸地，會跟相鄰的路、橋、房子、城堡接起來。</summary>
    Road = 4,
}

/// <summary>小格的地形修改（只改畫面，不重算水系：下游的河不會跟著變）。</summary>
public enum TerrainEditKind
{
    /// <summary>挖河道：陸地變成一段有水的渠道。</summary>
    Channel = 1,

    /// <summary>填平：水（河、湖、海）變成陸地。</summary>
    Fill = 2,
}
