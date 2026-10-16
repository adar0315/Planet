using Planet.Simulation;
using Xunit;

namespace Planet.Simulation.Tests;

/// <summary>
/// 板塊歸屬的增量重新分配（只重掃邊界附近）必須跟完整重掃完全一致。
///
/// 這道測試守的是一個**安靜的**失效模式：增量分配如果漏掉某些會換板塊的格子，
/// 板塊歸屬就會慢慢偏離正確答案，而畫面上只是板塊形狀有點怪、沒有任何錯誤訊息。
/// 板塊歸屬錯了之後，邊界分類、聚合速率、標記歸屬全部跟著錯。
/// </summary>
public class PlateAssignmentTests
{
    private static readonly double TestRadiusMeters = TestPlanet.RadiusMeters;

    [Fact]
    public void Advance_IncrementalAssignmentMatchesFullScan()
    {
        var grid = SimulationGrid.Build(TestRadiusMeters, 24);
        var engine = new SimulationEngine(grid, 42);

        // 一開始就要一致（建構時是全掃）
        Assert.Equal(0, engine.Plates.CountAssignmentMismatchesAgainstFullScan(grid));

        // 跑久一點，讓邊界掃過大量格子、也讓子種子有機會漂進鄰居的領域
        for (var block = 0; block < 4; block++)
        {
            engine.Step(500);
            var mismatches = engine.Plates.CountAssignmentMismatchesAgainstFullScan(grid);
            Assert.True(mismatches == 0,
                $"{engine.State.ElapsedYears / 1e6:F0} 百萬年後有 {mismatches} 格的板塊歸屬" +
                "與完整重掃不一致——增量分配的「只有邊界附近會換板塊」推論不成立");
        }
    }
}
