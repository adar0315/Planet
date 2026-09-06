namespace Planet.Service;

/// <summary>
/// 星球資料播種進度的共用狀態（Singleton），讓 <see cref="PlanetSeeder"/>（背景寫入）跟
/// <see cref="Controllers.StatusController"/>（前端輪詢查詢）之間傳遞進度，不需要每次查詢都
/// 額外打一次資料庫（SqlBulkCopy本身有進度回呼，見 Planet.Data 專案的 TileRepository.BulkInsertAsync）。
/// 所有屬性都用 <c>volatile</c>／簡單型別讀寫，因為只有一個背景執行緒會寫入、多個HTTP請求執行緒會讀取，
/// 不需要用到鎖（.NET的long/bool讀寫本身在x64上是atomic的，這裡的用途只是「盡快看到最新進度」，
/// 不要求精確的happens-before順序）。
/// </summary>
public class SeedingStatus
{
    /// <summary>目前是否正在背景生成中（true=正在生成，false=已完成或還沒開始判斷）。</summary>
    public volatile bool IsSeeding;

    /// <summary>資料是否已經可以查詢（資料庫本來就有資料，或是背景生成已經完成）。</summary>
    public volatile bool IsReady;

    /// <summary>已寫入的地塊數（背景生成期間即時更新）。</summary>
    public long TilesWritten;

    /// <summary>預期的地塊總數，生成開始時就從 <see cref="Generation.IPlanetGenerator.TotalTileCount"/> 取得，供前端算百分比用。</summary>
    public long TotalTiles;

    /// <summary>如果背景生成失敗，這裡會有錯誤訊息；成功或還在進行中則是null。</summary>
    public string? ErrorMessage;
}
