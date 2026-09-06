using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace Planet.Service.Security;

/// <summary>
/// 檢查管理端點的 <c>X-Admin-Key</c> 標頭。掛在 <see cref="Controllers.RegenerationController"/> 上。
///
/// 這個專案沒有完整的認證機制（單人原型），也不需要——真正要擋的是
/// 「一個不可復原的破壞性操作可以被任何人／任何腳本無條件觸發」。共用金鑰對這個威脅足夠，
/// 而且不必為了它引入使用者系統。
/// </summary>
public sealed class AdminApiKeyFilter : IAsyncActionFilter
{
    /// <summary>呼叫端要帶的標頭名稱。</summary>
    public const string HeaderName = "X-Admin-Key";

    private readonly AdminApiKeyOptions _options;
    private readonly ILogger<AdminApiKeyFilter> _logger;

    /// <summary>建立過濾器。</summary>
    public AdminApiKeyFilter(IOptions<AdminApiKeyOptions> options, ILogger<AdminApiKeyFilter> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // 沒設定金鑰＝管理功能停用（fail closed）。絕對不能變成「沒設定就不檢查」，
        // 否則漏設定的後果就是整個管理API對外敞開，跟修之前一樣糟。
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogWarning(
                "管理端點被呼叫但尚未設定 {Section}:ApiKey，已拒絕。設定方式見 AdminApiKeyOptions 的說明。",
                AdminApiKeyOptions.SectionName);

            context.Result = new ObjectResult(new
            {
                message = "管理功能未啟用：伺服器尚未設定管理金鑰（Admin:ApiKey）。",
            })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable,
            };
            return;
        }

        var provided = context.HttpContext.Request.Headers[HeaderName].ToString();
        if (!IsValidKey(provided, _options.ApiKey))
        {
            _logger.LogWarning(
                "管理端點驗證失敗（來源 {RemoteIp}）。",
                context.HttpContext.Connection.RemoteIpAddress);

            context.Result = new ObjectResult(new
            {
                message = $"管理金鑰錯誤或未提供，請在 {HeaderName} 標頭帶上正確的金鑰。",
            })
            {
                StatusCode = StatusCodes.Status401Unauthorized,
            };
            return;
        }

        await next();
    }

    /// <summary>
    /// 固定時間比對，避免用回應時間差一個字元一個字元猜出金鑰。
    /// 長度不同時仍會提早回傳（長度本來就無法隱藏），但相同長度下不會洩漏是第幾個字元不符。
    /// </summary>
    private static bool IsValidKey(string provided, string expected)
    {
        if (string.IsNullOrEmpty(provided)) return false;

        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return providedBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}
