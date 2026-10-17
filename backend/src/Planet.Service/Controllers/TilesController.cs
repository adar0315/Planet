using Microsoft.AspNetCore.Mvc;
using Planet.Data;
using Planet.Domain;
using Planet.Generation;
using Planet.Shared;

namespace Planet.Service.Controllers;

/// <summary>提供地塊資料的唯讀查詢 REST API：依經緯度找附近地塊、依Id查單一地塊、查地塊的鄰居。</summary>
[ApiController]
[Route("api/tiles")]
public class TilesController : ControllerBase
{
    private readonly ITileRepository _tileRepository;
    private readonly ITileNeighborResolver _neighborResolver;
    private readonly OverviewTextureGenerator _overviewTextureGenerator;

    /// <summary>建立控制器。</summary>
    public TilesController(ITileRepository tileRepository, ITileNeighborResolver neighborResolver, OverviewTextureGenerator overviewTextureGenerator)
    {
        _tileRepository = tileRepository;
        _neighborResolver = neighborResolver;
        _overviewTextureGenerator = overviewTextureGenerator;
    }

    /// <summary>查詢指定經緯度附近最近的地塊。</summary>
    /// <param name="lat">緯度（度）。</param>
    /// <param name="lon">經度（度）。</param>
    /// <param name="count">最多回傳幾筆，預設250。</param>
    /// <param name="ct">取消權杖。</param>
    /// <returns>依距離由近到遠排序的地塊清單。</returns>
    /// <response code="200">依距離由近到遠排序的地塊清單。</response>
    [HttpGet("nearby")]
    [ProducesResponseType<IReadOnlyList<TileDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TileDto>>> GetNearby(
        [FromQuery] double lat,
        [FromQuery] double lon,
        [FromQuery] int count = 250,
        CancellationToken ct = default)
    {
        var tiles = await _tileRepository.GetTilesNearAsync(lat, lon, count, ct);

        // 一併附上patch內部的鄰接關係，前端就不必為了知道「腳下這格能往哪走」再打一次
        // /neighbors（省一次來回），也才能在按鍵當下直接移動、不等伺服器。純記憶體運算，不查DB。
        var neighborIds = PatchNeighborLinker.LinkWithinPatch(tiles, _neighborResolver);
        return Ok(tiles.Select((t, i) => t.ToDto(neighborIds[i])).ToList());
    }

    /// <summary>依Id查詢單一地塊。</summary>
    /// <param name="tileId">地塊Id。</param>
    /// <param name="ct">取消權杖。</param>
    /// <returns>找到的地塊；查無資料回傳404。</returns>
    /// <response code="200">找到的地塊。</response>
    /// <response code="404">找不到指定Id的地塊。</response>
    [HttpGet("{tileId:long}")]
    [ProducesResponseType<TileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TileDto>> GetById(long tileId, CancellationToken ct)
    {
        var tile = await _tileRepository.GetByIdAsync(tileId, ct);
        if (tile is null) return NotFound();

        return Ok(tile.ToDto());
    }

    /// <summary>
    /// 查詢指定地塊的完整地質狀態（地殼、板塊、溫度、降水、水深、冰厚…），也就是它放大來源的那個模擬粗格。
    /// 未來建 3D 場景時的參考資料。
    /// </summary>
    /// <param name="tileId">地塊Id。</param>
    /// <param name="ct">取消權杖。</param>
    /// <returns>來源粗格的完整狀態。</returns>
    /// <response code="200">來源粗格的完整狀態。</response>
    /// <response code="404">找不到這個地塊，或它的地形不是從地質模擬放大來的（程序化生成的星球沒有地質狀態）。</response>
    [HttpGet("{tileId:long}/geology")]
    [ProducesResponseType<PlanetCell>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PlanetCell>> GetGeology(long tileId, CancellationToken ct)
    {
        var tile = await _tileRepository.GetByIdAsync(tileId, ct);
        if (tile?.SourceCellId is not { } cellId) return NotFound();

        var cell = await _tileRepository.GetPlanetCellAsync(cellId, ct);
        return cell is null ? NotFound() : Ok(cell);
    }

    /// <summary>查詢指定地塊的所有鄰居（六角形6個、五角形5個）。</summary>
    /// <param name="tileId">地塊Id。</param>
    /// <param name="ct">取消權杖。</param>
    /// <returns>鄰居地塊清單；地塊本身查無資料回傳404。</returns>
    /// <response code="200">鄰居地塊清單。</response>
    /// <response code="404">找不到指定Id的地塊。</response>
    [HttpGet("{tileId:long}/neighbors")]
    [ProducesResponseType<IReadOnlyList<TileDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<TileDto>>> GetNeighbors(long tileId, CancellationToken ct)
    {
        var tile = await _tileRepository.GetByIdAsync(tileId, ct);
        if (tile is null) return NotFound();

        // 一次查完6組座標（原本是逐一查、最多6次DB來回）。
        var coordinates = _neighborResolver.GetNeighborCoordinates(tile);
        var neighbors = await _tileRepository.GetByCoordinatesAsync(coordinates, ct);

        // 依 resolver 的原始順序輸出，讓方位對應在不同呼叫之間保持穩定
        // （GetByCoordinatesAsync 不保證順序）。
        var byCoordinate = neighbors.ToDictionary(t => (t.FaceIndex, t.Row, t.Col));
        var ordered = coordinates
            .Select(c => byCoordinate.TryGetValue(c, out var t) ? t : null)
            .Where(t => t is not null) // 理論上不會發生，除非DB資料與生成演算法版本不一致
            .Select(t => t!.ToDto())
            .ToList();

        return Ok(ordered);
    }

    /// <summary>
    /// 全星球的低解析度地形總覽貼圖（等距柱狀投影，1024×512，RGBA原始位元組，沒有PNG之類的壓縮/封裝）。
    /// 給前端「星球總覽」模式貼到一顆簡單球體上用，不是實際地塊資料——查2000萬格地塊本身在查詢時間、
    /// 傳輸量、前端渲染都不可行，見 <see cref="OverviewTextureGenerator"/> 的說明。
    /// </summary>
    /// <param name="ct">取消權杖。</param>
    /// <returns>1024×512×4 bytes的RGBA像素資料，逐列由上（北極）到下（南極）、逐行由左（西經180）到右（東經180）。</returns>
    /// <response code="200">RGBA像素資料。</response>
    [HttpGet("overview-texture")]
    [ProducesResponseType(typeof(byte[]), StatusCodes.Status200OK, "application/octet-stream")]
    public async Task<IActionResult> GetOverviewTexture(CancellationToken ct)
    {
        var pixels = await _overviewTextureGenerator.GetRgbaBytesAsync(ct);
        return File(pixels, "application/octet-stream");
    }

    /// <summary>
    /// 全星球的陸海遮罩（<c>MaskWidth</c> × <c>MaskHeight</c>，每像素1位元組：1＝水、0＝陸地），
    /// 給右下角的位置指示器畫出大致的大陸輪廓用。
    ///
    /// 刻意跟上面那張1024×512的RGBA貼圖分開：那是2MB，為了一個120px的小圖示載2MB不合理；
    /// 這份只有8KB。尺寸請向 <c>GET /api/planet</c> 詢問，不要在前端寫死。
    /// </summary>
    /// <param name="ct">取消權杖。</param>
    /// <returns>逐列由上（北極）到下（南極）、逐行由左（西經180）到右（東經180）的遮罩位元組。</returns>
    /// <response code="200">陸海遮罩位元組。</response>
    [HttpGet("overview-water-mask")]
    [ProducesResponseType(typeof(byte[]), StatusCodes.Status200OK, "application/octet-stream")]
    public async Task<IActionResult> GetOverviewWaterMask(CancellationToken ct)
    {
        var mask = await _overviewTextureGenerator.GetWaterMaskAsync(ct);
        return File(mask, "application/octet-stream");
    }

}
