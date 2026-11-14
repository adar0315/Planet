using System.Collections.Concurrent;
using Planet.Domain;
using Planet.Generation;

namespace Planet.Service;

/// <summary>一批小格的幾何：小格、彼此的鄰居（編號與批次內的索引兩種寫法）。</summary>
/// <param name="SubTiles">小格，依地塊的順序。</param>
/// <param name="NeighborIds">每個小格的鄰居編號（批次外的是 null；順序同 <see cref="TileNeighborResolver"/>）。</param>
/// <param name="NeighborIndices">每個小格在這一批裡的鄰居索引（批次外的鄰居不列）。</param>
/// <param name="IndexById">小格編號 → 在這一批裡的索引。</param>
public sealed record SubTileBatch(
    IReadOnlyList<SubTile> SubTiles,
    IReadOnlyList<long?[]> NeighborIds,
    IReadOnlyList<IReadOnlyList<int>> NeighborIndices,
    IReadOnlyDictionary<long, int> IndexById);

/// <summary>
/// 小格幾何的快取（2026-11-14）。一個地塊底下有哪些小格、每個小格的鄰居是誰，只跟格子座標有關、永遠不變，
/// 卻是近看細分裡第二貴的一段：1,900 個地塊每次重算約 400 毫秒（列舉候選點、找最近的地塊、推導鄰居，
/// 每一步都要配置小物件）。鏡頭移動時重新要的地塊大部分跟上一批重複，編輯地形之後更是整批一模一樣。
///
/// 以地塊為單位存；超過 <see cref="MaxCachedParents"/> 就整個清掉重來——
/// 下一次只會重算當下視野的那一批，比維護 LRU 的順序簡單，也不會有清到一半的狀態。
/// </summary>
public sealed class SubTileGeometryCache
{
    /// <summary>最多快取幾個地塊（每個約 2.5 KB，一萬個約 25 MB；一次視野約 1,900 個）。</summary>
    public const int MaxCachedParents = 10_000;

    /// <summary>一個地塊底下的小格與它們完整的鄰居編號（不看批次）。</summary>
    private sealed record ParentGeometry(IReadOnlyList<SubTile> SubTiles, long[][] NeighborIds);

    /// <summary>正式細分頻率下的小格網格。</summary>
    private readonly SubTileGrid _grid = new(GoldbergPlanetGenerator.SubdivisionFrequency);

    /// <summary>地塊編號 → 幾何。</summary>
    private readonly ConcurrentDictionary<long, ParentGeometry> _byParent = new();

    /// <summary>組出這些地塊底下的一批小格。</summary>
    /// <param name="parents">地塊（座標要是正規化的，資料庫裡存的就是）。</param>
    /// <param name="ct">取消權杖。</param>
    public SubTileBatch Build(IReadOnlyList<Tile> parents, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parents);
        if (_byParent.Count > MaxCachedParents) _byParent.Clear();

        // 還沒算過的地塊平行算（彼此獨立）
        var geometries = new ParentGeometry[parents.Count];
        Parallel.For(0, parents.Count, new ParallelOptions { CancellationToken = ct }, p =>
            geometries[p] = _byParent.GetOrAdd(parents[p].TileId, static (_, args) => args.Self.Compute(args.Parent), (Self: this, Parent: parents[p])));

        // 攤平。同一個小格理論上只屬於一個地塊，但請求裡可能有重複的地塊，保險起見以編號去重
        var subs = new List<SubTile>(parents.Count * SubTileGrid.Factor * SubTileGrid.Factor);
        var fullNeighbors = new List<long[]>(subs.Capacity);
        var indexById = new Dictionary<long, int>(subs.Capacity);
        foreach (var geometry in geometries)
        {
            for (var k = 0; k < geometry.SubTiles.Count; k++)
            {
                var sub = geometry.SubTiles[k];
                if (!indexById.TryAdd(sub.SubTileId, subs.Count)) continue;
                subs.Add(sub);
                fullNeighbors.Add(geometry.NeighborIds[k]);
            }
        }

        // 鄰居：批次外的編號換成 null（前端靠它知道這一批的邊界），另外給一份批次內的索引（挖河道、填平要走訪鄰居）
        var neighborIds = new long?[subs.Count][];
        var neighborIndices = new IReadOnlyList<int>[subs.Count];
        for (var i = 0; i < subs.Count; i++)
        {
            var full = fullNeighbors[i];
            var ids = new long?[full.Length];
            var indices = new List<int>(full.Length);
            for (var k = 0; k < full.Length; k++)
            {
                if (!indexById.TryGetValue(full[k], out var j)) continue;
                ids[k] = full[k];
                indices.Add(j);
            }
            neighborIds[i] = ids;
            neighborIndices[i] = indices;
        }
        return new SubTileBatch(subs, neighborIds, neighborIndices, indexById);
    }

    /// <summary>算出一個地塊的幾何（純函式）。</summary>
    /// <param name="parent">地塊。</param>
    private ParentGeometry Compute(Tile parent)
    {
        var subs = _grid.ChildrenOfParent(parent);
        var neighbors = new long[subs.Count][];
        for (var i = 0; i < subs.Count; i++) neighbors[i] = [.. _grid.NeighborIdsOf(subs[i].SubTileId)];
        return new ParentGeometry(subs, neighbors);
    }
}
