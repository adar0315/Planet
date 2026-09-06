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
    /// 推進一步的冰蓋變化。
    /// </summary>
    /// <param name="state">模擬狀態，<see cref="SimulationState.IceThickness"/> 會被更新。</param>
    public void Step(SimulationState state)
    {
        Parallel.For(0, state.CellCount, cell =>
        {
            // 只有擱淺在陸地上的冰會改變海平面；浮冰不算（阿基米德原理）
            var onLand = state.WaterDepth[cell] <= 0f;
            var below = AccumulationTemperatureC - state.Temperature[cell];

            var equilibrium = onLand && below > 0f
                ? MathF.Min(below * MetersPerDegreeBelowFreezing, MaxIceThicknessM)
                : 0f;

            var current = state.IceThickness[cell];
            state.IceThickness[cell] = current + (equilibrium - current) * RelaxationPerStep;
        });
    }

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
