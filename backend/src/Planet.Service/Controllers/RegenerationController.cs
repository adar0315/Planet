using Microsoft.AspNetCore.Mvc;
using Planet.Generation;
using Planet.Service.Security;

namespace Planet.Service.Controllers;

/// <summary>
/// 星球地形生成參數的查詢／設定，以及手動觸發「清空並重新生成整顆星球」。這是開發用的工具端點，
/// 不是給一般玩家用的遊戲功能——重新生成會 truncate 掉現有的2,000萬格資料，且**無法復原**。
///
/// 因此整個控制器都要求 <c>X-Admin-Key</c> 標頭（見 <see cref="AdminApiKeyFilter"/>）。
/// 伺服器沒設定金鑰時這些端點一律回 503（停用），不會退化成不用驗證。
/// </summary>
[ApiController]
[Route("api/admin")]
[ServiceFilter(typeof(AdminApiKeyFilter))]
// 這兩個狀態碼對本控制器的每個action都適用（都由AdminApiKeyFilter產生），
// 掛在控制器層級即可，不必每個action重複一次。
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
public class RegenerationController : ControllerBase
{
    private readonly TerrainOptionsStore _optionsStore;
    private readonly PlanetRegenerationService _regenerationService;

    /// <summary>建立控制器。</summary>
    public RegenerationController(TerrainOptionsStore optionsStore, PlanetRegenerationService regenerationService)
    {
        _optionsStore = optionsStore;
        _regenerationService = regenerationService;
    }

    /// <summary>查詢目前的地形生成參數（下次「重新生成」會使用的參數，不一定等於目前資料庫裡星球實際的參數，如果查完之後還沒觸發過重新生成的話）。</summary>
    /// <returns>目前的地形生成參數。</returns>
    /// <response code="200">目前的地形生成參數。</response>
    [HttpGet("terrain-options")]
    [ProducesResponseType<TerrainGenerationOptions>(StatusCodes.Status200OK)]
    public ActionResult<TerrainGenerationOptions> GetTerrainOptions()
    {
        return Ok(_optionsStore.Current);
    }

    /// <summary>
    /// 更新地形生成參數。只是存起來，不會立刻改變目前的星球——要另外呼叫
    /// <see cref="Regenerate"/> 才會用新參數實際重新生成。
    /// </summary>
    /// <param name="options">新的地形生成參數（沒指定的欄位會用 <see cref="TerrainGenerationOptions"/> 的預設值，不是沿用目前的值，所以要整組一起送）。</param>
    /// <returns>更新後的地形生成參數（等同於回傳你剛送出的內容）。</returns>
    /// <response code="200">更新後的地形生成參數。</response>
    [HttpPut("terrain-options")]
    [ProducesResponseType<TerrainGenerationOptions>(StatusCodes.Status200OK)]
    public ActionResult<TerrainGenerationOptions> SetTerrainOptions([FromBody] TerrainGenerationOptions options)
    {
        _optionsStore.Set(options);
        return Ok(_optionsStore.Current);
    }

    /// <summary>
    /// 清空現有地圖資料，用目前的地形生成參數（<see cref="GetTerrainOptions"/> 查得到的那組）在背景重新生成整顆星球。
    /// 立刻回傳，不等待生成完成（實測2,000萬格約需15分鐘）——用 <c>GET /api/status</c> 輪詢進度。
    /// </summary>
    /// <returns>202：已受理，開始在背景生成；409：已經有生成作業在進行中。</returns>
    /// <response code="202">已受理，開始在背景生成。</response>
    /// <response code="409">已經有生成作業在進行中，請等它完成後再試一次。</response>
    [HttpPost("regenerate")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public IActionResult Regenerate()
    {
        if (_regenerationService.IsRunning)
        {
            return Conflict(new { message = "已經有星球生成作業正在進行中，請等它完成後再試一次。" });
        }

        _regenerationService.StartInBackground(_optionsStore.Current, clearExisting: true);
        return Accepted();
    }
}
