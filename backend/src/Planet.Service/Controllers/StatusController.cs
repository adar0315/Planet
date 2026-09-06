using Microsoft.AspNetCore.Mvc;

namespace Planet.Service.Controllers;

/// <summary>星球資料的準備狀態，主要供前端在服務剛啟動、還在背景生成整個星球時顯示進度用。</summary>
[ApiController]
[Route("api/status")]
public class StatusController : ControllerBase
{
    private readonly SeedingStatus _status;

    /// <summary>建立控制器。</summary>
    public StatusController(SeedingStatus status)
    {
        _status = status;
    }

    /// <summary>查詢星球資料目前的生成狀態。</summary>
    /// <returns>目前的生成狀態。</returns>
    /// <response code="200">目前的生成狀態。</response>
    [HttpGet]
    [ProducesResponseType<SeedingStatusDto>(StatusCodes.Status200OK)]
    public ActionResult<SeedingStatusDto> Get()
    {
        return Ok(new SeedingStatusDto(
            _status.IsReady,
            _status.IsSeeding,
            _status.TilesWritten,
            _status.TotalTiles,
            _status.ErrorMessage));
    }
}

/// <summary>星球資料生成狀態的回應內容。</summary>
/// <param name="IsReady">資料是否已經可以查詢（原本就有資料，或背景生成已完成）。</param>
/// <param name="IsSeeding">是否正在背景生成中。</param>
/// <param name="TilesWritten">已寫入的地塊數（IsSeeding時才有意義，即時更新）。</param>
/// <param name="TotalTiles">預期的地塊總數，供算百分比用。</param>
/// <param name="ErrorMessage">如果背景生成失敗，這裡會有錯誤訊息；否則是null。</param>
public record SeedingStatusDto(bool IsReady, bool IsSeeding, long TilesWritten, long TotalTiles, string? ErrorMessage);
