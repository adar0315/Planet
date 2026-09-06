using Planet.Domain;

namespace Planet.Data;

/// <summary>地形顯示樣式（<see cref="TerrainStyle"/>）的存取介面。</summary>
public interface ITerrainStyleRepository
{
    /// <summary>取得所有地形的顯示樣式。</summary>
    /// <param name="ct">取消權杖。</param>
    Task<IReadOnlyList<TerrainStyle>> GetAllAsync(CancellationToken ct = default);

    /// <summary>依地形類型查詢單一顯示樣式，查無資料回傳 null。</summary>
    /// <param name="terrain">地形類型。</param>
    /// <param name="ct">取消權杖。</param>
    Task<TerrainStyle?> GetByTerrainAsync(TerrainType terrain, CancellationToken ct = default);

    /// <summary>更新一種地形的顯示樣式（名稱／顏色），這張表本身資料量小（18筆），直接整筆覆蓋即可。</summary>
    /// <param name="style">要寫入的樣式，<see cref="TerrainStyle.Terrain"/> 決定要更新哪一筆。</param>
    /// <param name="ct">取消權杖。</param>
    /// <returns>true＝更新成功；false＝找不到指定的地形類型（表示還沒有這筆資料）。</returns>
    Task<bool> UpdateAsync(TerrainStyle style, CancellationToken ct = default);
}
