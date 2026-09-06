using Planet.Domain;
using Planet.Generation;
using Xunit;

namespace Planet.Generation.Tests;

public class GoldbergPlanetGeneratorTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(8)]
    public void GenerateTiles_ProducesExpectedGoldbergTopology(int n)
    {
        var generator = new GoldbergPlanetGenerator(n);
        var tiles = generator.GenerateTiles().ToList();

        var expectedTotal = 10 * n * n + 2;
        Assert.Equal(expectedTotal, tiles.Count);

        var pentagons = tiles.Where(t => t.Shape == TileShape.Pentagon).ToList();
        Assert.Equal(12, pentagons.Count);

        var hexagons = tiles.Count(t => t.Shape == TileShape.Hexagon);
        Assert.Equal(expectedTotal - 12, hexagons);

        // TileId必須唯一（不能有重複格點被算成兩筆）
        Assert.Equal(tiles.Count, tiles.Select(t => t.TileId).Distinct().Count());

        // 經緯度必須在合法範圍內
        Assert.All(tiles, t =>
        {
            Assert.InRange(t.Latitude, -90.0, 90.0);
            Assert.InRange(t.Longitude, -180.0, 180.0);
        });
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void GenerateTiles_HasPentagonAtNorthAndSouthPole(int n)
    {
        var generator = new GoldbergPlanetGenerator(n);
        var tiles = generator.GenerateTiles().ToList();
        var pentagons = tiles.Where(t => t.Shape == TileShape.Pentagon).ToList();

        Assert.Contains(pentagons, t => t.Latitude > 89.9);
        Assert.Contains(pentagons, t => t.Latitude < -89.9);
    }

    [Fact]
    public void PlanetRadiusMeters_IsPositiveAndBoundedForOneKilometerEdges()
    {
        var generator = new GoldbergPlanetGenerator(1415);

        // 六邊形邊長約1公里、總格數約2000萬時，星球半徑約1850公里（遠小於地球，且<2100公里的上限）
        Assert.InRange(generator.PlanetRadiusMeters, 1_700_000, 2_000_000);
    }

    [Fact]
    public void GenerateTiles_AtProductionFrequency_ProducesExpectedCount()
    {
        var generator = new GoldbergPlanetGenerator();
        var count = generator.GenerateTiles().LongCount();

        Assert.Equal(10L * 1415 * 1415 + 2, count);
    }
}
