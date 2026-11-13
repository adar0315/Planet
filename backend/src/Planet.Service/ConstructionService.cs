using Planet.Data;
using Planet.Domain;
using Planet.Generation;
using Planet.Shared;

namespace Planet.Service;

/// <summary>編輯模式（2026-11-08）：檢查規則、寫入小格的修改。</summary>
public sealed class ConstructionService
{
    /// <summary>
    /// 用來建立 scoped 的 <see cref="IConstructionRepository"/>（本服務是 Singleton，不能直接注入 scoped 的 DbContext）。
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;
    /// <summary>產生小格與套用修改的服務，用來讀取修改前的小格狀態。</summary>
    private readonly SubTileService _subTiles;
    /// <summary>正式細分頻率下的小格網格，用來查相連的城堡格。</summary>
    private readonly SubTileGrid _grid = new(GoldbergPlanetGenerator.SubdivisionFrequency);

    /// <summary>同一時間只處理一個修改：讀出目前狀態、檢查、寫回之間不能被別的請求插隊。</summary>
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>建立服務。</summary>
    public ConstructionService(IServiceScopeFactory scopeFactory, SubTileService subTiles)
    {
        _scopeFactory = scopeFactory;
        _subTiles = subTiles;
    }

    /// <summary>對一個小格做一件事。</summary>
    /// <param name="request">要做的事。</param>
    /// <param name="ct">取消權杖。</param>
    /// <returns>成功時是修改之後的狀態；失敗時是給玩家看的原因，<c>NotFound</c> 表示找不到這個小格。</returns>
    public async Task<(ConstructionResultDto? Result, string? Error, bool NotFound)> ApplyAsync(ConstructionRequest request, CancellationToken ct)
    {
        if (!TryParseAction(request.Action, out var structure, out var terrainEdit, out var demolish))
            return (null, $"不認得的動作：{request.Action}", false);

        var raw = await _subTiles.GetRawSurfaceAsync(request.ParentTileId, request.SubTileId, ct);
        if (raw is not { } surface) return (null, "找不到這個小格。", true);

        await _lock.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IConstructionRepository>();
            var current = await repository.GetAsync(request.SubTileId, ct);
            var currentStructure = current?.Structure;
            var currentEdit = current?.TerrainEdit;
            var nextStructure = currentStructure;
            var nextEdit = currentEdit;

            if (demolish)
            {
                // 先拆建築物，沒有建築物才把地形改回原樣
                if (currentStructure is not null) nextStructure = null;
                else if (currentEdit is not null) nextEdit = null;
                else return (null, "這裡沒有東西可以拆。", false);
            }
            else if (structure is { } kind)
            {
                if (currentStructure == kind) return (null, "這裡已經有一樣的建築物了。", false);
                if (!ConstructionRules.CanPlace(ConstructionRules.Apply(surface, currentEdit), kind, out var reason))
                    return (null, reason, false);
                if (kind == StructureKind.Castle)
                {
                    var size = await ConstructionRules.CastleSizeAfterPlacingAsync(
                        request.SubTileId,
                        _grid.NeighborIdsOf,
                        async ids => (await repository.GetByIdsAsync(ids, ct))
                            .Where(c => c.Structure == StructureKind.Castle)
                            .Select(c => c.SubTileId)
                            .ToHashSet());
                    if (size > ConstructionRules.MaxCastleCells)
                        return (null, $"一座城堡最多 {ConstructionRules.MaxCastleCells} 格；這一格會跟旁邊的城堡連起來，超過了。", false);
                }
                nextStructure = kind;
            }
            else if (!ConstructionRules.TryEditTerrain(surface, currentEdit, currentStructure, terrainEdit!.Value, out nextEdit, out var reason))
            {
                return (null, reason, false);
            }

            await repository.SaveAsync(new Construction
            {
                SubTileId = request.SubTileId,
                ParentTileId = request.ParentTileId,
                Structure = nextStructure,
                TerrainEdit = nextEdit,
                UpdatedAtUtc = DateTime.UtcNow,
            }, ct);
            return (new ConstructionResultDto(
                request.SubTileId, nextStructure?.ToString(), nextEdit?.ToString(), nextEdit != currentEdit), null, false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 把前端送來的動作字串解析成建築物、地形修改或拆除三者之一。
    /// 純數字字串（例如 "7"）雖然 <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> 吃得下，但不是合法動作，一律拒絕。
    /// </summary>
    /// <param name="action">動作名稱（不分大小寫），例如 <c>Castle</c>、<c>DigChannel</c>、<c>Demolish</c>。</param>
    /// <param name="structure">解析成建築物時的種類，否則為 <see langword="null"/>。</param>
    /// <param name="terrainEdit">解析成地形修改時的種類，否則為 <see langword="null"/>。</param>
    /// <param name="demolish">是否為拆除。</param>
    /// <returns>是否為合法的動作。</returns>
    private static bool TryParseAction(string action, out StructureKind? structure, out TerrainEditKind? terrainEdit, out bool demolish)
    {
        structure = null;
        terrainEdit = null;
        demolish = string.Equals(action, "Demolish", StringComparison.OrdinalIgnoreCase);
        if (demolish) return true;
        // Enum.TryParse 也吃數字字串（"7"），那不是合法的動作
        if (Enum.TryParse<StructureKind>(action, true, out var s) && Enum.IsDefined(s) && !char.IsDigit(action[0]))
        {
            structure = s;
            return true;
        }
        if (Enum.TryParse<TerrainEditKind>(action, true, out var t) && Enum.IsDefined(t) && !char.IsDigit(action[0]))
        {
            terrainEdit = t;
            return true;
        }
        return false;
    }
}
