using Planet.Generation;

namespace Planet.Simulation.Tests;

/// <summary>
/// 測試用的星球參數——**一律向權威來源要，不要寫死**。
///
/// 2026-10-16 之前每個測試檔各自寫死 <c>1_852_259.92</c>，而正式服務用的是
/// <see cref="GoldbergPlanetGenerator"/> 算出來、存在資料庫裡的 <c>1852259.924347263</c>——**差 4.3 毫米**。
/// 對畫面毫無影響，但模擬是混沌的：實測 n=150 同一個種子，半徑差這 4.3 毫米，
/// **第 1 步就有 11 個欄位不同**（而 1 核 vs 8 核跑 200 步是位元相同的）。
///
/// 上一個 session 因此看到「線上第 1,975 步、離線約 2,350 步才到陸地 35%」，一度當成跨行程的非決定性在追，
/// 還為此修了水量加總的切片數。那個修正本身沒錯，但不是那次分歧的原因——兩邊跑的本來就是兩顆不同的星球。
///
/// CLAUDE.md 有一條「星球的尺寸只有一個權威來源」，原本是寫給前端的；測試同樣適用。
/// </summary>
public static class TestPlanet
{
    /// <summary>正式星球的半徑（公尺），跟 <c>PlanetMetadata</c> 裡存的、服務實際用的是同一個值。</summary>
    public static double RadiusMeters { get; } = new GoldbergPlanetGenerator().PlanetRadiusMeters;
}
