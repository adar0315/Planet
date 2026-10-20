using Microsoft.AspNetCore.Mvc;
using Planet.Simulation;

namespace Planet.Service.Controllers;

/// <summary>存檔的結果。</summary>
/// <param name="Name">存檔名稱。</param>
/// <param name="SizeBytes">檔案大小（位元組）。</param>
public record SaveResultDto(string Name, long SizeBytes);

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

    /// <summary>
    /// 把又小又矮的孤立碎島沉回水面下。
    ///
    /// 這是**交付地形前的表面處理**，不是模擬迴圈的一部分：碎島的成因是標記重新取樣
    /// 造成的海陸翻面（取樣解析度的產物），繼續推進模擬又會長出新的，所以要在匯出前呼叫。
    /// </summary>
    /// <param name="ct">取消權杖。</param>
    /// <returns>清了幾塊、留了幾塊，以及留下來的原因。</returns>
    /// <response code="200">清理結果。</response>
    [HttpPost("clean-small-islands")]
    [ProducesResponseType<SmallIslandCleanupReport>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SmallIslandCleanupReport>> CleanSmallIslands(CancellationToken ct)
    {
        return Ok(await _simulationService.CleanUpSmallIslandsAsync(ct));
    }

    /// <summary>
    /// 把目前的模擬狀態存成檔案。
    ///
    /// 模擬只活在記憶體裡，服務一重啟就沒了，而跑到接近地球的海陸比要四到五分鐘。
    /// **存檔不含 marker-in-cell 的標記**（讀回來時從格子狀態重建），
    /// 所以續跑不會與未中斷的執行位元相同，詳見 <see cref="SimulationSnapshot"/>。
    /// </summary>
    /// <param name="name">存檔名稱，只能用英數、底線與連字號。</param>
    /// <param name="ct">取消權杖。</param>
    /// <returns>存檔的位元組數。</returns>
    /// <response code="200">存檔完成。</response>
    /// <response code="400">名稱不合規。</response>
    [HttpPost("save")]
    [ProducesResponseType<SaveResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SaveResultDto>> Save([FromQuery] string name, CancellationToken ct)
    {
        try
        {
            var bytes = await _simulationService.SaveAsync(name, ct);
            return Ok(new SaveResultDto(name, bytes));
        }
        catch (ArgumentException ex)
        {
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>從存檔把模擬狀態讀回來，**覆蓋目前記憶體裡的星球**。</summary>
    /// <param name="name">存檔名稱。</param>
    /// <param name="ct">取消權杖。</param>
    /// <returns>讀回來之後的概況。</returns>
    /// <response code="200">讀取完成。</response>
    /// <response code="400">名稱不合規，或存檔的網格跟目前的對不上。</response>
    /// <response code="404">找不到這個存檔。</response>
    [HttpPost("load")]
    [ProducesResponseType<SimulationStatusDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SimulationStatusDto>> Load([FromQuery] string name, CancellationToken ct)
    {
        try
        {
            return Ok(await _simulationService.LoadAsync(name, ct));
        }
        catch (FileNotFoundException ex)
        {
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        {
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>列出所有存檔（最新的在前）。</summary>
    /// <returns>每個存檔的名稱、大小與存檔時間。</returns>
    /// <response code="200">存檔清單。</response>
    [HttpGet("saves")]
    [ProducesResponseType<IReadOnlyList<SnapshotInfo>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<SnapshotInfo>> ListSaves()
    {
        return Ok(_simulationService.ListSnapshots());
    }

    /// <summary>
    /// 刪除一個存檔（連同它的說明檔）。不可復原。
    /// </summary>
    /// <param name="name">存檔名稱。</param>
    /// <response code="204">刪掉了。</response>
    /// <response code="400">名稱不合規。</response>
    /// <response code="404">找不到這個存檔。</response>
    [HttpDelete("saves/{name}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public IActionResult DeleteSave(string name)
    {
        try
        {
            _simulationService.DeleteSnapshot(name);
            return NoContent();
        }
        catch (FileNotFoundException ex)
        {
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (ArgumentException ex)
        {
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>海洋的連通性：內海有沒有被陸地封死、水域裡有幾條獨立環路、最窄的水道多寬。</summary>
    /// <param name="ct">取消權杖。</param>
    /// <returns>連通性報告。</returns>
    /// <response code="200">連通性報告。</response>
    [HttpGet("ocean-topology")]
    [ProducesResponseType<OceanTopologyReport>(StatusCodes.Status200OK)]
    public async Task<ActionResult<OceanTopologyReport>> GetOceanTopology(CancellationToken ct)
    {
        return Ok(await _simulationService.AnalyzeOceanAsync(ct));
    }

    /// <summary>
    /// 固定儀表：概況、陸地配置、海洋連通性、高度分布、陸塊大小分布、各機制觸發量、每步耗時、狀態雜湊。
    /// </summary>
    /// <param name="resetTimings">回傳後把每步耗時歸零（例如剛改完一個機制，想重新量它的負擔）。</param>
    /// <param name="ct">取消權杖。</param>
    /// <returns>儀表的全部數字。</returns>
    /// <response code="200">儀表的全部數字。</response>
    [HttpGet("dashboard")]
    [ProducesResponseType<SimulationDashboardDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SimulationDashboardDto>> GetDashboard(
        [FromQuery] bool resetTimings = false, CancellationToken ct = default)
    {
        return Ok(await _simulationService.GetDashboardAsync(resetTimings, ct));
    }
}
