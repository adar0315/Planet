using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Planet.Domain;

namespace Planet.Data.Tests;

/// <summary>
/// <see cref="TileRepository"/> 的查詢邏輯，用記憶體資料庫跑同一段 LINQ。
///
/// 記憶體資料庫不會驗證 SQL（索引、執行計畫、TOP 要不要寫成常數這些都測不到，那些靠量測），
/// 它驗的是「挑出哪些地塊、什麼順序」：附近地塊的距離公式是自己手刻的（換日線繞回、依緯度縮放經度差、
/// 極區不篩經度），每一條都曾經是實際出過錯的地方，而且錯了不會有例外，只會讓畫面上的 patch 缺一塊或歪一邊。
/// 做法是在測試裡用獨立寫的暴力算法算出「最近的 N 個」，跟倉儲的結果逐筆比對。
/// </summary>
public class TileRepositoryTests : IDisposable
{
    private readonly PlanetDbContext _db;
    private readonly TileRepository _repository;
    private long _nextId = 1;

    public TileRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<PlanetDbContext>()
            .UseInMemoryDatabase("tiles-" + Guid.NewGuid().ToString("N"))
            .Options;
        _db = new PlanetDbContext(options);
        _repository = new TileRepository(_db, Options.Create(new NearbyQueryOptions()));
    }

    public void Dispose() => _db.Dispose();

    private Tile AddTile(double latitude, double longitude, int face = 0, int row = 0, int col = 0)
    {
        var tile = new Tile
        {
            TileId = _nextId++,
            Shape = TileShape.Hexagon,
            FaceIndex = face,
            Row = row,
            Col = col,
            Latitude = latitude,
            Longitude = longitude,
        };
        _db.Tiles.Add(tile);
        return tile;
    }

    /// <summary>在經緯度的矩形範圍內撒點（固定種子，位置不規則所以不會有距離完全相等的平手）。</summary>
    private List<Tile> Scatter(int count, double latMin, double latMax, double lonMin, double lonMax, int seed = 7)
    {
        var random = new Random(seed);
        var tiles = new List<Tile>();
        for (var i = 0; i < count; i++)
        {
            tiles.Add(AddTile(
                latMin + random.NextDouble() * (latMax - latMin),
                lonMin + random.NextDouble() * (lonMax - lonMin)));
        }
        _db.SaveChanges();
        return tiles;
    }

    /// <summary>
    /// 獨立的暴力算法：實際的球面距離近似是 √(Δlat² + (cosφ·Δlon)²)，其中 Δlon 要先繞回 [-180, 180]。
    /// 刻意不照倉儲裡的運算式抄（那是在驗自己），而是照這個定義重寫一遍。
    /// </summary>
    private static List<long> Nearest(IEnumerable<Tile> all, double lat, double lon, int count)
    {
        var cos = Math.Cos(lat * Math.PI / 180);
        return all
            .Select(t =>
            {
                var dLon = t.Longitude - lon;
                while (dLon > 180) dLon -= 360;
                while (dLon < -180) dLon += 360;
                var dLat = t.Latitude - lat;
                return (t.TileId, Distance: Math.Sqrt(dLat * dLat + cos * cos * dLon * dLon));
            })
            .OrderBy(x => x.Distance)
            .Take(count)
            .Select(x => x.TileId)
            .ToList();
    }

    [Fact]
    public async Task Nearby_ReturnsTheNearestN_InOrder()
    {
        var all = Scatter(2000, 9, 11, 19, 21);

        var result = await _repository.GetTilesNearAsync(10, 20, 100);

        Assert.Equal(Nearest(all, 10, 20, 100), result.Select(t => t.TileId).ToList());
    }

    [Theory]
    [InlineData(179.9)] // 查詢點在換日線西側
    [InlineData(-179.9)] // 查詢點在換日線東側
    [InlineData(180)]
    public async Task Nearby_AcrossTheDateLine_PicksTilesFromBothSides(double queryLongitude)
    {
        // 點分布在 ±180° 兩側各 1.2°。原本的 bug：把 179.9 與 -179.9 當成差 359.8°，整批對面的點被排到最後砍掉
        var east = Scatter(1000, 9, 11, 178.8, 180, seed: 1);
        var west = Scatter(1000, 9, 11, -180, -178.8, seed: 2);

        var result = await _repository.GetTilesNearAsync(10, queryLongitude, 200);

        var expected = Nearest(east.Concat(west), 10, queryLongitude, 200);
        Assert.Equal(expected, result.Select(t => t.TileId).ToList());
        Assert.Contains(result, t => t.Longitude > 0);
        Assert.Contains(result, t => t.Longitude < 0);
    }

    [Fact]
    public async Task Nearby_AtHighLatitude_MeasuresLongitudeInRealDistance()
    {
        // 緯度 60°：經度 1° 只有緯度 1° 的一半長。不乘 cos 的話，選出來的範圍會是南北向的細長橢圓
        var all = Scatter(3000, 58, 62, 18, 22);

        var result = await _repository.GetTilesNearAsync(60, 20, 150);

        Assert.Equal(Nearest(all, 60, 20, 150), result.Select(t => t.TileId).ToList());
        var latSpread = result.Max(t => t.Latitude) - result.Min(t => t.Latitude);
        var lonSpread = result.Max(t => t.Longitude) - result.Min(t => t.Longitude);
        Assert.InRange(lonSpread / latSpread, 1.4, 2.8); // 約 2：經度方向要比緯度方向寬
    }

    [Fact]
    public async Task Nearby_NearThePole_DoesNotFilterByLongitude()
    {
        // 緯度超過 ±84° 時經度窗理論上要放到整圈：點分布在整圈經度上
        var all = Scatter(3000, 88, 90, -180, 180);

        var result = await _repository.GetTilesNearAsync(89, 30, 100);

        Assert.Equal(Nearest(all, 89, 30, 100), result.Select(t => t.TileId).ToList());
    }

    [Fact]
    public async Task Nearby_NeverReturnsTilesOutsideTheQueryBox_EvenWhenAskedForMore()
    {
        var near = Scatter(100, 9.5, 10.5, 19.5, 20.5);
        var far = Scatter(100, 20, 21, 30, 31, seed: 3); // 離查詢點 10° 以上，超過最大的查詢框 7°

        var result = await _repository.GetTilesNearAsync(10, 20, 60_000);

        Assert.Equal(near.Count, result.Count);
        Assert.DoesNotContain(result, t => far.Any(f => f.TileId == t.TileId));
    }

    [Theory]
    [InlineData(0, 1)] // 低於下限：夾到 1，不是回傳空或丟例外
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(100_000, 500)] // 高於上限：夾到上限，這裡只有 500 筆所以全回傳
    public async Task Nearby_CountIsClampedIntoRange(int requested, int expectedRows)
    {
        Scatter(500, 9.5, 10.5, 19.5, 20.5);

        var result = await _repository.GetTilesNearAsync(10, 20, requested);

        Assert.Equal(expectedRows, result.Count);
    }

    [Fact]
    public async Task GetByIds_ReturnsOnlyTheRequestedOnes_AndNothingForAnEmptyRequest()
    {
        var tiles = Scatter(20, 0, 1, 0, 1);

        var result = await _repository.GetByIdsAsync([tiles[3].TileId, tiles[7].TileId, 999_999]);

        Assert.Equal([tiles[3].TileId, tiles[7].TileId], result.Select(t => t.TileId).OrderBy(i => i).ToArray());
        Assert.Empty(await _repository.GetByIdsAsync([]));
    }

    [Fact]
    public async Task GetByCoordinates_FiltersTheCartesianProductDownToTheExactPairs()
    {
        // 倉儲用三個獨立的 IN 才能走索引，代價是會多撈到笛卡兒積的列，最後要在記憶體裡精確過濾。
        // 要 (0,1,1) 與 (0,2,2)，就不能把 (0,1,2) 與 (0,2,1) 也回傳。
        foreach (var (row, col) in new[] { (1, 1), (1, 2), (2, 1), (2, 2) })
        {
            AddTile(0, 0, face: 0, row: row, col: col);
        }
        _db.SaveChanges();

        var result = await _repository.GetByCoordinatesAsync([(0, 1, 1), (0, 2, 2)]);

        Assert.Equal([(1, 1), (2, 2)], result.Select(t => (t.Row, t.Col)).OrderBy(x => x).ToArray());
        Assert.Empty(await _repository.GetByCoordinatesAsync([]));
    }
}
