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

    /// <summary>保護 <see cref="_exclusiveOwner"/> 的鎖。</summary>
    private readonly Lock _exclusiveLock = new();

    /// <summary>目前佔著「整個換掉地塊表」位置的工作名稱；沒有的話是 null。</summary>
    private string? _exclusiveOwner;

    /// <summary>
    /// 目前佔著位置的工作（「重新生成」或「放大存檔」），沒有的話是 null。
    ///
    /// 這兩件工作都會整個換掉地塊表，同時跑會互相覆蓋。原本各自用自己的鎖、各自看對方的旗標
    /// （重新生成在控制器先看放大有沒有在跑、放大在自己的鎖裡看重新生成的旗標），兩個幾乎同時的請求可能都通過。
    /// 現在兩邊都在這同一把鎖裡搶同一個位置（2026-11-15）。
    /// </summary>
    public string? ExclusiveOwner
    {
        get
        {
            lock (_exclusiveLock) return _exclusiveOwner;
        }
    }

    /// <summary>
    /// 佔用「整個換掉地塊表」的位置。已經有別的工作佔著時回傳 false（檢查與佔用是同一個原子動作）。
    /// 成功的話，工作結束時（成功或失敗都一樣）一定要呼叫 <see cref="EndExclusive"/>。
    /// </summary>
    /// <param name="owner">工作名稱，記錄誰佔著。</param>
    /// <param name="onAcquired">佔到位置之後、放開鎖之前要做的事（例如把進度狀態設成「進行中」），讓別人看到的狀態跟位置一致。</param>
    public bool TryBeginExclusive(string owner, Action? onAcquired = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        lock (_exclusiveLock)
        {
            if (_exclusiveOwner is not null) return false;
            _exclusiveOwner = owner;
            onAcquired?.Invoke();
            return true;
        }
    }

    /// <summary>放開位置。只有佔著的那件工作能放；放錯（不是自己佔的）就丟例外，那是程式寫錯了。</summary>
    /// <param name="owner">當初佔用時的工作名稱。</param>
    public void EndExclusive(string owner)
    {
        lock (_exclusiveLock)
        {
            if (_exclusiveOwner != owner)
                throw new InvalidOperationException($"「{owner}」要放開的位置現在是「{_exclusiveOwner ?? "沒有人"}」佔著。");
            _exclusiveOwner = null;
        }
    }
}
