using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Planet.Domain;

namespace Planet.Data;

/// <summary><see cref="ITileRepository"/> 的 EF Core + SQL Server 實作。</summary>
public class TileRepository : ITileRepository
{
    private readonly PlanetDbContext _db;
    private readonly NearbyQueryOptions _nearbyOptions;

    /// <summary>建立地塊資料存取物件。</summary>
    /// <param name="db">已設定好連線字串的資料庫內容。</param>
    /// <param name="nearbyOptions">附近地塊查詢的查詢框設定；省略時使用預設值。</param>
    public TileRepository(PlanetDbContext db, IOptions<NearbyQueryOptions>? nearbyOptions = null)
    {
        _db = db;
        _nearbyOptions = nearbyOptions?.Value ?? new NearbyQueryOptions();
    }

    /// <inheritdoc />
    public Task<bool> HasAnyTilesAsync(CancellationToken ct = default) =>
        _db.Tiles.AnyAsync(ct);

    /// <inheritdoc />
    public async Task ClearAllTilesAsync(CancellationToken ct = default)
    {
        var tableName = _db.Model.FindEntityType(typeof(Tile))!.GetTableName();
        // EF1002：tableName來自EF自己的model metadata（編譯期固定的映射結果），不是使用者輸入，
        // 沒有injection風險；改用ExecuteSqlAsync也解決不了，因為TABLE名稱是SQL identifier，
        // 本來就不能像一般的值那樣參數化（TRUNCATE TABLE [@p0]是語法錯誤）。
#pragma warning disable EF1002
        await _db.Database.ExecuteSqlRawAsync($"TRUNCATE TABLE [{tableName}]", ct);
#pragma warning restore EF1002
    }

    /// <inheritdoc />
    public async Task BulkInsertAsync(IEnumerable<Tile> tiles, Action<long>? onProgress = null, CancellationToken ct = default)
    {
        var connectionString = _db.Database.GetConnectionString()
            ?? throw new InvalidOperationException("找不到 PlanetDb 連線字串");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        using var bulkCopy = new SqlBulkCopy(connection)
        {
            DestinationTableName = _db.Model.FindEntityType(typeof(Tile))!.GetTableName(),
            BatchSize = 50_000,
            BulkCopyTimeout = 0, // 2000萬筆規模可能需要數分鐘，不設逾時上限
        };

        if (onProgress is not null)
        {
            // SqlBulkCopy內建的進度回呼，每寫入NotifyAfter筆就觸發一次，不用另外查資料庫算目前寫了多少筆。
            bulkCopy.NotifyAfter = 50_000;
            bulkCopy.SqlRowsCopied += (_, e) => onProgress(e.RowsCopied);
        }

        foreach (var columnName in TileDataReader.ColumnNames)
        {
            bulkCopy.ColumnMappings.Add(columnName, columnName);
        }

        using var reader = new TileDataReader(tiles);
        await bulkCopy.WriteToServerAsync(reader, ct);
    }

    /// <summary>
    /// 先用經緯度bounding box把候選集合縮小到能被 (Latitude,Longitude) 複合索引有效seek的範圍，
    /// 縮小後的小集合再依實際距離排序取前N筆。
    ///
    /// 這裡試過兩種更「正規」的做法，都不理想：
    /// 1. 沒有bounding box、直接對全部2000萬筆排序——全表掃描+排序，單次查詢7~8秒。
    /// 2. 加SQL Server geography型別的persisted computed欄位＋spatial index——結果查詢優化器
    ///    幾乎不會自動選用spatial index做「排序」用途（只有WHERE配合上限距離時才可能考慮），
    ///    就算用index hint強制使用，對這種「整顆星球均勻分布2000萬個點」的資料形狀，
    ///    產生的執行計畫反而是病態的（實測跑了2分鐘還沒完），已經整個撤掉了。
    ///
    /// 最後選定：緯度窗固定±1.1度（前端一次要載入2000格填滿傾斜視角，實測±1.1度在赤道約2400格、
    /// 極區約3400格，留有安全邊界）；經度窗依cos(緯度)放大（緯度越高，經線在球面上越收攏，固定經度
    /// 範圍涵蓋的實際面積越小，所以窗要放大來補償）。
    /// 但緯度超過±84度左右（cos&lt;0.1）時經度窗理論上要放到很大，這時乾脆整段經度都不篩——
    /// 反正那個緯度帶本身候選數就很小，全篩掃過去也很快。
    /// 實測（2026-08-30效能評估）：±1.1度查詢約60ms量級，遠比原本的7~8秒快；範圍再往上放大到
    /// 涵蓋全星球（2000萬格）在查詢時間、傳輸量、前端draw call數三個維度都完全不可行
    /// （分別是分鐘等級、3.7GB、2000萬次draw call），全星球總覽改用另一套低精度貼圖渲染
    /// （Planet.Service專案的 OverviewTextureGenerator），不走這個查詢路徑。
    /// </summary>
    /// <param name="latitude">查詢中心緯度（度）。</param>
    /// <param name="longitude">查詢中心經度（度）。</param>
    /// <param name="count">最多回傳幾筆，依實際距離由近到遠排序。</param>
    /// <param name="ct">取消權杖。</param>
    public async Task<IReadOnlyList<Tile>> GetTilesNearAsync(double latitude, double longitude, int count, CancellationToken ct = default)
    {
        count = Math.Clamp(count, 1, _nearbyOptions.MaxCount);
        var baseDeltaDegrees = _nearbyOptions.DeltaDegreesFor(count);

        var latRad = latitude * Math.PI / 180.0;
        var cosLat = Math.Cos(latRad);

        var latMin = Math.Max(latitude - baseDeltaDegrees, -90);
        var latMax = Math.Min(latitude + baseDeltaDegrees, 90);

        var candidates = _db.Tiles.Where(t => t.Latitude >= latMin && t.Latitude <= latMax);

        if (cosLat > 0.1)
        {
            var lonDelta = Math.Min(baseDeltaDegrees / cosLat, 180.0);
            var lonMin = longitude - lonDelta;
            var lonMax = longitude + lonDelta;

            // 經度窗跨過±180°接縫時（在換日線附近查詢）簡化為不篩經度，緯度窗仍能把候選集合縮小到位。
            if (lonMin >= -180 && lonMax <= 180)
            {
                candidates = candidates.Where(t => t.Longitude >= lonMin && t.Longitude <= lonMax);
            }
        }

        // 排序距離的兩個修正（原本直接用原始度數的平方差，兩處都會出錯）：
        //
        // 1. 換日線繞回：查詢點在經度179.9時，對面179.9°W(=-179.9)的格子算出來的Δ是359.8而不是0.2，
        //    整批被排到最後、被Take砍掉——patch會在換日線上被切掉半邊。
        //    先把經度差平移到[-180,180]再算。（T-SQL的%不吃float，用FLOOR做等效的取模。）
        // 2. 緯度扭曲：實際距離是 √(Δlat² + (cosφ·Δlon)²)，不乘cosφ的話高緯度會過度懲罰經度方向，
        //    選出來的範圍被拉成南北向的細長橢圓（φ=60°約2:1，φ=84°約10:1）。
        //
        // 這兩點在原本「每走一步就重抓」的用法下影響不大，但邊緣觸發式載入需要patch大致置中、
        // 大致等向，否則玩家會永遠卡在某一側的邊緣。
        var cosLatSquared = cosLat * cosLat;

        return await candidates
            .AsNoTracking()
            .OrderBy(t =>
                (t.Latitude - latitude) * (t.Latitude - latitude)
                + (t.Longitude - longitude + 180.0 - 360.0 * Math.Floor((t.Longitude - longitude + 180.0) / 360.0) - 180.0)
                * (t.Longitude - longitude + 180.0 - 360.0 * Math.Floor((t.Longitude - longitude + 180.0) / 360.0) - 180.0)
                * cosLatSquared)
            .Take(count)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public Task<Tile?> GetByIdAsync(long tileId, CancellationToken ct = default) =>
        _db.Tiles.AsNoTracking().FirstOrDefaultAsync(t => t.TileId == tileId, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Tile>> GetByIdsAsync(IReadOnlyCollection<long> tileIds, CancellationToken ct = default)
    {
        if (tileIds.Count == 0) return [];

        var ids = tileIds.ToArray();
        return await _db.Tiles.AsNoTracking().Where(t => ids.Contains(t.TileId)).ToListAsync(ct);
    }

    /// <inheritdoc />
    public Task<Tile?> GetByCoordinateAsync(int faceIndex, int row, int col, CancellationToken ct = default) =>
        _db.Tiles.AsNoTracking().FirstOrDefaultAsync(t => t.FaceIndex == faceIndex && t.Row == row && t.Col == col, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Tile>> GetByCoordinatesAsync(
        IReadOnlyCollection<(int FaceIndex, int Row, int Col)> coordinates,
        CancellationToken ct = default)
    {
        if (coordinates.Count == 0) return [];

        // 三個獨立的 IN 條件（而不是 (a,b,c) 的tuple IN——EF Core無法翻譯tuple的Contains，
        // 而把三個欄位打包成單一數值又會讓條件不sargable、退化成掃描2000萬列）。
        // 這樣可以直接seek現有的 (FaceIndex,Row,Col) 複合唯一索引。
        // 笛卡兒積會多撈一些不要的（鄰居座標彼此相鄰，最多約3×3×3=27列），最後在記憶體精確過濾。
        var faces = coordinates.Select(c => c.FaceIndex).Distinct().ToArray();
        var rows = coordinates.Select(c => c.Row).Distinct().ToArray();
        var cols = coordinates.Select(c => c.Col).Distinct().ToArray();

        // EF.Constant：讓EF產生字面值 IN (1,2,3) 而不是參數化的 OPENJSON——後者在SQL Server上
        // 基數估計很差，可能因此不走索引seek。這些值全部是伺服器端由鄰居演算法算出來的小整數，
        // 不是使用者輸入，沒有injection風險。
        var candidates = await _db.Tiles
            .AsNoTracking()
            .Where(t => EF.Constant(faces).Contains(t.FaceIndex)
                     && EF.Constant(rows).Contains(t.Row)
                     && EF.Constant(cols).Contains(t.Col))
            .ToListAsync(ct);

        var wanted = coordinates.ToHashSet();
        return candidates.Where(t => wanted.Contains((t.FaceIndex, t.Row, t.Col))).ToList();
    }
}
