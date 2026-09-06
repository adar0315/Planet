using Microsoft.AspNetCore.SignalR;
using Planet.Data;
using Planet.Domain;
using Planet.Generation;
using Planet.Shared;

namespace Planet.Service.Hubs;

/// <summary>
/// 玩家即時移動同步。單人原型階段先驗證＋回傳給呼叫者自己，之後擴充多人時可依此
/// 加入房間/Group管理，把 Clients.Others.SendAsync(...) 廣播給同房間其他玩家。
/// </summary>
public class MovementHub : Hub
{
    private readonly ITileRepository _tileRepository;
    private readonly ITileNeighborResolver _neighborResolver;

    /// <summary>建立Hub。</summary>
    public MovementHub(ITileRepository tileRepository, ITileNeighborResolver neighborResolver)
    {
        _tileRepository = tileRepository;
        _neighborResolver = neighborResolver;
    }

    /// <summary>
    /// 驗證 toTileId 是否真的是 fromTileId 的合法鄰居，是的話回傳目標Tile資料；
    /// 不是（座標造假、或兩者已經不相鄰）就回傳null，前端不會移動。
    /// </summary>
    public async Task<TileDto?> MoveTo(long fromTileId, long toTileId)
    {
        // 一次把起點與終點都撈回來（原本是先查起點，再逐一查最多6個鄰居座標，最差7次DB來回）。
        var tiles = await _tileRepository.GetByIdsAsync([fromTileId, toTileId]);
        var fromTile = tiles.FirstOrDefault(t => t.TileId == fromTileId);
        var targetTile = tiles.FirstOrDefault(t => t.TileId == toTileId);
        if (fromTile is null || targetTile is null) return null;

        // 反過來比對：不必把6個鄰居都查出來，只要看終點自己的(FaceIndex,Row,Col)有沒有出現在
        // 起點的鄰居座標清單裡就好。Canonicalize保證resolver算出來的座標跟DB實際存的完全一致
        // （都取面索引最小的表示法），所以這個比對是精確的，不是近似。
        var isNeighbor = _neighborResolver
            .GetNeighborCoordinates(fromTile)
            .Any(c => c.FaceIndex == targetTile.FaceIndex && c.Row == targetTile.Row && c.Col == targetTile.Col);

        if (!isNeighbor) return null;

        return targetTile.ToDto();
    }
}
