namespace Planet.Shared;

/// <summary>
/// 一種地形的顯示樣式：中英文名稱＋依時段（清晨/白天/黃昏/夜晚）變化的顏色，供前端渲染跟HUD顯示用。
/// </summary>
/// <param name="Terrain">地形類型名稱（TerrainType 列舉值的字串表示，跟 TileDto.Terrain 用同一套名稱）。</param>
/// <param name="NameEn">英文名稱。</param>
/// <param name="NameZh">繁體中文名稱。</param>
/// <param name="ColorDawn">清晨（日出後第一小時）的顏色，格式 <c>#rrggbb</c>。</param>
/// <param name="ColorDay">白天的顏色，格式 <c>#rrggbb</c>。</param>
/// <param name="ColorDusk">黃昏（日落前一小時）的顏色，格式 <c>#rrggbb</c>。</param>
/// <param name="ColorNight">夜晚的顏色，格式 <c>#rrggbb</c>。</param>
public record TerrainStyleDto(
    string Terrain,
    string NameEn,
    string NameZh,
    string ColorDawn,
    string ColorDay,
    string ColorDusk,
    string ColorNight
);
