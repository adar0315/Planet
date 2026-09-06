using Planet.Domain;
using Planet.Generation;
using Xunit;

namespace Planet.Generation.Tests;

public class TerrainGeneratorTests
{
    private static readonly TerrainType[] OceanTerrains = [TerrainType.Seawater, TerrainType.CoralReef, TerrainType.Glacier];

    private static readonly TerrainType[] LandTerrains =
    [
        TerrainType.Grassland, TerrainType.Shrubland, TerrainType.Forest, TerrainType.Freshwater,
        TerrainType.Mud, TerrainType.Loess, TerrainType.Rock, TerrainType.Sand, TerrainType.SnowField,
        TerrainType.Glacier, TerrainType.Swamp, TerrainType.SaltFlat, TerrainType.VolcanicRock,
        TerrainType.Cliff, TerrainType.Tundra, TerrainType.Gravel,
    ];

    [Theory]
    [InlineData(90.0, 0.0)]
    [InlineData(-90.0, 0.0)]
    [InlineData(85.0, 123.0)]
    [InlineData(-84.0, -50.0)]
    public void Sample_ForcesGlacier_NearPoles(double latitude, double longitude)
    {
        var generator = new TerrainGenerator(seed: 1);
        var result = generator.Sample(latitude, longitude);

        Assert.Equal(TerrainType.Glacier, result.Terrain);
    }

    [Fact]
    public void Sample_IsDeterministic_ForSameSeedAndCoordinates()
    {
        var a = new TerrainGenerator(seed: 7);
        var b = new TerrainGenerator(seed: 7);

        var sampleA = a.Sample(12.3, 45.6);
        var sampleB = b.Sample(12.3, 45.6);

        Assert.Equal(sampleA, sampleB);
    }

    [Fact]
    public void Sample_DifferentSeeds_ProduceDifferentContinentLayouts()
    {
        var a = new TerrainGenerator(seed: 1);
        var b = new TerrainGenerator(seed: 2);

        var differences = 0;
        for (var lat = -70; lat <= 70; lat += 5)
        {
            for (var lon = -175; lon <= 175; lon += 10)
            {
                if (a.Sample(lat, lon).Terrain != b.Sample(lat, lon).Terrain) differences++;
            }
        }

        Assert.True(differences > 0, "不同種子應該產生不同的地形佈局");
    }

    private static Dictionary<TerrainType, int> SampleWholeSphere(TerrainGenerator generator, int latStep = 1, int lonStep = 2)
    {
        var counts = new Dictionary<TerrainType, int>();
        for (var lat = -90; lat <= 90; lat += latStep)
        {
            for (var lon = -180; lon < 180; lon += lonStep)
            {
                var terrain = generator.Sample(lat, lon).Terrain;
                counts[terrain] = counts.GetValueOrDefault(terrain) + 1;
            }
        }
        return counts;
    }

    [Fact]
    public void Sample_AcrossWholeSphere_ProducesRoughlyTwoLandmassesAndCommonTerrainTypesAppear()
    {
        var generator = new TerrainGenerator(seed: 42);
        var counts = SampleWholeSphere(generator);

        var oceanCount = OceanTerrains.Sum(t => counts.GetValueOrDefault(t));
        var landCount = LandTerrains.Sum(t => counts.GetValueOrDefault(t));

        Assert.True(oceanCount > 0);
        Assert.True(landCount > 0, "應該要有陸地地形");

        var totalSamples = counts.Values.Sum();
        var oceanRatio = oceanCount / (double)totalSamples;
        // 「大致分兩大洲、其餘都是海」：海洋應占多數但不是全部
        Assert.InRange(oceanRatio, 0.5, 0.95);

        // 常見地形（不靠稀有noise閾值觸發的）應該都出現過。
        foreach (var common in new[] { TerrainType.Grassland, TerrainType.Forest, TerrainType.Seawater, TerrainType.SnowField })
        {
            Assert.True(counts.GetValueOrDefault(common) > 0, $"{common} 應該要出現過");
        }
    }

    [Fact]
    public void Sample_RockLikeTerrain_HasHigherElevationThanVegetatedLand()
    {
        var generator = new TerrainGenerator(seed: 42);
        TerrainType[] rockLike = [TerrainType.Rock, TerrainType.Cliff, TerrainType.VolcanicRock];
        TerrainType[] vegetated = [TerrainType.Grassland, TerrainType.Forest, TerrainType.Shrubland];

        var rockElevations = new List<int>();
        var vegetatedElevations = new List<int>();

        for (var lat = -70; lat <= 70; lat += 2)
        {
            for (var lon = -180; lon < 180; lon += 3)
            {
                var sample = generator.Sample(lat, lon);
                if (rockLike.Contains(sample.Terrain)) rockElevations.Add(sample.ElevationLevel);
                else if (vegetated.Contains(sample.Terrain)) vegetatedElevations.Add(sample.ElevationLevel);
            }
        }

        Assert.NotEmpty(rockElevations);
        Assert.NotEmpty(vegetatedElevations);
        Assert.True(rockElevations.Average() > vegetatedElevations.Average());
    }

    [Fact]
    public void Sample_ElevationLevel_IsWithinDocumentedRange()
    {
        var generator = new TerrainGenerator(seed: 42);
        foreach (var (_, sample) in SampleAllPoints(generator))
        {
            Assert.InRange(sample.ElevationLevel, -500, 500);
        }
    }

    [Theory]
    [InlineData(TerrainType.Grassland, 0, 4)]
    [InlineData(TerrainType.Shrubland, 0, 3)]
    [InlineData(TerrainType.Forest, 0, 7)]
    [InlineData(TerrainType.Freshwater, 0, 6)]
    [InlineData(TerrainType.Seawater, 0, 6)]
    public void Sample_HeightCategory_IsWithinDocumentedRange(TerrainType terrain, int min, int max)
    {
        var generator = new TerrainGenerator(seed: 42);
        var found = false;

        foreach (var (_, sample) in SampleAllPoints(generator))
        {
            if (sample.Terrain != terrain) continue;
            found = true;
            Assert.InRange(sample.HeightCategory, min, max);
        }

        Assert.True(found, $"樣本中應該要出現過 {terrain}");
    }

    private static IEnumerable<((double Lat, double Lon), TerrainSample)> SampleAllPoints(TerrainGenerator generator)
    {
        for (var lat = -90; lat <= 90; lat += 1)
        {
            for (var lon = -180; lon < 180; lon += 2)
            {
                yield return ((lat, lon), generator.Sample(lat, lon));
            }
        }
    }
}
