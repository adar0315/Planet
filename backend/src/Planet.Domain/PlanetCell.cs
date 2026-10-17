namespace Planet.Domain;

/// <summary>
/// 地質模擬粗網格的一格（n=150 約 22.5 萬格）在放大成地塊那一刻的完整狀態。
///
/// ## 為什麼不直接存進 <see cref="Tile"/>
///
/// 89 個地塊共用同一個粗格的地質狀態，全部攤到 2,000 萬列會是約 1 GB 的重複資料，
/// 而且之後每加一個欄位都要重寫 2,000 萬列。所以分兩層：地塊只存走動模式直接要用的欄位
/// 加上 <see cref="Tile.SourceCellId"/>，完整狀態放這裡。
///
/// 這張表也是**總覽貼圖與放大取樣的唯一來源**：兩者都從這裡取樣，才保證一致。
/// 高度欄位都是**相對海平面**的公尺數。
/// </summary>
public class PlanetCell
{
    /// <summary>粗格索引（跟模擬網格的格子索引相同）。</summary>
    public int CellId { get; set; }

    /// <summary>格心緯度（度）。</summary>
    public double Latitude { get; set; }

    /// <summary>格心經度（度）。</summary>
    public double Longitude { get; set; }

    /// <summary>分類出來的地表覆蓋類型（放大時最近粗格的地塊都用這個）。</summary>
    public TerrainType Terrain { get; set; }

    /// <summary>地表覆蓋物的高度／深度細分等級，意義同 <see cref="Tile.HeightCategory"/>。</summary>
    public int HeightCategory { get; set; }

    /// <summary>地表高度（公尺，相對海平面；海底是負值）。</summary>
    public float SurfaceElevationMeters { get; set; }

    /// <summary>水深（公尺），0 代表陸地。</summary>
    public float WaterDepthMeters { get; set; }

    /// <summary>這片水是不是連通主要大洋（false 是湖或內海）。陸地為 false。</summary>
    public bool IsOcean { get; set; }

    /// <summary>冰厚（公尺）。</summary>
    public float IceThicknessMeters { get; set; }

    /// <summary>沉積物厚度（公尺）。</summary>
    public float SedimentThicknessMeters { get; set; }

    /// <summary>有機物（礁灰岩等）厚度（公尺）。</summary>
    public float OrganicThicknessMeters { get; set; }

    /// <summary>地表溫度（攝氏）。</summary>
    public float TemperatureCelsius { get; set; }

    /// <summary>降水量，模擬的**相對尺度**（不是公釐／年，典型值 0~30）。</summary>
    public float Precipitation { get; set; }

    /// <summary>地殼類型（0 海洋、1 大陸，同模擬的 <c>CrustType</c>）。</summary>
    public int CrustType { get; set; }

    /// <summary>地殼厚度（公里）。</summary>
    public float CrustThicknessKm { get; set; }

    /// <summary>地殼年齡（百萬年）。</summary>
    public float CrustAgeMyr { get; set; }

    /// <summary>地殼密度（kg/m³）。</summary>
    public float CrustDensity { get; set; }

    /// <summary>所屬板塊編號。</summary>
    public int PlateId { get; set; }

    /// <summary>板塊邊界類型（同模擬的 <c>BoundaryType</c>：0 非邊界、1 聚合、2 張裂、3 轉形）。</summary>
    public int Boundary { get; set; }

    /// <summary>火山活動強度。</summary>
    public float VolcanicActivity { get; set; }
}
