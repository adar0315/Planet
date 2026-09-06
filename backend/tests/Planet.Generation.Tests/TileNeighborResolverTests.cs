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
