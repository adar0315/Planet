using Planet.Domain;

namespace Planet.Data;

/// <summary>「目前這顆星球實際上是什麼」的存取介面，見 <see cref="PlanetMetadata"/>。</summary>
public interface IPlanetMetadataRepository
{
    /// <summary>讀取目前的星球記錄；資料庫還沒有星球（或記錄尚未建立）時回傳 null。</summary>
    /// <param name="ct">取消權杖。</param>
    Task<PlanetMetadata?> GetAsync(CancellationToken ct = default);

    /// <summary>寫入／覆蓋星球記錄（永遠只有一列，Id 固定為 1）。</summary>
    /// <param name="metadata">要寫入的內容。</param>
    /// <param name="ct">取消權杖。</param>
    Task UpsertAsync(PlanetMetadata metadata, CancellationToken ct = default);
}
