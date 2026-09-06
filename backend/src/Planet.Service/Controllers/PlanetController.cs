using Microsoft.AspNetCore.Mvc;
using Planet.Generation;
using Planet.Shared;

namespace Planet.Service.Controllers;

/// <summary>星球本身的基本資訊（尺寸、地塊總數、貼圖規格），供前端取代寫死的常數。</summary>
[ApiController]
[Route("api/planet")]
public class PlanetController : ControllerBase
{
    private readonly PlanetMetadataService _metadataService;

    /// <summary>建立控制器。</summary>
    public PlanetController(PlanetMetadataService metadataService)
    {
        _metadataService = metadataService;
    }

    /// <summary>
    /// 查詢目前這顆星球的基本資訊。前端啟動時讀一次，用來換算Tile在場景中的大小、
    /// 以及配置總覽貼圖的緩衝區。
    /// </summary>
    /// <param name="ct">取消權杖。</param>
    /// <returns>星球基本資訊。資料庫還沒有星球時，回傳的是依目前設定推算出來的值。</returns>
    /// <response code="200">星球基本資訊。</response>
    [HttpGet]
    [ProducesResponseType<PlanetInfoDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PlanetInfoDto>> Get(CancellationToken ct)
    {
        var metadata = await _metadataService.GetAsync(ct);

        if (metadata is not null)
        {
            return Ok(new PlanetInfoDto(
                metadata.RadiusMeters,
                metadata.TileEdgeLengthMeters,
                metadata.SubdivisionFrequency,
                metadata.TotalTileCount,
                OverviewTextureGenerator.Width,
                OverviewTextureGenerator.Height,
                OverviewTextureGenerator.MaskWidth,
                OverviewTextureGenerator.MaskHeight,
                metadata.GeneratedAtUtc,
                metadata.IsInferred));
        }

        // 還沒有星球（全新的空資料庫，正在背景生成中）：回傳「即將生成出來的星球」會有的尺寸，
        // 讓前端不必特別處理 null。這些值是純運算，不需要資料庫。
        var generator = new GoldbergPlanetGenerator(
            GoldbergPlanetGenerator.SubdivisionFrequency,
            TerrainGenerationOptions.Default);

        return Ok(new PlanetInfoDto(
            generator.PlanetRadiusMeters,
            GoldbergPlanetGenerator.TargetHexagonEdgeLengthMeters,
            GoldbergPlanetGenerator.SubdivisionFrequency,
            generator.TotalTileCount,
            OverviewTextureGenerator.Width,
            OverviewTextureGenerator.Height,
            OverviewTextureGenerator.MaskWidth,
            OverviewTextureGenerator.MaskHeight,
            GeneratedAtUtc: null,
            IsInferred: true));
    }
}
