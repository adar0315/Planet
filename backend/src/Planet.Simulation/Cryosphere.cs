namespace Planet.Simulation;

/// <summary>
/// 冰凍圈：兩極與高山的大陸冰蓋。
///
/// ## 冰把水鎖起來，海平面就下降
///
/// 這是這個模型裡冰最重要的作用，也是使用者要的效果：星球的**總水量是守恆的**
/// （見 <c>SimulationEngine.SolveSeaLevel</c>），冰蓋存起來的每一立方公尺水，
/// 就是海洋少掉的一立方公尺。地球上冰期的海平面比現在低約120公尺，正是這個機制——
/// 當時北美與歐洲北部壓著數公里厚的冰。
///
/// 所以冰蓋一長，海平面下降、大陸棚露出來變成陸地；冰蓋一融，海平面上升、沿海被淹。
/// 這會讓海陸比出現真正的**氣候驅動**變化，而不是只跟著地殼厚度走。
///
/// ## 只算「陸地上的」冰
///
/// 浮在海上的海冰**不影響海平面**（它已經排開了等重的水，融化前後體積相同，
/// 這就是阿基米德原理）。會改變海平面的只有**擱淺在陸地上**的冰蓋。
/// 所以這裡只在露出水面的格子上累積冰。
///
/// ## 一步5萬年，冰蓋幾乎是瞬間平衡的
///
/// 大陸冰蓋建立與崩解的時間尺度是1萬~10萬年，跟一步的長度同一個量級。
/// 所以這裡不做長時間的積分，而是讓厚度往「當下氣溫對應的平衡厚度」鬆弛，
/// 跟 <see cref="Climate"/> 解穩態是同一個道理。
/// </summary>
public sealed class Cryosphere
{
    /// <summary>
    /// 開始堆積冰的溫度（攝氏）。
    /// 取 −2：年均溫低於這個值，夏天融化的量才追不上冬天堆積的量，冰才留得住。
    /// 地球上冰蓋的邊緣大致落在年均溫 0 ~ −5°C 之間。
    /// </summary>
    private const float AccumulationTemperatureC = -2f;

    /// <summary>
    /// 年均溫每低於門檻1度，平衡冰厚增加幾公尺。
    ///
    /// 校準目標是地球的兩大冰蓋：南極內陸年均溫約 −50°C、平均冰厚約2,100公尺
    /// （最厚超過4,000）；格陵蘭約 −20°C、平均約1,500公尺。
    /// 取60：−50°C 給出 (50−2)×60 ≈ 2,880 公尺，量級對得上。
    /// </summary>
    private const float MetersPerDegreeBelowFreezing = 60f;

    /// <summary>
    /// 冰厚上限（公尺）。南極最厚處約4,800公尺；再厚冰自己會流動擴散開來，
    /// 這裡沒有模擬冰流，所以直接用上限代替。
    /// </summary>
    private const float MaxIceThicknessM = 4500f;

    /// <summary>
    /// 冰的密度（公斤／立方公尺）。換算「水當量」要用它——
    /// 1公尺的冰只鎖住0.917公尺的水，忽略這一點會把海平面的變化高估約9%。
    /// </summary>
    private const float IceDensity = 917f;

    /// <summary>水的密度（公斤／立方公尺）。</summary>
    private const float WaterDensity = 1000f;

    /// <summary>
    /// 每步往平衡厚度靠近多少比例。取0.5：一步5萬年，冰蓋的建立/崩解時間尺度是1萬~10萬年，
    /// 所以兩三步內就跟上氣候，但不會在一步之內硬跳（硬跳會讓海平面每步震盪）。
    /// </summary>
    private const float RelaxationPerStep = 0.5f;

    /// <summary>
    /// 平衡冰厚要不要依降雪打折（v2 P6，2026-10-19；只在板塊座標系模式開，標記法照舊只看氣溫）。
    ///
    /// 原本平衡冰厚只看年均溫：一片又冷又乾、幾乎不下雪的沙漠也長出幾公里厚的冰。種子 42 劇本 1 Myr 時冰就蓋住 34% 的陸地、
    /// 40 Myr 時 57%（地球約 10%，冰期最盛約 25%），鎖住的水讓海平面一路降、大陸相對變高變冷，又長更多冰。
    /// 真實的冰蓋要有降雪才長得起來——冰期的西伯利亞夠冷，但太乾，沒有大冰蓋。
    ///
    /// 打折的比例 ＝ 降雪 ÷（全球平均降水 × <see cref="SnowForFullIcePerMeanPrecipitation"/>），上限 1。
    /// 降雪 ＝ 降水 × <see cref="Climate.SnowFraction"/>。還沒解過氣候（全球都沒有降水）時沒有雪，也就沒有冰。
    /// </summary>
    public bool SnowLimited { get; set; }

    /// <summary>
    /// 降雪達到全球平均降水的這個比例，冰才長到只看氣溫的平衡厚度。
    /// 南極沿海的年降雪約 200~600 公釐、內陸約 50 公釐，全球平均降水約 1,000 公釐；取 0.25。
    /// </summary>
    private const float SnowForFullIcePerMeanPrecipitation = 0.25f;

    /// <summary>每格這一步的平衡冰厚（公尺），先算好才知道全球加起來要不要按供給打折。</summary>
    private float[] _equilibrium = [];

    /// <summary>
    /// 推進一步的冰蓋變化。
    ///
    /// ## 冰不能鎖住比星球擁有的還多的水
    ///
    /// 2026-09-09 踩過，而且是**正回饋跑掉**：冰長 → 鎖住水 → 海平面下降 →
    /// 更多陸地露出來 → 更多格子有資格長冰 → 鎖更多水。這個回饋在地球上是真的
    /// （冰期海退確實會擴大陸地），但地球有「海洋縮小則蒸發量減少」這類供給端的煞車，
    /// 原本這裡一個都沒有——平衡冰厚純粹是溫度的函數，跟還剩多少水完全脫鉤。
    ///
    /// 這顆星球的水預算只有平均 1,150 公尺，而冰厚上限 4,500 公尺換算成水當量是
    /// 每格 4,126 公尺，所以**冰只要蓋滿 27.9% 的星球就把水全部吃光**。
    /// 實測跑到2億年時冰覆蓋 29.3%，剛好越過那條線，於是
    /// <c>液態水量 = 總水量 − 冰鎖住的量</c> 變成負數（實測 −4.96e15 立方公尺），
    /// 而 <c>WaterVolumeAt</c> 最小只能是 0，`0 &gt; 負數` 恆成立，
    /// 海平面的括號因此永遠往下掉——症狀是整個測試**空轉9小時、沒有任何訊息**。
    ///
    /// 所以這裡加上供給上限：全球想要的冰量超過總水量時，**按比例縮小每一格的平衡厚度**。
    /// 這不是一個可調的參數，是質量守恆本身。**打折比例一旦明顯小於1，
    /// 代表氣候模型正在產生一個吃掉整顆海洋的雪球**，那要另外查（目前 <see cref="Climate"/>
    /// 沒有海洋熱輸送，高緯度會過冷），不是靠這裡的上限矇混過去。
    /// </summary>
    /// <param name="state">模擬狀態，<see cref="SimulationState.IceThickness"/> 會被更新。</param>
    /// <param name="totalWaterVolume">這顆星球的總水量（立方公尺），冰能取用的上限。</param>
    /// <param name="cellAreaSquareMeters">每格的面積。</param>
    public void Step(SimulationState state, double totalWaterVolume, double cellAreaSquareMeters)
    {
        if (_equilibrium.Length != state.CellCount) _equilibrium = new float[state.CellCount];
        var snowForFullIce = 0f;
        if (SnowLimited)
        {
            var precipitationSum = 0.0;
            for (var cell = 0; cell < state.CellCount; cell++) precipitationSum += state.Precipitation[cell];
            snowForFullIce = (float)(precipitationSum / state.CellCount) * SnowForFullIcePerMeanPrecipitation;
        }

        Parallel.For(0, state.CellCount, cell =>
        {
            // 只有擱淺在陸地上的冰會改變海平面；浮冰不算（阿基米德原理）
            var onLand = state.WaterDepth[cell] <= 0f;
            var below = AccumulationTemperatureC - state.Temperature[cell];

            _equilibrium[cell] = onLand && below > 0f
                ? MathF.Min(below * MetersPerDegreeBelowFreezing, MaxIceThicknessM)
                : 0f;
            if (SnowLimited && _equilibrium[cell] > 0f)
            {
                var snow = state.Precipitation[cell] * Climate.SnowFraction(state.Temperature[cell]);
                _equilibrium[cell] *= snowForFullIce > 0f ? MathF.Min(1f, snow / snowForFullIce) : 0f;
            }
        });

        var wantedThicknessSum = 0.0;
        for (var cell = 0; cell < state.CellCount; cell++) wantedThicknessSum += _equilibrium[cell];
        var wantedWater = wantedThicknessSum * cellAreaSquareMeters * IceDensity / WaterDensity;

        var supply = wantedWater > totalWaterVolume && wantedWater > 0.0
            ? (float)(totalWaterVolume / wantedWater)
            : 1f;

        LastSupplyLimit = supply;

        Parallel.For(0, state.CellCount, cell =>
        {
            var equilibrium = _equilibrium[cell] * supply;
            var current = state.IceThickness[cell];
            state.IceThickness[cell] = current + (equilibrium - current) * RelaxationPerStep;
        });
    }

    /// <summary>
    /// 診斷用：上一步平衡冰厚被供給上限打了幾折（1 代表水夠、沒有打折）。
    ///
    /// 明顯小於1就表示氣候正在要求一個超過整顆星球水量的冰蓋——
    /// 那是氣候端的問題，不是冰凍圈的（見 <see cref="Step"/> 的說明）。
    /// </summary>
    public float LastSupplyLimit { get; private set; } = 1f;

    /// <summary>
    /// 冰蓋總共鎖住多少水（立方公尺水當量）。
    /// <see cref="SimulationEngine"/> 要從總水量裡扣掉這些，才解得出正確的海平面。
    /// </summary>
    /// <param name="state">模擬狀態。</param>
    /// <param name="cellAreaSquareMeters">每格的面積。</param>
    public static double LockedWaterVolume(SimulationState state, double cellAreaSquareMeters)
    {
        var total = 0.0;
        for (var cell = 0; cell < state.CellCount; cell++) total += state.IceThickness[cell];
        return total * cellAreaSquareMeters * IceDensity / WaterDensity;
    }
}
