using Planet.Domain;
using Planet.Generation;
using Xunit;

namespace Planet.Generation.Tests;

/// <summary>
/// <see cref="PatchNeighborLinker"/> 的正確性測試。
///
/// 這支的重要性在於：前端「按鍵當下就移動」完全建立在這份連結結果之上。如果連結錯了，
/// 玩家會走到錯誤的格子，而且因為前端是先動再驗證，錯誤會先顯示出來才被伺服器打回。
/// 所以這裡用「整顆小星球」做全量驗證，而不是抽樣。
/// </summary>
public class PatchNeighborLinkerTests
{
    /// <summary>
    /// 把整顆星球當成一個patch：這種情況下沒有任何格子在「範圍外」，
    /// 所以每一格的每一個鄰居都必須連得起來（不得有 null），數量也要符合形狀。
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void LinkWithinPatch_WholePlanet_HasNoUnresolvedNeighbors(int n)
    {
        var tiles = new GoldbergPlanetGenerator(n).GenerateTiles().ToList();
        var resolver = new TileNeighborResolver(n);

        var links = PatchNeighborLinker.LinkWithinPatch(tiles, resolver);

        Assert.Equal(tiles.Count, links.Count);
        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            var expectedDegree = tile.Shape == TileShape.Pentagon ? 5 : 6;

            Assert.True(links[i].Length == expectedDegree,
                $"Tile(face={tile.FaceIndex},row={tile.Row},col={tile.Col},{tile.Shape}) 應有{expectedDegree}個鄰居，實際{links[i].Length}個");

            Assert.All(links[i], id => Assert.NotNull(id));
            // 不能把自己列為自己的鄰居
            Assert.DoesNotContain(tile.TileId, links[i].Select(id => id!.Value));
        }
    }

    /// <summary>
    /// 鄰接關係必須對稱：A把B當鄰居，B也一定要把A當鄰居。
    /// 這是最能抓出座標推導錯誤的檢查——手推跨面公式很容易寫出「看起來對、實際不對稱」的結果。
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void LinkWithinPatch_WholePlanet_IsSymmetric(int n)
    {
        var tiles = new GoldbergPlanetGenerator(n).GenerateTiles().ToList();
        var resolver = new TileNeighborResolver(n);

        var links = PatchNeighborLinker.LinkWithinPatch(tiles, resolver);
        var neighborsById = tiles
            .Select((t, i) => (t.TileId, Neighbors: links[i].Select(id => id!.Value).ToHashSet()))
            .ToDictionary(x => x.TileId, x => x.Neighbors);

        foreach (var (tileId, neighbors) in neighborsById)
        {
            foreach (var neighborId in neighbors)
            {
                Assert.True(neighborsById[neighborId].Contains(tileId),
                    $"鄰接不對稱：{tileId} 把 {neighborId} 當鄰居，但反過來沒有");
            }
        }
    }

    /// <summary>
    /// 只取一部分地塊當patch時：範圍內部的格子仍要完全連得起來，
    /// 而範圍邊界上的格子必須出現 null（代表鄰居存在但不在這批裡）——
    /// 前端就是靠這個 null 判斷「快走到邊緣了，該去補載下一批」。
    /// </summary>
    [Fact]
    public void LinkWithinPatch_PartialPatch_MarksOutOfPatchNeighborsAsNull()
    {
        const int n = 5;
        var all = new GoldbergPlanetGenerator(n).GenerateTiles().ToList();
        var resolver = new TileNeighborResolver(n);

        // 取北半球一小塊當patch
        var patch = all.Where(t => t.Latitude > 20).ToList();
        Assert.NotEmpty(patch);
        Assert.True(patch.Count < all.Count, "測試前提：patch必須真的只是整顆星球的一部分");

        var links = PatchNeighborLinker.LinkWithinPatch(patch, resolver);
        var idsInPatch = patch.Select(t => t.TileId).ToHashSet();

        var tilesWithNull = 0;
        for (var i = 0; i < patch.Count; i++)
        {
            // 有連到的，一定要是這批patch裡面的Tile（不能連到範圍外的Id）
            foreach (var id in links[i].Where(id => id is not null))
            {
                Assert.Contains(id!.Value, idsInPatch);
            }
            if (links[i].Any(id => id is null)) tilesWithNull++;
        }

        Assert.True(tilesWithNull > 0, "邊界上的格子應該要有連不到的鄰居（null）");
        Assert.True(tilesWithNull < patch.Count, "不應該每一格都連不齊——內部的格子應該是完整的");
    }

    /// <summary>部分patch裡「有連起來」的那些關係，彼此之間仍必須對稱。</summary>
    [Fact]
    public void LinkWithinPatch_PartialPatch_ResolvedLinksAreStillSymmetric()
    {
        const int n = 5;
        var all = new GoldbergPlanetGenerator(n).GenerateTiles().ToList();
        var resolver = new TileNeighborResolver(n);

        var patch = all.Where(t => t.Latitude > 20).ToList();
        var links = PatchNeighborLinker.LinkWithinPatch(patch, resolver);

        var neighborsById = patch
            .Select((t, i) => (t.TileId, Neighbors: links[i].Where(id => id is not null).Select(id => id!.Value).ToHashSet()))
            .ToDictionary(x => x.TileId, x => x.Neighbors);

        foreach (var (tileId, neighbors) in neighborsById)
        {
            foreach (var neighborId in neighbors)
            {
                Assert.True(neighborsById[neighborId].Contains(tileId),
                    $"patch內鄰接不對稱：{tileId} 把 {neighborId} 當鄰居，但反過來沒有");
            }
        }
    }

    /// <summary>空patch不應該爆掉。</summary>
    [Fact]
    public void LinkWithinPatch_EmptyPatch_ReturnsEmpty()
    {
        var resolver = new TileNeighborResolver(5);
        Assert.Empty(PatchNeighborLinker.LinkWithinPatch([], resolver));
    }
}
