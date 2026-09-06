namespace Planet.Service.Security;

/// <summary>
/// 管理端點（<c>/api/admin</c>）的存取金鑰設定。
///
/// 這些端點會 TRUNCATE 掉整張 Tiles 表（2,000萬列、無法復原），在此之前完全沒有任何保護，
/// 任何本機程序或開著網頁的人都能觸發。這裡加上一道共用金鑰。
///
/// 設定方式（**不要寫進 appsettings.json**，那個檔案會進版控）：
///   * 開發：<c>dotnet user-secrets set "Admin:ApiKey" "&lt;金鑰&gt;"</c>
///   * 服務：環境變數 <c>Admin__ApiKey</c>（NSSM 用 AppEnvironmentExtra 設定）
/// </summary>
public sealed class AdminApiKeyOptions
{
    /// <summary>設定區段名稱。</summary>
    public const string SectionName = "Admin";

    /// <summary>
    /// 呼叫管理端點時必須在 <c>X-Admin-Key</c> 標頭帶上的金鑰。
    ///
    /// **沒有設定時管理端點會整個停用（回 503），而不是變成不用驗證。**
    /// 這是刻意的：漏設定的後果應該是「功能用不了」，不是「大門敞開」——
    /// 後者正是這次要修掉的狀態。
    /// </summary>
    public string? ApiKey { get; init; }
}
