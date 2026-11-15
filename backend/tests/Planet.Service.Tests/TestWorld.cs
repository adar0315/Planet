using Microsoft.Extensions.DependencyInjection;
using Planet.Domain;
using Planet.Generation;
using Planet.Service;

namespace Planet.Service.Tests;

/// <summary>
/// 服務層測試共用的「世界」：正式頻率（1415）的真實地塊，加上用假倉儲組好的服務。
/// 小格幾何要用真的 (面, 列, 行) 才算得出來，所以地塊取自真正的產生器；地形（地表種類與海拔）由各測試自己設。
/// </summary>
internal static class TestWorld
{
    /// <summary>不是模擬取樣器的地形來源：服務會走「小格照抄所屬地塊」那條路。</summary>
    internal sealed class ProceduralSource : ITerrainGenerator
    {
        public TerrainSample Sample(double latitude, double longitude) => new(TerrainType.Grassland, 0, 10);
    }

    /// <summary>正式頻率的前 20 萬個地塊（第 0 面從頂點往下約 630 列）。只產生一次。</summary>
    internal static readonly Lazy<IReadOnlyList<Tile>> RealTiles = new(() =>
        new GoldbergPlanetGenerator(GoldbergPlanetGenerator.SubdivisionFrequency, new ProceduralSource())
            .GenerateTiles().Take(200_000).ToList());

    /// <summary>複製地塊並換掉地形欄位。</summary>
    internal static Tile Copy(Tile tile, TerrainType terrain, int heightCategory, int elevationLevel, int seaLevel) => new()
    {
        TileId = tile.TileId,
        Shape = tile.Shape,
        FaceIndex = tile.FaceIndex,
        Row = tile.Row,
        Col = tile.Col,
        Latitude = tile.Latitude,
        Longitude = tile.Longitude,
        Terrain = terrain,
        HeightCategory = heightCategory,
        ElevationLevel = elevationLevel,
        SeaLevel = seaLevel,
    };

    /// <summary>一小片相連的陸地地塊（第 0 面第 40～45 列附近），每塊的海拔不同，方便認出小格抄的是哪一塊。</summary>
    internal static List<Tile> LandPatch() => RealTiles.Value
        .Where(t => t.Row is >= 40 and <= 45 && t.Col <= 6)
        .Select((t, i) => Copy(t, TerrainType.Grassland, 1, 100 + i, 0))
        .ToList();

    /// <summary>同一片位置的海：海床 -50 級、海面 0 級。</summary>
    internal static List<Tile> SeaPatch() => RealTiles.Value
        .Where(t => t.Row is >= 40 and <= 45 && t.Col <= 6)
        .Select(t => Copy(t, TerrainType.Seawater, 6, -50, 0))
        .ToList();

    /// <summary>用假倉儲組好的小格服務。</summary>
    internal sealed record Setup(SubTileService Service, FakeConstructionRepository Constructions)
    {
        /// <summary>同一組假倉儲的 scope 工廠，要組別的服務（例如 <see cref="ConstructionService"/>）時用。</summary>
        public required IServiceScopeFactory Scopes { get; init; }

        /// <summary>用同一組假倉儲與小格服務組出建造服務。</summary>
        public ConstructionService Construction() => new(Scopes, Service);
    }

    /// <summary>組出小格服務；不給地形來源時用程序化的（小格照抄所屬地塊）。</summary>
    internal static Setup Create(IReadOnlyList<Tile> tiles, ITerrainGenerator? source = null)
    {
        var constructions = new FakeConstructionRepository();
        var scopes = FakeScopes.For(new FakeTileRepository(tiles), constructions);
        var terrain = source ?? new ProceduralSource();
        return new Setup(new SubTileService(scopes, _ => Task.FromResult(terrain), new SubTileGeometryCache()), constructions)
        {
            Scopes = scopes,
        };
    }
}
