using System.Buffers.Binary;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Planet.Data;
using Planet.Domain;
using Planet.Generation;
using Planet.Service;
using Planet.Service.Controllers;
using Planet.Shared;

namespace Planet.Service.Tests;

/// <summary>
/// <see cref="TilesController"/> 的輸入驗證與回應格式。資料庫、模擬都不需要：倉儲換成假的，
/// 要驗的是「不合法的輸入在碰到資料庫之前就被擋下」與「前端帶哪個 Accept 就回哪種格式」。
/// </summary>
public class TilesControllerTests
{
    /// <summary>假的倉儲：記下 <c>GetTilesNearAsync</c> 收到的參數，回傳固定的一批地塊。</summary>
    private sealed class FakeTileRepository(IReadOnlyList<Tile> tiles) : ITileRepository
    {
        public List<(double Lat, double Lon, int Count)> NearbyCalls { get; } = [];

        public Task<IReadOnlyList<Tile>> GetTilesNearAsync(double latitude, double longitude, int count, CancellationToken ct = default)
        {
            NearbyCalls.Add((latitude, longitude, count));
            return Task.FromResult(tiles);
        }

        public Task<bool> HasAnyTilesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearAllTilesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task BulkInsertAsync(IEnumerable<Tile> tiles, Action<long>? onProgress = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReplaceAllWithSimulationAsync(IEnumerable<Tile> tiles, IReadOnlyList<PlanetCell> cells, Action<long>? onProgress = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearPlanetCellsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PlanetCell>> GetAllPlanetCellsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PlanetCell?> GetPlanetCellAsync(int cellId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Tile?> GetByIdAsync(long tileId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Tile>> GetByIdsAsync(IReadOnlyCollection<long> tileIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Tile?> GetByCoordinateAsync(int faceIndex, int row, int col, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Tile>> GetByCoordinatesAsync(IReadOnlyCollection<(int FaceIndex, int Row, int Col)> coordinates, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private const int Frequency = 3;

    private static (TilesController Controller, FakeTileRepository Repository) Create(string? accept = null)
    {
        var tiles = new GoldbergPlanetGenerator(Frequency).GenerateTiles().ToList();
        var repository = new FakeTileRepository(tiles);
        // 小格、粗格四季、總覽貼圖在這些測試的路徑上都不會被用到（驗證在它們之前就回了）
        var controller = new TilesController(repository, new TileNeighborResolver(Frequency), null!, null!, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        if (accept is not null) controller.Request.Headers.Accept = accept;
        return (controller, repository);
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.NaN)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(0, double.NegativeInfinity)]
    [InlineData(90.0001, 0)]
    [InlineData(-90.0001, 0)]
    public async Task Nearby_InvalidCoordinates_AreRejectedBeforeTheDatabase(double lat, double lon)
    {
        var (controller, repository) = Create();

        var result = await controller.GetNearby(lat, lon, 250);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(repository.NearbyCalls);
    }

    [Theory]
    [InlineData(190, -170)]
    [InlineData(-190, 170)]
    [InlineData(360, 0)]
    [InlineData(540, -180)]
    [InlineData(10, 10)]
    [InlineData(-180, -180)]
    public async Task Nearby_LongitudeIsWrappedIntoMinus180To180(double lon, double expected)
    {
        var (controller, repository) = Create();

        await controller.GetNearby(45, lon, 250);

        var call = Assert.Single(repository.NearbyCalls);
        Assert.Equal(expected, call.Lon, 9);
    }

    [Fact]
    public async Task Nearby_PassesLatitudeAndCountThrough()
    {
        var (controller, repository) = Create();

        await controller.GetNearby(-33.5, 20, 1234);

        Assert.Equal((-33.5, 20.0, 1234), Assert.Single(repository.NearbyCalls));
    }

    [Fact]
    public async Task Nearby_DefaultsToJson_WithNeighborIdsLinkedWithinThePatch()
    {
        var (controller, repository) = Create();

        var result = await controller.GetNearby(0, 0, 250);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dtos = Assert.IsAssignableFrom<IReadOnlyList<TileDto>>(ok.Value);
        Assert.Equal((await repository.GetTilesNearAsync(0, 0, 250)).Count, dtos.Count);
        // 整顆星球就是整個 patch：每格的鄰居都連得起來，數量符合形狀
        Assert.All(dtos, dto =>
        {
            Assert.NotNull(dto.NeighborIds);
            Assert.Equal(dto.Shape == "Pentagon" ? 5 : 6, dto.NeighborIds!.Length);
            Assert.All(dto.NeighborIds, id => Assert.NotNull(id));
        });
    }

    [Theory]
    [InlineData("application/vnd.planet.tiles")]
    [InlineData("application/vnd.planet.tiles, application/json;q=0.9")]
    public async Task Nearby_BinaryAccept_ReturnsTheBinaryFormat(string accept)
    {
        var (controller, repository) = Create(accept);

        var result = await controller.GetNearby(0, 0, 250);

        var file = Assert.IsType<FileContentResult>(result.Result);
        Assert.Equal(TileBinaryEncoder.ContentType, file.ContentType);
        Assert.Equal(0x31425450u, BinaryPrimitives.ReadUInt32LittleEndian(file.FileContents));
        var rows = BinaryPrimitives.ReadUInt32LittleEndian(file.FileContents.AsSpan(4));
        Assert.Equal((await repository.GetTilesNearAsync(0, 0, 250)).Count, (int)rows);
    }

    [Fact]
    public async Task SubTiles_RejectsMissingEmptyAndTooManyIds_BeforeTouchingTheService()
    {
        var (controller, _) = Create();

        Assert.IsType<BadRequestObjectResult>((await controller.GetSubTiles(new SubTileRequest(null!), default)).Result);
        Assert.IsType<BadRequestObjectResult>((await controller.GetSubTiles(new SubTileRequest([]), default)).Result);
        var tooMany = new long[SubTileService.MaxParentsPerRequest + 1];
        Assert.IsType<BadRequestObjectResult>((await controller.GetSubTiles(new SubTileRequest(tooMany), default)).Result);
    }

    [Fact]
    public async Task CellSeasons_RejectsMissingEmptyAndTooManyIds_BeforeTouchingTheService()
    {
        var (controller, _) = Create();

        Assert.IsType<BadRequestObjectResult>((await controller.GetCellSeasons(new CellSeasonsRequest(null!), default)).Result);
        Assert.IsType<BadRequestObjectResult>((await controller.GetCellSeasons(new CellSeasonsRequest([]), default)).Result);
        var tooMany = new int[CellSeasonsService.MaxCellsPerRequest + 1];
        Assert.IsType<BadRequestObjectResult>((await controller.GetCellSeasons(new CellSeasonsRequest(tooMany), default)).Result);
    }
}
