using Microsoft.AspNetCore.Mvc;
using Planet.Data;
using Planet.Domain;
using Planet.Shared;

namespace Planet.Service.Controllers;

/// <summary>
/// 地形顯示樣式（中英文名稱＋依時段變化的顏色）查詢／更新。存成資料表而不是寫死在程式碼裡，
/// 是為了讓調整名稱／顏色不需要重新編譯部署，見 <see cref="TerrainStyle"/> 的說明。
/// </summary>
[ApiController]
[Route("api/terrain-styles")]
public class TerrainStylesController : ControllerBase
{
    private readonly ITerrainStyleRepository _repository;
    private readonly OverviewTextureGenerator _overviewTextureGenerator;

    /// <summary>建立控制器。</summary>
    public TerrainStylesController(ITerrainStyleRepository repository, OverviewTextureGenerator overviewTextureGenerator)
    {
        _repository = repository;
        _overviewTextureGenerator = overviewTextureGenerator;
    }

    /// <summary>查詢全部18種地形的顯示樣式。</summary>
    /// <param name="ct">取消權杖。</param>
    /// <returns>全部地形的顯示樣式清單。</returns>
    /// <response code="200">全部地形的顯示樣式清單。</response>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<TerrainStyleDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TerrainStyleDto>>> GetAll(CancellationToken ct)
    {
        var styles = await _repository.GetAllAsync(ct);
        return Ok(styles.Select(ToDto).ToList());
    }

    /// <summary>更新一種地形的顯示樣式（名稱／顏色）。</summary>
    /// <param name="terrain">要更新的地形類型（跟路由參數對應的 <see cref="TerrainType"/> 名稱，例如 "Grassland"）。</param>
    /// <param name="style">新的顯示樣式內容（<paramref name="style"/> 裡的 Terrain 欄位會被忽略，一律以路由參數為準）。</param>
    /// <param name="ct">取消權杖。</param>
    /// <returns>更新後的顯示樣式；找不到指定地形類型回傳404。</returns>
    /// <response code="200">更新後的顯示樣式。</response>
    /// <response code="404">找不到指定的地形類型。</response>
    [HttpPut("{terrain}")]
    [ProducesResponseType<TerrainStyleDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TerrainStyleDto>> Update(TerrainType terrain, [FromBody] TerrainStyleUpdateRequest style, CancellationToken ct)
    {
        var entity = new TerrainStyle
        {
            Terrain = terrain,
            NameEn = style.NameEn,
            NameZh = style.NameZh,
            ColorDawn = style.ColorDawn,
            ColorDay = style.ColorDay,
            ColorDusk = style.ColorDusk,
            ColorNight = style.ColorNight,
        };

        var updated = await _repository.UpdateAsync(entity, ct);
        if (!updated) return NotFound();

        _overviewTextureGenerator.Invalidate(); // 星球總覽貼圖的顏色快取跟這筆新樣式對不上了，清掉讓下次請求重算。
        return Ok(ToDto(entity));
    }

    private static TerrainStyleDto ToDto(TerrainStyle s) =>
        new(s.Terrain.ToString(), s.NameEn, s.NameZh, s.ColorDawn, s.ColorDay, s.ColorDusk, s.ColorNight);
}

/// <summary>更新地形顯示樣式的請求內容（不含Terrain欄位，路由參數已經指定要更新哪一種地形）。</summary>
/// <param name="NameEn">英文名稱。</param>
/// <param name="NameZh">繁體中文名稱。</param>
/// <param name="ColorDawn">清晨顏色，格式 <c>#rrggbb</c>。</param>
/// <param name="ColorDay">白天顏色，格式 <c>#rrggbb</c>。</param>
/// <param name="ColorDusk">黃昏顏色，格式 <c>#rrggbb</c>。</param>
/// <param name="ColorNight">夜晚顏色，格式 <c>#rrggbb</c>。</param>
public record TerrainStyleUpdateRequest(string NameEn, string NameZh, string ColorDawn, string ColorDay, string ColorDusk, string ColorNight);
