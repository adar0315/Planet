using Planet.Domain;
using Planet.Generation;
using Xunit;

namespace Planet.Generation.Tests;

/// <summary>
/// 近看細分（水文地貌規劃 H7）：每個小格剛好屬於一個地塊、整顆星球的小格一個不漏，鄰居推導跟地塊用同一套。
/// 用小頻率跑整顆星球（n=6 → 細分 24，5,762 個小格）。
/// </summary>
public class SubTileGridTests
{
    private const int ParentFrequency = 6;

    private static readonly List<Tile> Parents = new GoldbergPlanetGenerator(ParentFrequency, 42).GenerateTiles().ToList();

    [Fact]
    public void EverySubTileBelongsToExactlyOneParent()
    {
        var grid = new SubTileGrid(ParentFrequency);
        var claims = new Dictionary<long, int>();
        foreach (var parent in Parents)
        {
            var (children, _) = grid.ChildrenOf([parent]);
            Assert.NotEmpty(children);
            Assert.All(children, c => Assert.Equal(parent.TileId, c.ParentTileId));
            foreach (var child in children) claims[child.SubTileId] = claims.GetValueOrDefault(child.SubTileId) + 1;
        }

        var total = 10L * grid.SubFrequency * grid.SubFrequency + 2;
        Assert.Equal(total, claims.Count);
        Assert.All(claims, kv => Assert.Equal(1, kv.Value));
    }

    [Fact]
    public void AParentsCenterIsOneOfItsChildren_AndChildrenAreAboutFactorSquared()
    {
        var grid = new SubTileGrid(ParentFrequency);
        var counts = new List<int>();
        foreach (var parent in Parents)
        {
            var (children, _) = grid.ChildrenOf([parent]);
            counts.Add(children.Count);
            Assert.Contains(children, c => Math.Abs(c.Latitude - parent.Latitude) < 1e-9 && Math.Abs(c.Longitude - parent.Longitude) < 1e-9);
        }
        Assert.InRange(counts.Average(), 15.5, 16.5);
    }

    [Fact]
    public void WholePlanet_EverySubTileHasCompleteNeighbors()
    {
        var grid = new SubTileGrid(ParentFrequency);
        var (children, neighbors) = grid.ChildrenOf(Parents);

        Assert.Equal(12, children.Count(c => c.Shape == TileShape.Pentagon));
        for (var i = 0; i < children.Count; i++)
        {
            var expected = children[i].Shape == TileShape.Pentagon ? 5 : 6;
            Assert.Equal(expected, neighbors[i].Length);
            Assert.All(neighbors[i], id => Assert.NotNull(id));
        }
        // 鄰居是對稱的
        var index = children.Select((c, i) => (c.SubTileId, i)).ToDictionary(x => x.SubTileId, x => x.i);
        for (var i = 0; i < children.Count; i++)
            foreach (var nb in neighbors[i])
                Assert.Contains(children[i].SubTileId, neighbors[index[nb!.Value]].Select(x => x!.Value));
    }
}
