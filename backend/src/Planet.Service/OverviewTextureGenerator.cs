using Planet.Data;
using Planet.Domain;
using Planet.Generation;

namespace Planet.Service;

/// <summary>
/// 產生全星球的低解析度等距柱狀投影（equirectangular）地形色彩貼圖，給前端「星球總覽」模式用。
/// 直接依經緯度取樣（跟資料庫裡2000萬格用的是同一套演算法，顏色會對得上實際地形），完全不查Tile資料——
/// 把2000萬格全部查出來在查詢時間、傳輸量、前端渲染三個維度都不可行（見
/// <see cref="Data.TileRepository.GetTilesNearAsync"/> 的文件註解），低解析度取樣＋純色貼圖貼到一顆
/// 簡單球體上，是唯一可行的「一次看到整顆星球」方案。
/// 第一次請求時才生成（有一定運算量，用 <see cref="Parallel.For(int,int,Action{int})"/> 平行化壓在
/// 可接受範圍），生成後快取在記憶體，之後的請求直接回傳快取——直到 <see cref="Invalidate"/> 被呼叫
/// （星球被重新生成、地形樣式被編輯，顏色可能已經不一樣了）才會在下次請求時重新算一張。
/// 地形顏色一律向 <see cref="ITerrainStyleRepository"/>（<c>TerrainStyles</c> 資料表）查詢，
/// 不再另外維護一份寫死的調色盤——這份表本身就是「地形顯示顏色」的唯一權威來源，改在網頁上編輯
/// 地形樣式之後，這裡也要能顯示新顏色。
/// </summary>
public class OverviewTextureGenerator
{
    /// <summary>貼圖寬度（像素），對應經度360度（-180~+180）。</summary>
    public const int Width = 1024;

    /// <summary>貼圖高度（像素），對應緯度180度（+90~-90，第0列＝北極）。</summary>
    public const int Height = 512;

    /// <summary>陸海遮罩的寬度（像素）。給右下角位置指示器用，只需要看得出大陸輪廓。</summary>
    public const int MaskWidth = 128;

    /// <summary>陸海遮罩的高度（像素）。</summary>
    public const int MaskHeight = 64;

    /// <summary>陸海遮罩裡代表「水」的值。</summary>
    public const byte MaskWater = 1;

    /// <summary>陸海遮罩裡代表「陸地」的值。</summary>
    public const byte MaskLand = 0;

    private static readonly (byte R, byte G, byte B) FallbackColor = (0xff, 0x00, 0xff);

    private readonly PlanetMetadataService _metadataService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Lock _lock = new();
    private byte[]? _cachedRgba;
    private byte[]? _cachedWaterMask;

    /// <summary>建立貼圖產生器。</summary>
    /// <param name="metadataService">
    /// 提供「**實際產生資料庫裡那批地塊**的地形參數」。
    /// 注意這裡刻意不是 <see cref="TerrainOptionsStore"/>（那是「下次重新生成要用的參數」）——
    /// 用錯的話，只要改了參數還沒重新生成，這張貼圖就會畫出一顆資料庫裡不存在的星球。
    /// </param>
    /// <param name="scopeFactory">
    /// 用來在生成貼圖時開一個Scope解析Scoped的 <see cref="ITerrainStyleRepository"/>——
    /// <see cref="OverviewTextureGenerator"/> 本身是Singleton，不能直接注入Scoped服務。
    /// </param>
    public OverviewTextureGenerator(PlanetMetadataService metadataService, IServiceScopeFactory scopeFactory)
    {
        _metadataService = metadataService;
        _scopeFactory = scopeFactory;
    }

    /// <summary>取得RGBA像素資料（<see cref="Width"/> × <see cref="Height"/> × 4 bytes，逐列由上到下、逐行由左到右）。快取不存在時才實際生成（含查一次目前的地形顯示顏色）。</summary>
    public async Task<byte[]> GetRgbaBytesAsync(CancellationToken ct = default)
    {
        if (_cachedRgba is not null) return _cachedRgba;

        var dayColors = await LoadDayColorsAsync(ct);
        var appliedOptions = await _metadataService.GetAppliedTerrainOptionsAsync(ct);

        lock (_lock)
        {
            _cachedRgba ??= Generate(new TerrainGenerator(appliedOptions), dayColors);
            return _cachedRgba;
        }
    }

    /// <summary>
    /// 全星球的陸海遮罩（<see cref="MaskWidth"/> × <see cref="MaskHeight"/>，每像素1位元組：
    /// <see cref="MaskWater"/> ＝水、<see cref="MaskLand"/> ＝陸地），給右下角位置指示器用。
    ///
    /// 刻意不重用上面那張1024×512的RGBA貼圖：那是2MB，為了一個120px的小圖示載2MB不合理。
    /// 也刻意回傳「已經分類好的遮罩」而不是地形代碼——分類規則（哪些地形算水）留在後端一份就好，
    /// 讓前端拿地形代碼自己判斷等於又把規則複製成兩份。
    /// </summary>
    public async Task<byte[]> GetWaterMaskAsync(CancellationToken ct = default)
    {
        if (_cachedWaterMask is not null) return _cachedWaterMask;

        var appliedOptions = await _metadataService.GetAppliedTerrainOptionsAsync(ct);

        lock (_lock)
        {
            _cachedWaterMask ??= GenerateWaterMask(new TerrainGenerator(appliedOptions));
            return _cachedWaterMask;
        }
    }

    /// <summary>
    /// 清掉所有快取。星球重新生成、或 <c>TerrainStyles</c> 資料表被編輯（顏色改變）之後要呼叫這個，
    /// 不然「星球總覽」畫面會繼續顯示舊的貼圖。
    ///
    /// **兩份快取都要清**：顏色改變其實只影響RGBA貼圖、不影響陸海遮罩，
    /// 但重新生成星球會同時影響兩者，漏清遮罩的話小圖示會停在舊地形。
    /// 遮罩只有8,192個取樣點、重算很便宜，寧可多清一次也不要留下不一致。
    /// </summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _cachedRgba = null;
            _cachedWaterMask = null;
        }
    }

    private async Task<IReadOnlyDictionary<TerrainType, (byte R, byte G, byte B)>> LoadDayColorsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ITerrainStyleRepository>();
        var styles = await repository.GetAllAsync(ct);
        return styles.ToDictionary(s => s.Terrain, s => ParseHexColor(s.ColorDay));
    }

    private static (byte R, byte G, byte B) ParseHexColor(string hex)
    {
        var value = Convert.ToInt32(hex.TrimStart('#'), 16);
        return ((byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF));
    }

    /// <summary>
    /// 哪些地形算「水」。這條規則只存在這裡一份——前端拿到的是分類結果，不需要也不應該知道這份清單。
    /// CoralReef 長在淺海裡，算水；Glacier 可能在海上也可能在陸地上，在「大陸輪廓」的用途下算陸地
    /// （極冠看起來是實心的比較符合直覺）。
    /// </summary>
    private static bool IsWater(TerrainType terrain) =>
        terrain is TerrainType.Seawater or TerrainType.Freshwater or TerrainType.CoralReef;

    private static byte[] GenerateWaterMask(ITerrainGenerator terrainGenerator)
    {
        var mask = new byte[MaskWidth * MaskHeight];

        Parallel.For(0, MaskHeight, y =>
        {
            // 與RGBA貼圖同樣的取樣慣例：第0列＝北極，左到右＝西到東。
            var latitude = 90.0 - (y + 0.5) / MaskHeight * 180.0;
            for (var x = 0; x < MaskWidth; x++)
            {
                var longitude = (x + 0.5) / MaskWidth * 360.0 - 180.0;
                var sample = terrainGenerator.Sample(latitude, longitude);
                mask[y * MaskWidth + x] = IsWater(sample.Terrain) ? MaskWater : MaskLand;
            }
        });

        return mask;
    }

    private static byte[] Generate(ITerrainGenerator terrainGenerator, IReadOnlyDictionary<TerrainType, (byte R, byte G, byte B)> dayColors)
    {
        var pixels = new byte[Width * Height * 4];

        Parallel.For(0, Height, y =>
        {
            // 圖片第0列＝北極（緯度+90），最後一列＝南極（緯度-90），跟latLonToVector3同樣「北=上」的慣例。
            var latitude = 90.0 - (y + 0.5) / Height * 180.0;
            for (var x = 0; x < Width; x++)
            {
                // 左到右＝經度由-180到+180（由西到東），跟前端修正過的east-west方向一致。
                var longitude = (x + 0.5) / Width * 360.0 - 180.0;
                var sample = terrainGenerator.Sample(latitude, longitude);
                var (r, g, b) = dayColors.TryGetValue(sample.Terrain, out var color) ? color : FallbackColor;

                var offset = (y * Width + x) * 4;
                pixels[offset] = r;
                pixels[offset + 1] = g;
                pixels[offset + 2] = b;
                pixels[offset + 3] = 255;
            }
        });

        return pixels;
    }
}
