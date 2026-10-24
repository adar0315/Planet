namespace Planet.Simulation;

/// <summary>可以畫出來看的模擬欄位。</summary>
public enum SimulationField
{
    /// <summary>地表高程，用地形分層著色（深海→淺海→平原→高山→雪線）。最常用的總覽。</summary>
    Elevation = 0,

    /// <summary>水深，用來檢查海陸分布與湖泊。</summary>
    WaterDepth = 1,

    /// <summary>溫度，藍（冷）到紅（熱）。</summary>
    Temperature = 2,

    /// <summary>地殼類型，海洋地殼 vs 大陸地殼兩色。</summary>
    CrustType = 3,

    /// <summary>沉積物厚度（含有機物），檢查堆積作用有沒有在正確的地方發生。</summary>
    SedimentThickness = 4,

    /// <summary>板塊分布，每個板塊一個顏色。用來確認板塊真的有在移動。</summary>
    PlateId = 5,

    /// <summary>板塊邊界類型：聚合（紅）、張裂（綠）、轉形（黃）、非邊界（深灰）。</summary>
    BoundaryType = 6,

    /// <summary>地殼厚度，薄（暗）到厚（亮）。山根與大陸的分布看這個最清楚。</summary>
    CrustThickness = 7,

    /// <summary>地殼年齡。海洋地殼從中洋脊新生後往兩側變老，正常的話會看到對稱的條帶。</summary>
    CrustAge = 8,

    /// <summary>
    /// 火山活動強度。三種火山都會顯示：中洋脊（沿著張裂邊界的長條）、
    /// 島弧（沿著聚合邊界的弧線）、地函熱柱（板塊內部的孤立熱點，會拖出島鏈）。
    /// </summary>
    VolcanicActivity = 9,

    /// <summary>
    /// 地表高程，**再把各大板塊的邊界描上去**（紅＝聚合、綠＝張裂、黃＝轉形、白＝該處沒有相對運動）。
    ///
    /// 跟單獨的 <see cref="BoundaryType"/> 的差別是那張圖只有邊界、看不到地形，
    /// 對不出「這條隱沒帶造出來的是哪一條山脈」。這張把兩者疊在一起。
    /// </summary>
    ElevationWithPlateBoundaries = 10,

    /// <summary>
    /// 降水量。要看得到的特徵：赤道多雨帶、南北緯30度的沙漠帶、山脈背風側的雨影。
    /// </summary>
    Precipitation = 11,

    /// <summary>陸地逕流量（＝可以往下游流的水）。階段4流水侵蝕的輸入，海面是黑的。</summary>
    Runoff = 12,

    /// <summary>
    /// 珊瑚礁的厚度。應該只出現在南北緯30度之間的溫暖淺海；
    /// 露出水面的礁體（珊瑚島／環礁）另外用亮色標示。
    /// </summary>
    CoralReef = 13,

    /// <summary>
    /// 冰蓋厚度。應該只出現在兩極與高山；冰把水鎖住，所以冰一多海平面就下降。
    /// </summary>
    IceThickness = 14,

    /// <summary>
    /// 河流與湖（v2 P9）：陸地淡淡的灰綠當底，湖是亮青色，河依流量由淺藍到深藍（見 <see cref="Rivers"/>）。
    /// 要有依連通性注水的水系（v2）才畫得出河；沒有的話只有湖與海。
    /// </summary>
    RiversAndLakes = 15,

    /// <summary>
    /// 地表露出的岩石（水文地貌規劃 H1）：結晶基盤（粉紅）、沉積岩（土黃）、火山岩（暗紅）、混雜岩（紫）、
    /// 鬆散沉積物（淡米色）。海是暗藍底，海床的岩石不畫——要看的是陸地上的軟硬分布。
    /// </summary>
    Lithology = 16,

    /// <summary>
    /// 侵蝕與堆積（水文地貌規劃 H2）：陸地上紅褐＝河流下切（越亮越快）、黃＝河流堆積（沖積扇、沖積平原）、
    /// 青＝冰川刨蝕、淡黃白＝風蝕（沙漠、戈壁）、粉褐＝黃土；海上綠＝三角洲的供砂。都是約 100 萬年的移動平均。
    /// </summary>
    Landforms = 17,
}

/// <summary>
/// 把模擬狀態畫成等距柱狀投影（equirectangular）RGBA 貼圖，給前端顯示用。
///
/// **取樣慣例跟 <c>OverviewTextureGenerator</c> 完全一致**（第0列＝北極、由左到右＝經度-180→+180），
/// 這組慣例已經用7個座標實測驗證過跟前端 <c>SphereGeometry</c> 的 UV 對得上
/// （見 docs/architecture.md「全星球總覽模式」），不要自己另外定一套。
///
/// 跟 <c>OverviewTextureGenerator</c> 的關鍵差異：那支是**取樣連續函式**（地形noise可以在任意
/// 經緯度求值），這支面對的是**離散的逐格資料**，所以需要「這個像素該顯示哪一格」的對應表。
/// 這張對應表只跟網格與貼圖尺寸有關、跟模擬跑到第幾步無關，所以**建一次就重複使用**——
/// 之後每張畫面只是查表＋上色，成本是 O(像素數)，即時模擬每秒推好幾張才不會被這個拖垮。
/// </summary>
public sealed class SimulationFieldRenderer
{
    /// <summary>貼圖寬度（像素）。刻意跟 <c>OverviewTextureGenerator.Width</c> 一致，前端可以共用同一顆球體與材質設定。</summary>
    public const int Width = 1024;

    /// <summary>貼圖高度（像素）。</summary>
    public const int Height = 512;

    /// <summary>每個像素對應哪一格（長度 = Width*Height）。建構時算好，之後每次renderer只查表。</summary>
    private readonly int[] _cellByPixel;

    private readonly SimulationGrid _grid;

    /// <summary>建立renderer並預先算好像素→格子的對應表。</summary>
    /// <param name="grid">要顯示的網格。</param>
    public SimulationFieldRenderer(SimulationGrid grid)
    {
        _grid = grid;
        _cellByPixel = BuildPixelToCellMap(grid);
    }

    /// <summary>
    /// 查出貼圖上某個像素顯示的是哪一格。
    /// 除了測試要驗證對應表正確之外，之後「在總覽畫面上點一個位置，要知道點到哪一格」也會用到。
    /// </summary>
    /// <param name="x">像素X座標（0 ~ <see cref="Width"/>-1）。</param>
    /// <param name="y">像素Y座標（0 ~ <see cref="Height"/>-1，0＝北極）。</param>
    public int CellAtPixel(int x, int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, Width);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        return _cellByPixel[y * Width + x];
    }

    /// <summary>把指定欄位畫成 RGBA 位元組（Width × Height × 4，逐列由上到下、逐行由左到右）。</summary>
    /// <param name="state">目前的模擬狀態。</param>
    /// <param name="field">要畫哪個欄位。</param>
    /// <param name="hydrology">水系（<see cref="SimulationField.RiversAndLakes"/> 要用；其他欄位用不到）。</param>
    public byte[] Render(SimulationState state, SimulationField field, Hydrology? hydrology = null)
    {
        if (field == SimulationField.RiversAndLakes) return RenderRivers(state, hydrology);
        var pixels = new byte[Width * Height * 4];
        var baseField = field == SimulationField.ElevationWithPlateBoundaries
            ? SimulationField.Elevation
            : field;

        Parallel.For(0, Height, y =>
        {
            for (var x = 0; x < Width; x++)
            {
                var pixelIndex = y * Width + x;
                var cell = _cellByPixel[pixelIndex];
                var (r, g, b) = ColorFor(state, baseField, cell);

                var offset = pixelIndex * 4;
                pixels[offset] = r;
                pixels[offset + 1] = g;
                pixels[offset + 2] = b;
                pixels[offset + 3] = 255;
            }
        });

        if (field == SimulationField.ElevationWithPlateBoundaries)
        {
            DrawPlateBoundaries(state, pixels);
        }

        return pixels;
    }

    /// <summary>河流與湖的圖層，見 <see cref="SimulationField.RiversAndLakes"/>。</summary>
    private byte[] RenderRivers(SimulationState state, Hydrology? hydrology)
    {
        var pixels = new byte[Width * Height * 4];
        var discharge = hydrology?.Discharge;
        var lakeOf = hydrology?.LakeOf;
        var minDischarge = hydrology is null ? 0f : Rivers.MinDischarge(_grid, state);
        Parallel.For(0, Height, y =>
        {
            for (var x = 0; x < Width; x++)
            {
                var pixelIndex = y * Width + x;
                var cell = _cellByPixel[pixelIndex];
                (byte R, byte G, byte B) color;
                if (lakeOf is not null && lakeOf[cell] >= 0)
                {
                    color = (0x4f, 0xd8, 0xf0);
                }
                else if (state.WaterDepth[cell] > 0f)
                {
                    color = (0x0b, 0x1d, 0x3a);
                }
                else
                {
                    var size = discharge is null ? 0f : Rivers.Size(discharge[cell], minDischarge);
                    if (discharge is not null && Rivers.IsRiver(discharge[cell], minDischarge))
                    {
                        color = Ramp(size, 0f, 1f, (0x7f, 0xb8, 0xff), (0x10, 0x4a, 0xd8));
                    }
                    else
                    {
                        var elevation = state.SurfaceElevationAt(cell) - state.SeaLevelMeters;
                        color = Ramp(elevation, 0f, 4000f, (0x5a, 0x66, 0x52), (0xb8, 0xb8, 0xb0));
                    }
                }
                var offset = pixelIndex * 4;
                pixels[offset] = color.R;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.B;
                pixels[offset + 3] = 255;
            }
        });
        return pixels;
    }

    /// <summary>
    /// 把板塊邊界描到已經畫好的貼圖上。
    ///
    /// 邊界是**在像素層級**判斷的：比對這個像素與右邊、下面那個像素各自對應到的格子屬於哪個板塊，
    /// 不同就是邊界。這樣不需要走訪網格的鄰接表，畫出來的線也剛好是一像素寬、不會隨板塊大小變粗。
    /// 最右／最下一行不比對（沒有右邊/下面的像素），少描那一條線在球面上看不出來。
    ///
    /// 顏色沿用 <see cref="SimulationField.BoundaryType"/> 那張圖的配色，兩張圖才對得起來。
    /// </summary>
    private void DrawPlateBoundaries(SimulationState state, byte[] pixels)
    {
        Parallel.For(0, Height, y =>
        {
            for (var x = 0; x < Width; x++)
            {
                var pixelIndex = y * Width + x;
                var cell = _cellByPixel[pixelIndex];
                var plate = state.PlateId[cell];

                // 經度是環繞的，所以最右邊要跟最左邊比，海圖上才不會有一條假的接縫
                var right = _cellByPixel[y * Width + (x + 1) % Width];
                var isEdge = state.PlateId[right] != plate
                    || (y + 1 < Height && state.PlateId[_cellByPixel[(y + 1) * Width + x]] != plate);
                if (!isEdge) continue;

                var (r, g, b) = state.Boundary[cell] switch
                {
                    Simulation.BoundaryType.Convergent => ((byte)0xe0, (byte)0x3a, (byte)0x3a),
                    Simulation.BoundaryType.Divergent => ((byte)0x3a, (byte)0xd0, (byte)0x6a),
                    Simulation.BoundaryType.Transform => ((byte)0xe0, (byte)0xd0, (byte)0x3a),
                    _ => ((byte)0xf0, (byte)0xf0, (byte)0xf0),
                };

                var offset = pixelIndex * 4;
                pixels[offset] = r;
                pixels[offset + 1] = g;
                pixels[offset + 2] = b;
            }
        });
    }

    private static (byte R, byte G, byte B) ColorFor(SimulationState state, SimulationField field, int cell) => field switch
    {
        SimulationField.Elevation => ElevationColor(state, cell),
        SimulationField.WaterDepth => state.WaterDepth[cell] <= 0f
            ? ((byte)0x3b, (byte)0x2f, (byte)0x2f)
            : Ramp(state.WaterDepth[cell], 0f, 6000f, (0x9f, 0xd8, 0xef), (0x03, 0x1a, 0x4a)),
        SimulationField.Temperature => Ramp(state.Temperature[cell], -30f, 35f, (0x2b, 0x5f, 0xd9), (0xd9, 0x33, 0x2b)),
        SimulationField.CrustType => state.Crust[cell] == CrustType.Continental
            ? ((byte)0xc2, (byte)0xa2, (byte)0x6b)
            : ((byte)0x1d, (byte)0x3f, (byte)0x6e),
        SimulationField.SedimentThickness => Ramp(
            state.SedimentThickness[cell] + state.OrganicThickness[cell], 0f, 500f,
            (0x2a, 0x2a, 0x2a), (0xe8, 0xd5, 0x9a)),
        SimulationField.PlateId => PlateColor(state.PlateId[cell]),
        SimulationField.BoundaryType => state.Boundary[cell] switch
        {
            Simulation.BoundaryType.Convergent => ((byte)0xe0, (byte)0x3a, (byte)0x3a),
            Simulation.BoundaryType.Divergent => ((byte)0x3a, (byte)0xd0, (byte)0x6a),
            Simulation.BoundaryType.Transform => ((byte)0xe0, (byte)0xd0, (byte)0x3a),
            _ => ((byte)0x22, (byte)0x22, (byte)0x28),
        },
        SimulationField.CrustThickness => Ramp(state.CrustThickness[cell], 0f, Isostasy.MaxCrustThicknessKm,
            (0x10, 0x18, 0x30), (0xff, 0xf0, 0xd0)),
        SimulationField.CrustAge => Ramp(state.CrustAge[cell], 0f, 200f,
            (0xf2, 0x50, 0x30), (0x20, 0x30, 0x70)),
        // 上限取8：熱柱最強是8公里/百萬年，中洋脊也是同一個量級（見 MantlePlumes 的強度校準）。
        // 島弧比這弱得多，所以會顯示成暗紅色而不是完全看不見。
        // 乾（褐）→ 濕（深綠）
        SimulationField.Precipitation => SqrtRamp(state.Precipitation[cell], 30f,
            (0xc9, 0xa0, 0x5c), (0x0a, 0x4d, 0x2e)),
        SimulationField.CoralReef => CoralReefColor(state, cell),
        // 沒有冰的地方照海陸分兩種暗色當底；冰由淡藍到純白，上限用南極最厚處的量級
        SimulationField.IceThickness => state.IceThickness[cell] <= 1f
            ? (state.WaterDepth[cell] > 0f
                ? ((byte)0x0d, (byte)0x1c, (byte)0x30)
                : ((byte)0x2a, (byte)0x26, (byte)0x20))
            : Ramp(state.IceThickness[cell], 1f, 4000f, (0x8f, 0xc8, 0xe8), (0xff, 0xff, 0xff)),
        SimulationField.Runoff => state.WaterDepth[cell] > 0f
            ? ((byte)0x0d, (byte)0x12, (byte)0x22)
            : SqrtRamp(state.Moisture[cell], 10f, (0x3a, 0x2e, 0x1e), (0x4f, 0xd0, 0xff)),
        SimulationField.Lithology => LithologyColor(state, cell),
        SimulationField.Landforms => LandformColor(state, cell),
        SimulationField.VolcanicActivity => state.VolcanicActivity[cell] <= 0f
            ? ((byte)0x14, (byte)0x14, (byte)0x18)
            : Ramp(state.VolcanicActivity[cell], 0f, 8f, (0x5a, 0x12, 0x08), (0xff, 0xe8, 0x8a)),
        _ => (0xff, 0x00, 0xff),
    };

    private static (byte R, byte G, byte B) LandformColor(SimulationState state, int cell)
    {
        if (state.WaterDepth[cell] > 0f)
        {
            return state.DeltaSupply[cell] > 0f
                ? SqrtRamp(state.DeltaSupply[cell], 2_000f, (0x10, 0x30, 0x20), (0x60, 0xff, 0x80))
                : ((byte)0x0c, (byte)0x14, (byte)0x24);
        }
        if (state.LoessRate[cell] > 1f && state.LoessRate[cell] > state.AggradationRate[cell])
            return SqrtRamp(state.LoessRate[cell], 100f, (0x50, 0x38, 0x30), (0xe8, 0xa8, 0x80));
        if (state.DeflationRate[cell] > 1f)
            return SqrtRamp(state.DeflationRate[cell], 50f, (0x60, 0x58, 0x40), (0xff, 0xf4, 0xc0));
        var aggradation = state.AggradationRate[cell];
        var glacial = state.GlacialIncisionRate[cell];
        var incision = state.IncisionRate[cell];
        if (aggradation >= incision && aggradation >= glacial && aggradation > 1f)
            return SqrtRamp(aggradation, 400f, (0x40, 0x38, 0x18), (0xff, 0xe0, 0x40));
        if (glacial > incision && glacial > 1f)
            return SqrtRamp(glacial, 400f, (0x18, 0x38, 0x40), (0x80, 0xf0, 0xff));
        return incision > 1f
            ? SqrtRamp(incision, 400f, (0x30, 0x22, 0x1c), (0xff, 0x70, 0x40))
            : ((byte)0x2a, (byte)0x2a, (byte)0x2a);
    }

    private static (byte R, byte G, byte B) LithologyColor(SimulationState state, int cell)
    {
        if (state.WaterDepth[cell] > 0f) return (0x10, 0x1c, 0x30);
        return Simulation.Lithology.ExposedAt(state, cell) switch
        {
            ExposedRock.LooseSediment => (0xe8, 0xdc, 0xb8),
            ExposedRock.Sedimentary => (0xc8, 0x9a, 0x50),
            ExposedRock.Volcanic => (0x8a, 0x2a, 0x22),
            ExposedRock.Melange => (0x7a, 0x50, 0x9a),
            ExposedRock.Crystalline => (0xe0, 0x8a, 0x9a),
            _ => (0x40, 0x48, 0x58), // 露出來的海洋玄武岩（被抬上岸的洋殼）
        };
    }

    /// <summary>
    /// 地形分層著色：水面下依深度由淺到深、陸地依高度由綠到褐到白。
    /// 只是給人眼看的除錯配色，**不是**正式的地形顏色——正式顏色的權威來源是資料庫的
    /// <c>TerrainStyles</c> 表（見 CLAUDE.md），等階段5決定岩性之後才會接上那套。
    /// </summary>
    private static (byte R, byte G, byte B) ElevationColor(SimulationState state, int cell)
    {
        if (state.WaterDepth[cell] > 0f)
        {
            return Ramp(state.WaterDepth[cell], 0f, 6000f, (0x7e, 0xc8, 0xe3), (0x04, 0x1e, 0x50));
        }

        var elevation = state.SurfaceElevationAt(cell);
        if (elevation < 500f) return Ramp(elevation, 0f, 500f, (0x4c, 0x8b, 0x4f), (0x8f, 0xa8, 0x54));
        if (elevation < 2000f) return Ramp(elevation, 500f, 2000f, (0x8f, 0xa8, 0x54), (0x8a, 0x6b, 0x45));
        if (elevation < 4000f) return Ramp(elevation, 2000f, 4000f, (0x8a, 0x6b, 0x45), (0x9a, 0x9a, 0x9a));
        return Ramp(elevation, 4000f, 8000f, (0x9a, 0x9a, 0x9a), (0xff, 0xff, 0xff));
    }

    /// <summary>
    /// 珊瑚礁：沒有礁的地方照海陸分成兩種暗色當底，礁體由暗綠到亮青綠，
    /// 已經露出水面的礁體（珊瑚島／環礁）用接近白的亮色，一眼看得出哪些長成了島。
    /// </summary>
    private static (byte R, byte G, byte B) CoralReefColor(SimulationState state, int cell)
    {
        var reef = state.OrganicThickness[cell];
        if (reef <= 1f)
        {
            return state.WaterDepth[cell] > 0f
                ? ((byte)0x0d, (byte)0x1c, (byte)0x30)
                : ((byte)0x2a, (byte)0x26, (byte)0x20);
        }

        // 上限1,500公尺是實測的量級（p99約1,400、最大2,000）——真實的環礁底下確實有
        // 一公里以上的珊瑚灰岩，因為火山島持續下沉、珊瑚一直往上追（達爾文沉降理論）。
        return state.WaterDepth[cell] <= 0f
            ? ((byte)0xff, (byte)0xf4, (byte)0xd6) // 露出水面的珊瑚島
            : Ramp(reef, 1f, 1500f, (0x10, 0x50, 0x48), (0x50, 0xe8, 0xc8));
    }

    /// <summary>
    /// 板塊的顏色。用黃金比例分散色相，相鄰編號的板塊顏色差異夠大，
    /// 板塊數量增加時也不會撞色（固定調色盤在超過色數之後就會重複）。
    /// </summary>
    private static (byte R, byte G, byte B) PlateColor(int plateId)
    {
        var hue = plateId * 0.618033988749895 % 1.0;
        return HsvToRgb(hue, 0.55, 0.95);
    }

    private static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        var i = (int)(h * 6);
        var f = h * 6 - i;
        var p = v * (1 - s);
        var q = v * (1 - f * s);
        var t = v * (1 - (1 - f) * s);
        var (r, g, b) = (i % 6) switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
        return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    /// <summary>
    /// 在兩個顏色之間依數值的**平方根**內插。
    ///
    /// 給分布極度偏斜的欄位用（降水、逕流）：實測降水的 p50 是 0.4、p99 是 25.6、最大 58，
    /// 線性刻度會把九成的地方擠進同一個顏色，整張圖看起來一片死色、只有赤道那條帶看得出來。
    /// 平方根把低端的差異拉開，同時保留高端還沒飽和。
    /// </summary>
    /// <param name="value">數值（負值當0）。</param>
    /// <param name="max">對應到 <paramref name="high"/> 的數值。</param>
    /// <param name="low">數值0的顏色。</param>
    /// <param name="high">數值達到 <paramref name="max"/> 的顏色。</param>
    private static (byte R, byte G, byte B) SqrtRamp(
        float value, float max, (int R, int G, int B) low, (int R, int G, int B) high)
    {
        var t = max > 0f ? MathF.Sqrt(Math.Clamp(value, 0f, max) / max) : 0f;
        return (
            (byte)(low.R + (high.R - low.R) * t),
            (byte)(low.G + (high.G - low.G) * t),
            (byte)(low.B + (high.B - low.B) * t));
    }

    /// <summary>在兩個顏色之間依數值線性內插。超出範圍會夾在兩端。</summary>
    private static (byte R, byte G, byte B) Ramp(
        float value, float min, float max,
        (int R, int G, int B) low, (int R, int G, int B) high)
    {
        var t = max > min ? Math.Clamp((value - min) / (max - min), 0f, 1f) : 0f;
        return (
            (byte)(low.R + (high.R - low.R) * t),
            (byte)(low.G + (high.G - low.G) * t),
            (byte)(low.B + (high.B - low.B) * t));
    }

    /// <summary>
    /// 建立「每個像素該顯示哪一格」的對應表。
    ///
    /// 最近格的搜尋本身交給 <see cref="SimulationGrid.FindNearestCell"/>（含極區的經度範圍修正），
    /// 這裡只負責走訪每個像素。空間搜尋只存在網格那一份，不要在這裡再實作一次。
    /// </summary>
    private static int[] BuildPixelToCellMap(SimulationGrid grid)
    {
        var map = new int[Width * Height];

        Parallel.For(0, Height, y =>
        {
            // 跟 OverviewTextureGenerator 同一組慣例：第0列＝北極（+90度）。
            var latitude = 90.0 - (y + 0.5) / Height * 180.0;
            var latRad = latitude * Math.PI / 180.0;
            var cosLat = Math.Cos(latRad);
            var sinLat = Math.Sin(latRad);

            for (var x = 0; x < Width; x++)
            {
                var longitude = (x + 0.5) / Width * 360.0 - 180.0;
                var lonRad = longitude * Math.PI / 180.0;
                map[y * Width + x] = grid.FindNearestCell(
                    cosLat * Math.Cos(lonRad),
                    cosLat * Math.Sin(lonRad),
                    sinLat);
            }
        });

        return map;
    }
}
