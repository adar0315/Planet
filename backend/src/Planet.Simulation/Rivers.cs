namespace Planet.Simulation;

/// <summary>
/// 哪些格子算「河」（v2 P9，2026-10-20）：地質演化視角的「河流與湖」圖層、放大到走動模式的河道都用這一份判斷。
///
/// <see cref="Hydrology.Discharge"/> 是降水的相對單位 × 格，不是立方公尺／秒，所以門檻寫成**集水面積**
/// （CLAUDE.md 第 4 條：不寫格數）：流量至少等於 <see cref="MinDrainageAreaSquareKm"/> 的陸地平均逕流加起來，才畫成河。
/// </summary>
public static class Rivers
{
    /// <summary>
    /// 集水面積至少這麼大（平方公里）才算一條河。3 萬平方公里約是台灣的面積，地球上這種規模的河有幾百條；
    /// 再小的溪在 15 公里一格的網格上畫出來只是滿地雜訊。
    /// </summary>
    public const double MinDrainageAreaSquareKm = 10_000;

    /// <summary>陸地平均每格的逕流（降水的相對單位）：陸地平均降水 × <see cref="Climate.RunoffFraction"/>。</summary>
    /// <param name="state">模擬狀態。</param>
    public static float MeanLandRunoff(SimulationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        double sum = 0;
        var land = 0;
        for (var c = 0; c < state.CellCount; c++)
        {
            if (state.WaterDepth[c] > 0f) continue;
            sum += state.Precipitation[c];
            land++;
        }
        return land == 0 ? 0f : (float)(sum / land * Climate.RunoffFraction);
    }

    /// <summary>算成河的最小流量（<see cref="Hydrology.Discharge"/> 的單位）。</summary>
    /// <param name="grid">網格。</param>
    /// <param name="state">模擬狀態。</param>
    public static float MinDischarge(SimulationGrid grid, SimulationState state) => MinDischarge(grid, state, MinDrainageAreaSquareKm);

    /// <summary>集水面積至少 <paramref name="drainageAreaSquareKm"/> 的流量（<see cref="Hydrology.Discharge"/> 的單位）。</summary>
    /// <param name="grid">網格。</param>
    /// <param name="state">模擬狀態。</param>
    /// <param name="drainageAreaSquareKm">集水面積（平方公里）。</param>
    public static float MinDischarge(SimulationGrid grid, SimulationState state, double drainageAreaSquareKm)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var cells = drainageAreaSquareKm / (grid.AverageCellAreaSquareMeters / 1e6);
        return (float)(cells * MeanLandRunoff(state));
    }

    /// <summary>
    /// 放大到走動模式時畫出來的河：集水面積至少這麼大（平方公里，水文地貌規劃 H4）。
    /// 近看細分成 250 公尺的小六角之後，一千平方公里的小河也看得到；地質演化的圖層仍用 <see cref="MinDrainageAreaSquareKm"/>。
    /// </summary>
    public const double MinDrawnDrainageAreaSquareKm = 1_000;

    /// <summary>最小的河（剛好到 <see cref="MinDrawnDrainageAreaSquareKm"/>）的河寬（公尺）。</summary>
    public const double MinChannelWidthMeters = 20;

    /// <summary>河寬上限（公尺）：亞馬遜下游約 2~5 公里，取 2.5 公里。</summary>
    public const double MaxChannelWidthMeters = 2_500;

    /// <summary>
    /// 河寬（公尺）：水力幾何的經驗式，河寬約與流量的平方根成正比。流量是門檻的 6,000 倍（集水 600 萬平方公里）時約 1.5 公里。
    /// </summary>
    /// <param name="discharge">流量。</param>
    /// <param name="minDrawnDischarge">畫出來的門檻（集水 <see cref="MinDrawnDrainageAreaSquareKm"/>）。</param>
    public static double ChannelWidthMeters(float discharge, float minDrawnDischarge)
    {
        if (minDrawnDischarge <= 0f || discharge < minDrawnDischarge) return 0;
        return Math.Min(MaxChannelWidthMeters, MinChannelWidthMeters * Math.Sqrt(discharge / minDrawnDischarge));
    }

    /// <summary>
    /// 河的相對大小（0~1）：剛好到門檻是 0，流量是門檻的 <see cref="LargestRiverRatio"/> 倍以上是 1，中間取對數。
    /// 用來決定圖層的顏色深淺、走動模式的河寬與水深。
    /// </summary>
    /// <param name="discharge">流量。</param>
    /// <param name="minDischarge">門檻（<see cref="MinDischarge(SimulationGrid, SimulationState)"/>）。</param>
    public static float Size(float discharge, float minDischarge)
    {
        if (minDischarge <= 0f || discharge < minDischarge) return 0f;
        return Math.Clamp(MathF.Log(discharge / minDischarge) / MathF.Log(LargestRiverRatio), 0f, 1f);
    }

    /// <summary>
    /// 這一格是不是河。門檻是 0（還沒解過氣候、陸地沒有降水，例如第 0 步）時一律不是——
    /// 否則 0 ≥ 0，整片陸地都變成河。
    /// </summary>
    /// <param name="discharge">流量。</param>
    /// <param name="minDischarge">門檻（<see cref="MinDischarge(SimulationGrid, SimulationState)"/>）。</param>
    public static bool IsRiver(float discharge, float minDischarge) => minDischarge > 0f && discharge >= minDischarge;

    /// <summary>流量是門檻的這麼多倍就算最大的河（亞馬遜的集水面積約是 1 萬平方公里的 600 倍）。</summary>
    public const float LargestRiverRatio = 600f;
}
