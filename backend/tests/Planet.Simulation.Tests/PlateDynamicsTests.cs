using System.Linq;
using Xunit;

namespace Planet.Simulation.Tests;

/// <summary>
/// 板塊動力學（<see cref="PlateDynamics"/>）的守門測試。
/// </summary>
public class PlateDynamicsTests
{
    private static readonly double TestRadiusMeters = TestPlanet.RadiusMeters;

    private static SimulationEngine CreateEngine(int seed = 42) =>
        new(SimulationGrid.Build(TestRadiusMeters, 24), seed);

    /// <summary>
    /// 板塊被完全吃掉時，力矩解不可以變成 NaN。
    ///
    /// 2026-09-09 踩過的坑，而且失效方式是全域的：<c>SolveSymmetric3x3</c> 對
    /// 「一格都不剩」的板塊會算出 <c>scale = 0</c>，原本的奇異值門檻因此變成 `0 &lt; 0`
    /// ——是 false，於是直接除以 <c>determinant = 0</c> 解出 NaN。接著
    /// <c>RemoveNetRotation</c> 把 NaN 平均給**每一個**板塊，種子全部變 NaN，
    /// Voronoi 的比較全落到 false，所有格子塌進 0 號板塊，
    /// 最後 <c>SolveSeaLevel</c> 在 NaN 地形上卡成無窮迴圈。
    ///
    /// **症狀完全不像數值問題**：測試不會失敗、不會拋例外、記憶體也不漲，
    /// 只是永遠不結束（實測四個測試各自空轉 9 小時）。所以這裡直接斷言
    /// 「跑久了板塊還在、旋轉還是有限值」，把三個環節一起守住。
    ///
    /// 3,163 步是實測第一次歸零的位置（n=24、種子42），這裡跑 4,000 步蓋過它。
    /// </summary>
    [Fact]
    public void Step_PlateLosingAllCellsDoesNotPoisonEveryPlate()
    {
        var engine = CreateEngine();
        engine.Step(4000);

        Assert.All(engine.Plates.Plates, plate =>
        {
            Assert.True(
                double.IsFinite(plate.PoleX) && double.IsFinite(plate.PoleY)
                    && double.IsFinite(plate.PoleZ)
                    && double.IsFinite(plate.AngularVelocityDegPerMyr),
                $"板塊 {plate.Id} 的旋轉不是有限值："
                + $"極({plate.PoleX}, {plate.PoleY}, {plate.PoleZ})、"
                + $"角速度 {plate.AngularVelocityDegPerMyr}");
        });

        Assert.True(
            float.IsFinite(engine.State.SeaLevelMeters),
            $"海平面不是有限值：{engine.State.SeaLevelMeters}");

        var counts = new int[engine.Plates.Plates.Length];
        foreach (var p in engine.Plates.PlateIdByCell)
        {
            if ((uint)p < (uint)counts.Length) counts[p]++;
        }

        var occupied = counts.Count(c => c > 0);
        Assert.True(
            occupied >= 5,
            $"只剩 {occupied} 個板塊還有格子（各板塊格數：{string.Join(",", counts)}）"
            + "——板塊全部塌進同一塊是 NaN 污染的典型症狀。");
    }
}
