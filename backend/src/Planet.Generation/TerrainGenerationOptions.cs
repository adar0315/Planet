namespace Planet.Generation;

/// <summary>
/// 地形程序化生成的可調參數。設計成immutable record，讓「更改參數」變成「產生一份新的options物件」，
/// 而不是直接改TerrainGenerator內部狀態——這樣同一個seed+options組合永遠對應同一個確定性結果，
/// 也方便之後做「儲存/比較不同參數版本」之類的功能。用 <c>with</c> 語法可以只改個別欄位，
/// 例如 <c>TerrainGenerationOptions.Default with { IslandCount = 500 }</c>。
/// </summary>
public sealed record TerrainGenerationOptions
{
    /// <summary>地形亂數種子，同種子＋同組參數必定產生同樣的地形。</summary>
    public int Seed { get; init; } = 42;

    /// <summary>大陸A的南北方向falloff半徑縮放倍率（1.0＝原始圓形半徑，&gt;1拉長）。</summary>
    public double ContinentALatitudeStretch { get; init; } = 1.0;

    /// <summary>大陸A的東西方向falloff半徑縮放倍率（1.0＝原始圓形半徑，&lt;1收窄）。</summary>
    public double ContinentALongitudeStretch { get; init; } = 1.0;

    /// <summary>大陸B的南北方向falloff半徑縮放倍率。</summary>
    public double ContinentBLatitudeStretch { get; init; } = 1.0;

    /// <summary>大陸B的東西方向falloff半徑縮放倍率——預設維持0.5（南北瘦長型），跟大陸A（預設1.0，正常寬度）不對稱。</summary>
    public double ContinentBLongitudeStretch { get; init; } = 0.5;

    /// <summary>小島總數（散布＋群聚合計）。</summary>
    public int IslandCount { get; init; } = 250;

    /// <summary>小島裡「屬於群聚（一個群聚中心＋幾個衛星小島，形成類似群島的效果）」的比例，範圍0~1，其餘是單獨散布的小島。</summary>
    public double IslandClusterRatio { get; init; } = 0.6;

    /// <summary>小島是火山地形（而非一般岩地）的機率，範圍0~1。</summary>
    public double IslandVolcanicProbability { get; init; } = 0.70;

    /// <summary>預設參數（跟目前資料庫實際使用的星球生成參數一致）。</summary>
    public static TerrainGenerationOptions Default { get; } = new();
}
