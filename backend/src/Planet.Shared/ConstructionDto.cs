namespace Planet.Shared;

/// <summary>編輯模式：對一個小格做一件事（蓋東西、改地形或拆除）。</summary>
/// <param name="SubTileId">小格編號。</param>
/// <param name="ParentTileId">小格屬於哪一個地塊（見 <see cref="SubTileDto.ParentTileId"/>）。</param>
/// <param name="Action">House、Castle、Bridge、Road（蓋建築物）、Channel（挖河道）、Fill（填平）、Demolish（拆除）。</param>
public record ConstructionRequest(long SubTileId, long ParentTileId, string Action);

/// <summary>修改之後這個小格的狀態。</summary>
/// <param name="SubTileId">小格編號。</param>
/// <param name="Structure">建築物；沒有是 null。</param>
/// <param name="TerrainEdit">地形修改；沒有是 null。</param>
/// <param name="TerrainChanged">地形有沒有變：有的話前端要重新要一次小格（高度、顏色、水面都變了）。</param>
public record ConstructionResultDto(long SubTileId, string? Structure, string? TerrainEdit, bool TerrainChanged);
