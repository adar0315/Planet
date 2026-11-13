using Microsoft.AspNetCore.Mvc;
using Planet.Shared;

namespace Planet.Service.Controllers;

/// <summary>編輯模式（2026-11-08）：在小格上蓋房子、城堡、橋、路，挖河道、填平，或拆除。</summary>
[ApiController]
[Route("api/constructions")]
public class ConstructionsController : ControllerBase
{
    /// <summary>處理編輯模式修改的服務。</summary>
    private readonly ConstructionService _constructions;

    /// <summary>建立控制器。</summary>
    public ConstructionsController(ConstructionService constructions) => _constructions = constructions;

    /// <summary>
    /// 對一個小格做一件事。規則：房子、城堡、路只能蓋在陸地，橋只能蓋在水上；挖河道要在沒有建築物的陸地，
    /// 填平要在沒有橋的水上；對改過的地形做相反的事等於復原。拆除先拆建築物，沒有建築物才把地形改回原樣。
    /// 只改畫面，不重算水系。
    /// </summary>
    /// <param name="request">小格與動作。</param>
    /// <param name="ct">取消權杖。</param>
    /// <returns>修改之後的狀態。</returns>
    /// <response code="200">修改之後的狀態；<c>terrainChanged</c> 為 true 時要重新要一次小格。</response>
    /// <response code="400">不符合規則（內容是給玩家看的原因）。</response>
    /// <response code="404">找不到這個小格。</response>
    [HttpPost]
    [ProducesResponseType<ConstructionResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConstructionResultDto>> Apply([FromBody] ConstructionRequest request, CancellationToken ct)
    {
        var (result, error, notFound) = await _constructions.ApplyAsync(request, ct);
        if (notFound) return NotFound(error);
        if (result is null) return BadRequest(error);
        return Ok(result);
    }
}
