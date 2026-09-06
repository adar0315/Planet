using Microsoft.AspNetCore.Mvc;
using Planet.Simulation;

namespace Planet.Service.Controllers;

/// <summary>
/// 地質演化模擬（規劃見 docs/geological-simulation-plan.md）。
/// 目前是階段2：板塊運動、隱沒、增生、火山都已經在跑，從全海洋星球會長出大陸。
/// </summary>
[ApiController]
[Route("api/simulation")]
public class SimulationController : ControllerBase
{
    private readonly SimulationService _simulationService;

    /// <summary>建立控制器。</summary>
    /// <param name="simulationService">模擬狀態的持有者。</param>
    public SimulationController(SimulationService simulationService)
    {
        _simulationService = simulationService;
    }

    /// <summary>模擬目前的概況（網格規模、已跑年數）。</summary>
    /// <param name="ct">取消權杖。</param>
    /// <returns>網格規模與模擬進度。</returns>
    /// <response code="200">網格規模與模擬進度。</response>
    [HttpGet("status")]
    [ProducesResponseType<SimulationStatusDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SimulationStatusDto>> GetStatus(CancellationToken ct)
    {
        return Ok(await _simulationService.GetStatusAsync(ct));
    }

    /// <summary>推進模擬指定步數（每步的年數見概況的 <c>yearsPerStep</c>）。</summary>
    /// <param name="steps">要跑幾步，預設1步。</param>
    /// <param name="ct">取消權杖。</param>
    /// <returns>推進之後的概況。</returns>
    /// <response code="200">推進之後的概況。</response>
    /// <response code="400">步數不是正整數，或超過單次請求的上限。</response>
    [HttpPost("step")]
    [ProducesResponseType<SimulationStatusDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SimulationStatusDto>> Step(
        [FromQuery] int steps = 1,
        CancellationToken ct = default)
    {
        // 上限是為了不要讓單一請求把 Singleton 的鎖佔住太久（推進期間繪製請求會被擋住）。
        // 要跑很長的地質時間就多打幾次，前端本來就是逐格更新畫面。
        const int maxStepsPerRequest = 2000;
        if (steps <= 0 || steps > maxStepsPerRequest)
        {
            return Problem(
                detail: $"steps 必須介於 1 ~ {maxStepsPerRequest} 之間（收到 {steps}）。",
                statusCode: StatusCodes.Status400BadRequest);
        }

        return Ok(await _simulationService.StepAsync(steps, ct));
    }

    /// <summary>把模擬重設回起始狀態：平坦、裸露、全部被海水覆蓋的星球。</summary>
    /// <param name="ct">取消權杖。</param>
    /// <returns>重設之後的概況。</returns>
    /// <response code="200">重設之後的概況。</response>
    [HttpPost("reset")]
    [ProducesResponseType<SimulationStatusDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SimulationStatusDto>> Reset(CancellationToken ct)
    {
        _simulationService.Reset();
        return Ok(await _simulationService.GetStatusAsync(ct));
    }

    /// <summary>
    /// 目前模擬狀態的等距柱狀投影 RGBA 貼圖（1024×512×4 位元組，逐列由上到下、第0列＝北極）。
    ///
    /// 取樣慣例跟 <c>GET /api/tiles/overview-texture</c> 完全一致，前端可以套用同一顆球體與材質設定
    /// （記得 <c>DataTexture</c> 的 <c>flipY</c> 要設 true，見 docs/architecture.md）。
    /// </summary>
    /// <param name="field">要顯示哪個欄位，預設為地表高程。</param>
    /// <param name="ct">取消權杖。</param>
    /// <returns>RGBA 位元組。</returns>
    /// <response code="200">RGBA 位元組。</response>
    [HttpGet("texture")]
    [ProducesResponseType(typeof(byte[]), StatusCodes.Status200OK, "application/octet-stream")]
    public async Task<IActionResult> GetTexture(
        [FromQuery] SimulationField field = SimulationField.Elevation,
        CancellationToken ct = default)
    {
        var pixels = await _simulationService.RenderAsync(field, ct);
        return File(pixels, "application/octet-stream");
    }
}
