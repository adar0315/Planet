namespace Planet.Simulation;

/// <summary>
/// 一支地函熱柱（mantle plume）／熱點。
/// </summary>
/// <param name="X">位置單位向量X（固定在**地函**參考系，不隨板塊移動）。</param>
/// <param name="Y">位置單位向量Y。</param>
/// <param name="Z">位置單位向量Z。</param>
/// <param name="StrengthKmPerMyr">噴發強度：每百萬年在正上方那一格堆積幾公里的地殼。</param>
/// <param name="RemainingLifeMyr">還會活躍多久（百萬年）。歸零後熄滅，在別處重新生成一支。</param>
public readonly record struct MantlePlume(
    double X,
    double Y,
    double Z,
    double StrengthKmPerMyr,
    double RemainingLifeMyr);

/// <summary>
/// 板塊**內部**的火山作用：地函熱柱。
///
/// 前面已經有的兩種火山都綁在板塊邊界上——中洋脊（張裂處生成新地殼）與島弧
/// （隱沒帶，見 <c>PlateTectonics.ApplyConvergence</c>）。熱柱是第三種，也是唯一
/// **不在邊界上**的：地函深處上湧的岩漿柱穿透板塊中央，在海洋中間憑空造出火山島。
///
/// ## 為什麼熱點固定不動，而板塊在上面滑過去
///
/// 熱柱來自地函深處，板塊在它上面移動，於是同一支熱柱會在板塊上**拖出一整串**
/// 年齡由新到老的火山島——夏威夷－天皇海山鏈就是這樣來的，這也是判斷板塊絕對運動方向
/// 最主要的證據。程式裡因此刻意把熱點座標固定、只讓板塊動。
///
/// **這正是 <c>PlateSet.RemoveNetRotation</c> 真正的物理意義**：熱點參考系跟無淨旋轉參考系
/// 本來就幾乎一致，有了它「板塊相對於地函在動」才有意義，島鏈才會指向正確的方向。
///
/// ## 熱柱會熄滅，也會有新的生出來
///
/// 地球上的熱點壽命約數千萬到一億年（部分可追溯更久）。若讓熱點永遠固定不變，
/// 跑上十億年會在同樣幾個位置反覆堆出不自然的巨大高地，所以這裡給每支熱柱一個壽命，
/// 熄滅後在別處重新生成一支，維持長時間下的變化。
/// </summary>
public sealed class MantlePlumes
{
    /// <summary>
    /// 熱點數量。地球上公認的主要熱點約10~20個（夏威夷、冰島、黃石、留尼旺、加拉巴哥……），
    /// 這顆星球的表面積是地球的8.45%，但取8而不是等比例的1~2支——太少會讓板塊內部完全沒有變化，
    /// 而熱點造出來的島鏈是這個模擬最容易一眼認出來的地質特徵。
    /// </summary>
    public const int DefaultPlumeCount = 8;

    /// <summary>
    /// 熱柱強度的範圍（公里地殼／百萬年）。
    ///
    /// **這是校準出來的，不是推導的。** 真實熱點的產量（夏威夷約0.1 km³/年）換算到這裡的格子
    /// 大小非常敏感——熱柱的實際噴發範圍比一格小得多，直接除以格面積會高估。
    /// 取這個範圍的依據是結果：板塊以約3公分/年滑過一格（中心間距93公里）需要約300萬年，
    /// 期間堆積約9~24公里，讓海洋地殼從7公里長到16~31公里——正好是「冰島／夏威夷這種
    /// 大型火山島」的量級，偶爾越過25公里的大陸門檻，符合熱點確實能造出陸地的事實。
    /// </summary>
    private const double MinStrengthKmPerMyr = 3.0;

    /// <summary>熱柱強度上限，見 <see cref="MinStrengthKmPerMyr"/>。</summary>
    private const double MaxStrengthKmPerMyr = 8.0;

    /// <summary>熱柱壽命的範圍（百萬年）。地球上的熱點大約是這個量級。</summary>
    private const double MinLifeMyr = 20.0;

    /// <summary>熱柱壽命上限，見 <see cref="MinLifeMyr"/>。</summary>
    private const double MaxLifeMyr = 120.0;

    /// <summary>
    /// 熱柱的噴發物有多少比例落在正下方那一格之外的鄰居上。
    /// 真實的火山島有山體、有側翼，不會只堆在一個點上；散一點也讓島鏈看起來連貫而不是斷點。
    /// </summary>
    private const double NeighborShare = 0.35;

    private readonly Random _rng;

    /// <summary>目前活躍的熱柱。</summary>
    public MantlePlume[] Plumes { get; }

    /// <summary>診斷用：最近一步堆積的地殼厚度總和（公里·格）。地殼收支要把它算進去。</summary>
    public double LastAddedCrustKm { get; private set; }

    /// <summary>建立一組隨機的地函熱柱。</summary>
    /// <param name="seed">亂數種子，同種子會得到同一組熱柱。</param>
    /// <param name="plumeCount">熱柱數量。</param>
    public MantlePlumes(int seed, int plumeCount = DefaultPlumeCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(plumeCount);
        _rng = new Random(seed);
        Plumes = new MantlePlume[plumeCount];
        for (var i = 0; i < plumeCount; i++) Plumes[i] = CreatePlume();
    }

    /// <summary>
    /// 推進一步：每支熱柱在它正上方那一格（與鄰居）堆積地殼，並遞減壽命。
    /// </summary>
    /// <param name="grid">網格。</param>
    /// <param name="state">模擬狀態，地殼厚度與火山活動會被更新。</param>
    /// <param name="deltaMyr">這一步代表多少百萬年。</param>
    public void Step(SimulationGrid grid, SimulationState state, double deltaMyr)
    {
        LastAddedCrustKm = 0;
        for (var i = 0; i < Plumes.Length; i++)
        {
            var plume = Plumes[i];

            var cell = grid.FindNearestCell(plume.X, plume.Y, plume.Z);
            var added = plume.StrengthKmPerMyr * deltaMyr;

            LastAddedCrustKm += Erupt(state, cell, (float)(added * (1.0 - NeighborShare)), plume.StrengthKmPerMyr);

            // 側翼：平分給鄰居，讓火山有山體而不是一根針
            var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
            var neighborCount = 0;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                if (grid.Neighbors[baseIndex + s] != SimulationGrid.NoNeighbor) neighborCount++;
            }

            if (neighborCount > 0)
            {
                var perNeighbor = (float)(added * NeighborShare / neighborCount);
                for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
                {
                    var neighbor = grid.Neighbors[baseIndex + s];
                    if (neighbor == SimulationGrid.NoNeighbor) continue;
                    LastAddedCrustKm += Erupt(
                        state, neighbor, perNeighbor, plume.StrengthKmPerMyr * NeighborShare / neighborCount);
                }
            }

            var remaining = plume.RemainingLifeMyr - deltaMyr;
            Plumes[i] = remaining <= 0 ? CreatePlume() : plume with { RemainingLifeMyr = remaining };
        }
    }

    /// <summary>在一格上堆積火山物質，並記錄火山活動強度供渲染使用。</summary>
    /// <returns>實際堆上去的厚度（公里）。可能小於要求的量，因為有上限。</returns>
    private static float Erupt(SimulationState state, int cell, float thicknessKm, double activityKmPerMyr)
    {
        var before = state.CrustThickness[cell];
        state.CrustThickness[cell] = MathF.Min(before + thicknessKm, Isostasy.MaxCrustThicknessKm);
        var actuallyAdded = state.CrustThickness[cell] - before;
        state.VolcanicActivity[cell] += (float)activityKmPerMyr;

        // 熱柱堆到夠厚一樣會變成大陸地殼——冰島就是熱點造出來、露出海面的例子
        if (state.Crust[cell] == CrustType.Oceanic
            && state.CrustThickness[cell] >= Isostasy.ContinentalThresholdKm)
        {
            state.TryMakeContinental(cell);
        }

        return actuallyAdded;
    }

    /// <summary>在球面上隨機生成一支新的熱柱。</summary>
    private MantlePlume CreatePlume()
    {
        // z均勻分布法：直接對經緯度取隨機會讓熱點在極區聚集
        var z = _rng.NextDouble() * 2.0 - 1.0;
        var theta = _rng.NextDouble() * 2.0 * Math.PI;
        var r = Math.Sqrt(1.0 - z * z);

        return new MantlePlume(
            r * Math.Cos(theta),
            r * Math.Sin(theta),
            z,
            MinStrengthKmPerMyr + _rng.NextDouble() * (MaxStrengthKmPerMyr - MinStrengthKmPerMyr),
            MinLifeMyr + _rng.NextDouble() * (MaxLifeMyr - MinLifeMyr));
    }
}
