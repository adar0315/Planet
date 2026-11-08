using Planet.Domain;

namespace Planet.Data;

/// <summary>小格修改（<see cref="Construction"/>）的存取介面。</summary>
public interface IConstructionRepository
{
    /// <summary>這些地塊底下所有被修改過的小格。</summary>
    /// <param name="parentTileIds">地塊編號。</param>
    /// <param name="ct">取消權杖。</param>
    Task<IReadOnlyList<Construction>> GetByParentsAsync(IReadOnlyCollection<long> parentTileIds, CancellationToken ct = default);

    /// <summary>一個小格的修改；沒改過回傳 null。</summary>
    /// <param name="subTileId">小格編號。</param>
    /// <param name="ct">取消權杖。</param>
    Task<Construction?> GetAsync(long subTileId, CancellationToken ct = default);

    /// <summary>寫入一個小格的修改：建築物與地形修改都是 null 時刪掉這一列。</summary>
    /// <param name="construction">要寫入的內容。</param>
    /// <param name="ct">取消權杖。</param>
    Task SaveAsync(Construction construction, CancellationToken ct = default);
}
