using Planet.Domain;
using Planet.Generation;
using Xunit;

namespace Planet.Generation.Tests;

public class TileNeighborResolverTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    public void GetNeighborCoordinates_DegreeMatchesShape(int n)
    {
        var tiles = new GoldbergPlanetGenerator(n).GenerateTiles().ToList();
        var resolver = new TileNeighborResolver(n);

        foreach (var tile in tiles)
        {
            var neighbors = resolver.GetNeighborCoordinates(tile);
            var expectedDegree = tile.Shape == TileShape.Pentagon ? 5 : 6;

            Assert.True(neighbors.Count == expectedDegree,
                $"Tile(face={tile.FaceIndex},row={tile.Row},col={tile.Col},{tile.Shape}) 應有{expectedDegree}個鄰居，實際{neighbors.Count}個");

            Assert.Equal(neighbors.Count, neighbors.Distinct().Count());
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void GetNeighborCoordinates_EveryNeighborCoordinateMatchesARealCanonicalTile(int n)
    {
        var tiles = new GoldbergPlanetGenerator(n).GenerateTiles().ToList();
        var canonicalCoords = tiles.Select(t => (t.FaceIndex, t.Row, t.Col)).ToHashSet();
        var resolver = new TileNeighborResolver(n);

        foreach (var tile in tiles)
        {
            foreach (var neighbor in resolver.GetNeighborCoordinates(tile))
            {
                Assert.Contains(neighbor, canonicalCoords);
            }
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void GetNeighborCoordinates_IsSymmetric(int n)
    {
        var tiles = new GoldbergPlanetGenerator(n).GenerateTiles().ToList();
        var byCoord = tiles.ToDictionary(t => (t.FaceIndex, t.Row, t.Col));
        var resolver = new TileNeighborResolver(n);

        foreach (var tile in tiles)
        {
            var tileCoord = (tile.FaceIndex, tile.Row, tile.Col);
            foreach (var neighborCoord in resolver.GetNeighborCoordinates(tile))
            {
                var neighborTile = byCoord[neighborCoord];
                var reverseNeighbors = resolver.GetNeighborCoordinates(neighborTile);

                Assert.Contains(tileCoord, reverseNeighbors);
            }
        }
    }

    /// <summary>
    /// 快速路徑（面內部的六角格直接加減座標）必須跟一般路徑給出一模一樣的鄰居，**順序也一樣**——
    /// 前端靠順序對應方位，小格的鄰居編號也照這個順序。
    /// </summary>
    [Theory]
    [InlineData(6)]
    [InlineData(9)]
    [InlineData(16)]
    public void GetNeighborCoordinates_FastPathMatchesGeneralPath(int n)
    {
        var resolver = new TileNeighborResolver(n);
        var tiles = new GoldbergPlanetGenerator(n, 42).GenerateTiles().ToList();
        var interior = 0;
        foreach (var tile in tiles)
        {
            Assert.Equal(resolver.GetNeighborCoordinatesGeneral(tile), resolver.GetNeighborCoordinates(tile));
            if (tile.Shape == TileShape.Hexagon && tile.Row >= 2 && tile.Col >= 2 && n - tile.Row - tile.Col >= 2) interior++;
        }
        Assert.True(interior > 0, "這個頻率下沒有任何格子走快速路徑，測不到東西");
    }

    [Fact]
    public void GetNeighborCoordinates_TotalDegreeMatchesGoldbergEdgeCount()
    {
        const int n = 5;
        var tiles = new GoldbergPlanetGenerator(n).GenerateTiles().ToList();
        var resolver = new TileNeighborResolver(n);

        var totalDegree = tiles.Sum(t => resolver.GetNeighborCoordinates(t).Count);

        // Goldberg多面體邊數 E = (6*hexagons + 5*pentagons) / 2
        var hexagons = tiles.Count(t => t.Shape == TileShape.Hexagon);
        var pentagons = tiles.Count(t => t.Shape == TileShape.Pentagon);
        var expectedEdges = (6 * hexagons + 5 * pentagons) / 2;

        Assert.Equal(expectedEdges * 2, totalDegree);
    }
}
