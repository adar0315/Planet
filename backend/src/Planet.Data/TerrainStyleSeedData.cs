using Planet.Domain;

namespace Planet.Data;

/// <summary>
/// <see cref="TerrainStyle"/> 資料表的初始種子資料：18種地形各自的中英文名稱＋依時段（清晨/白天/黃昏/夜晚）
/// 變化的顏色。白天顏色沿用原本寫死在程式碼裡的調色盤（<c>frontend/src/map/terrainColors.ts</c>／
/// 舊版 <c>OverviewTextureGenerator.ColorFor</c>），其餘三個時段用統一的亮度／色溫公式從白天顏色推算，
/// 不用手動為18種地形各自挑72個顏色值。這份資料透過EF Core的 <c>HasData</c> 寫進migration，
/// 之後這張表本身可以直接改資料庫內容調整，不需要重新跑一次seed。
/// </summary>
internal static class TerrainStyleSeedData
{
    /// <summary>(TerrainType, 英文名, 中文名, 白天基準色) 的原始清單，其餘時段顏色由 <see cref="All"/> 推算。</summary>
    private static readonly (TerrainType Terrain, string NameEn, string NameZh, int DayColor)[] Base =
    [
        (TerrainType.Grassland, "Grassland", "草地", 0x8fae4e),
        (TerrainType.Shrubland, "Shrubland", "灌木叢", 0x6b8f3e),
        (TerrainType.Forest, "Forest", "林地", 0x2e6b34),
        (TerrainType.Freshwater, "Freshwater", "淡水", 0x5dade2),
        (TerrainType.Seawater, "Seawater", "海水", 0x1b4f72),
        (TerrainType.Mud, "Mud", "泥地", 0x6b5439),
        (TerrainType.Loess, "Loess", "黃土地", 0xc2a45e),
        (TerrainType.Rock, "Rock", "岩地", 0x7b7d7d),
        (TerrainType.Sand, "Sand", "沙地", 0xe0c988),
        (TerrainType.SnowField, "SnowField", "雪地", 0xf2f6fa),
        (TerrainType.Glacier, "Glacier", "冰原", 0xcfe8f0),
        (TerrainType.Swamp, "Swamp", "沼澤", 0x4d5a3a),
        (TerrainType.SaltFlat, "SaltFlat", "鹽鹼地", 0xe8e6de),
        (TerrainType.VolcanicRock, "VolcanicRock", "火山岩地", 0x3a2c2c),
        (TerrainType.CoralReef, "CoralReef", "珊瑚礁", 0x3aa9a0),
        (TerrainType.Cliff, "Cliff", "懸崖", 0x5c5652),
        (TerrainType.Tundra, "Tundra", "苔原", 0x9aa082),
        (TerrainType.Gravel, "Gravel", "礫灘", 0x9c9284),
    ];

    /// <summary>18種地形的完整樣式清單（含推算出的清晨/黃昏/夜晚顏色）。</summary>
    public static IReadOnlyList<TerrainStyle> All { get; } = Base
        .Select(b => new TerrainStyle
        {
            Terrain = b.Terrain,
            NameEn = b.NameEn,
            NameZh = b.NameZh,
            ColorDawn = AdjustColor(b.DayColor, brightness: 0.85, warmth: 0.12),
            ColorDay = ToHex(b.DayColor),
            ColorDusk = AdjustColor(b.DayColor, brightness: 0.75, warmth: 0.20),
            ColorNight = AdjustColor(b.DayColor, brightness: 0.30, warmth: -0.15),
        })
        .ToList();

    /// <summary>
    /// 依亮度倍率＋色溫偏移從白天基準色推算其他時段的顏色。warmth為正＝偏暖（提高R、壓低B，
    /// 模擬清晨/黃昏的金色調），為負＝偏冷（壓低R、提高B，模擬夜晚的藍調月光）。
    /// </summary>
    private static string AdjustColor(int baseColor, double brightness, double warmth)
    {
        var r = (baseColor >> 16) & 0xFF;
        var g = (baseColor >> 8) & 0xFF;
        var b = baseColor & 0xFF;

        var adjustedR = Clamp(r * brightness * (1 + warmth));
        var adjustedG = Clamp(g * brightness);
        var adjustedB = Clamp(b * brightness * (1 - warmth));

        return $"#{adjustedR:x2}{adjustedG:x2}{adjustedB:x2}";
    }

    private static string ToHex(int color) => $"#{color:x6}";

    private static int Clamp(double value) => Math.Clamp((int)Math.Round(value), 0, 255);
}
