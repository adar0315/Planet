using Planet.Domain;

namespace Planet.Data;

/// <summary>地塊資料的存取介面，封裝所有對 Tiles 資料表的讀寫操作。</summary>
public interface ITileRepository
{
    /// <summary>檢查資料庫裡是否已經有任何地塊資料（服務啟動時用來判斷是否需要重新生成整個星球）。</summary>
    Task<bool> HasAnyTilesAsync(CancellationToken ct = default);

    /// <summary>清空所有地塊資料（用TRUNCATE，比逐筆DELETE快很多），供「重新生成整個星球」功能使用。</summary>
    /// <param name="ct">取消權杖。</param>
    Task ClearAllTilesAsync(CancellationToken ct = default);

    /// <summary>首次啟動生成整個星球時，用批次寫入（SqlBulkCopy）取代逐筆 EF Core Insert。</summary>
    /// <param name="tiles">要寫入的地塊集合（通常是串流產生，不會整批留在記憶體）。</param>
    /// <param name="onProgress">選填，每寫入一定筆數（見實作的NotifyAfter設定）就回呼一次目前累計寫入筆數，供顯示進度用。</param>
    /// <param name="ct">取消權杖。</param>
    Task BulkInsertAsync(IEnumerable<Tile> tiles, Action<long>? onProgress = null, CancellationToken ct = default);

    /// <summary>
    /// 用地質模擬放大出來的地形**整批替換**所有地塊與粗格記錄。
    ///
    /// 先寫進暫存表、建好索引，最後在一個交易裡用改名一次切換——寫入的十幾分鐘裡舊星球照常可以走，
    /// 中途失敗也不會留下一半新一半舊的地圖（舊表要到切換成功之後才刪）。
    /// </summary>
    /// <param name="tiles">新的全部地塊（串流產生）。</param>
    /// <param name="cells">新的全部粗格記錄。</param>
    /// <param name="onProgress">選填，地塊寫入進度（累計筆數）。</param>
    /// <param name="ct">取消權杖。</param>
    Task ReplaceAllWithSimulationAsync(
        IEnumerable<Tile> tiles, IReadOnlyList<PlanetCell> cells,
        Action<long>? onProgress = null, CancellationToken ct = default);

    /// <summary>清空粗格記錄（換回程序化地形時用，否則會留著一份跟地塊對不上的地質狀態）。</summary>
    /// <param name="ct">取消權杖。</param>
    Task ClearPlanetCellsAsync(CancellationToken ct = default);

    /// <summary>讀出全部粗格記錄，依 <see cref="PlanetCell.CellId"/> 排序。</summary>
    /// <param name="ct">取消權杖。</param>
    Task<IReadOnlyList<PlanetCell>> GetAllPlanetCellsAsync(CancellationToken ct = default);

    /// <summary>查一個粗格的完整狀態，沒有就回傳 null。</summary>
    /// <param name="cellId">粗格索引。</param>
    /// <param name="ct">取消權杖。</param>
    Task<PlanetCell?> GetPlanetCellAsync(int cellId, CancellationToken ct = default);

    /// <summary>查詢指定經緯度附近最近的地塊，依距離由近到遠排序。</summary>
    /// <param name="latitude">緯度（度）。</param>
    /// <param name="longitude">經度（度）。</param>
    /// <param name="count">最多回傳幾筆。</param>
    /// <param name="ct">取消權杖。</param>
    Task<IReadOnlyList<Tile>> GetTilesNearAsync(double latitude, double longitude, int count, CancellationToken ct = default);

    /// <summary>依 TileId 查詢單一地塊，查無資料回傳 null。</summary>
    Task<Tile?> GetByIdAsync(long tileId, CancellationToken ct = default);

    /// <summary>一次查詢多個 TileId（單次查詢，取代逐筆 <see cref="GetByIdAsync"/> 的多次來回）。</summary>
    /// <param name="tileIds">要查詢的地塊Id集合。</param>
    /// <param name="ct">取消權杖。</param>
    /// <returns>查到的地塊（順序不保證，查不到的Id不會出現在結果中）。</returns>
    Task<IReadOnlyList<Tile>> GetByIdsAsync(IReadOnlyCollection<long> tileIds, CancellationToken ct = default);

    /// <summary>供 ITileNeighborResolver 算出鄰居座標後，換回實際存在資料庫的Tile（含TileId）。</summary>
    /// <param name="faceIndex">正二十面體面編號。</param>
    /// <param name="row">面內細分座標（列）。</param>
    /// <param name="col">面內細分座標（欄）。</param>
    /// <param name="ct">取消權杖。</param>
    Task<Tile?> GetByCoordinateAsync(int faceIndex, int row, int col, CancellationToken ct = default);

    /// <summary>
    /// 一次查詢多組 (FaceIndex,Row,Col) 座標（單次查詢，取代逐筆 <see cref="GetByCoordinateAsync"/>）。
    /// 查一個Tile的6個鄰居原本要打6次DB，改用這支只要1次。
    /// </summary>
    /// <param name="coordinates">要查詢的座標組合。</param>
    /// <param name="ct">取消權杖。</param>
    /// <returns>查到的地塊（順序不保證，查不到的座標不會出現在結果中）。</returns>
    Task<IReadOnlyList<Tile>> GetByCoordinatesAsync(
        IReadOnlyCollection<(int FaceIndex, int Row, int Col)> coordinates,
        CancellationToken ct = default);
}
