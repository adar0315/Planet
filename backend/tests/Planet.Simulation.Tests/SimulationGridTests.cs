using Planet.Simulation;
using Xunit;

namespace Planet.Simulation.Tests;

/// <summary>
/// 粗網格的拓樸正確性測試。
///
/// 這裡的重點跟 <c>TileNeighborResolverTests</c> 一樣是**鄰接對稱性**——A的鄰居有B，
/// B的鄰居就必須有A。手推這類跨面座標公式很容易寫出「看起來對、實際不對稱」的結果
/// （見 docs/architecture.md「踩過的坑1~3」）。
///
/// 但只有對稱性還不夠：如果索引對應整組被打亂成另一種一致的排列，對稱性仍然會成立。
/// 所以另外加了**幾何合理性**檢查——鄰居必須真的在球面上就在旁邊，不能隔了半顆星球。
/// </summary>
public class SimulationGridTests
{
    private const double TestRadiusMeters = 1_852_259.92; // 跟正式星球同一個半徑

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void Build_CellCountMatchesFormula(int n)
    {
        var grid = SimulationGrid.Build(TestRadiusMeters, n);
        Assert.Equal(10 * n * n + 2, grid.CellCount);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void Build_HasExactlyTwelvePentagons(int n)
    {
        var grid = SimulationGrid.Build(TestRadiusMeters, n);
        Assert.Equal(12, grid.IsPentagon.Count(p => p));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void Build_NeighborCountMatchesShape(int n)
    {
        var grid = SimulationGrid.Build(TestRadiusMeters, n);
        var buffer = new int[SimulationGrid.MaxNeighborsPerCell];

        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            var count = grid.GetNeighbors(cell, buffer);
            var expected = grid.IsPentagon[cell] ? 5 : 6;
            Assert.True(count == expected,
                $"第{cell}格（{(grid.IsPentagon[cell] ? "五角形" : "六角形")}）應有{expected}個鄰居，實際{count}個");
        }
    }

    /// <summary>鄰接關係必須對稱——這是最能抓出座標公式錯誤的檢查。</summary>
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void Build_NeighborRelationIsSymmetric(int n)
    {
        var grid = SimulationGrid.Build(TestRadiusMeters, n);
        var buffer = new int[SimulationGrid.MaxNeighborsPerCell];
        var neighborSets = new HashSet<int>[grid.CellCount];

        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            var count = grid.GetNeighbors(cell, buffer);
            neighborSets[cell] = [.. buffer[..count]];
        }

        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            foreach (var neighbor in neighborSets[cell])
            {
                Assert.True(neighborSets[neighbor].Contains(cell),
                    $"第{cell}格的鄰居有第{neighbor}格，但第{neighbor}格的鄰居不含第{cell}格（不對稱）");
            }
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    public void Build_NeighborsAreDistinctAndNotSelf(int n)
    {
        var grid = SimulationGrid.Build(TestRadiusMeters, n);
        var buffer = new int[SimulationGrid.MaxNeighborsPerCell];

        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            var count = grid.GetNeighbors(cell, buffer);
            var neighbors = buffer[..count].ToArray();

            Assert.DoesNotContain(cell, neighbors);
            Assert.Equal(neighbors.Length, neighbors.Distinct().Count());
            Assert.All(neighbors, neighbor => Assert.InRange(neighbor, 0, grid.CellCount - 1));
        }
    }

    /// <summary>
    /// 幾何合理性：鄰居必須真的在旁邊。
    ///
    /// 對稱性測試抓不到「索引對應被整組打亂成另一種一致排列」這種錯誤，這個測試可以——
    /// 相鄰格中心的夾角應該跟格距同一個量級（約 180/n 度），如果對應錯了會出現隔半顆星球的「鄰居」。
    /// 上限抓 3 倍格距是刻意放寬的：測地線細分本來就不完全均勻，五角形附近的格距會偏離平均值。
    /// </summary>
    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void Build_NeighborsAreGeometricallyAdjacent(int n)
    {
        var grid = SimulationGrid.Build(TestRadiusMeters, n);
        var buffer = new int[SimulationGrid.MaxNeighborsPerCell];
        var expectedSpacingDegrees = 180.0 / n;
        var maxAllowedDegrees = expectedSpacingDegrees * 3;

        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            var count = grid.GetNeighbors(cell, buffer);
            for (var s = 0; s < count; s++)
            {
                var neighbor = buffer[s];
                var angleDegrees = AngleBetweenDegrees(grid, cell, neighbor);
                Assert.True(angleDegrees <= maxAllowedDegrees,
                    $"第{cell}格與其鄰居第{neighbor}格相距{angleDegrees:F2}度，超過預期格距{expectedSpacingDegrees:F2}度的3倍");
            }
        }
    }

    [Fact]
    public void Build_UsesSuppliedRadiusNotGeneratorDerivedOne()
    {
        // 生成器自己算的半徑是「六角形邊長固定1公里」反推的，粗頻率下會得到很小的星球
        //（n=16 只有約20公里）。模擬要代表的是同一顆1,852公里的星球，所以半徑必須由外面傳進來。
        var grid = SimulationGrid.Build(TestRadiusMeters, 16);
        Assert.Equal(TestRadiusMeters, grid.PlanetRadiusMeters);

        // 平均格面積 = 球面積 / 格數
        var expectedArea = 4.0 * Math.PI * TestRadiusMeters * TestRadiusMeters / grid.CellCount;
        Assert.Equal(expectedArea, grid.AverageCellAreaSquareMeters, 3);
    }

    private static double AngleBetweenDegrees(SimulationGrid grid, int a, int b)
    {
        var dot = grid.UnitVector[a * 3] * grid.UnitVector[b * 3]
                  + grid.UnitVector[a * 3 + 1] * grid.UnitVector[b * 3 + 1]
                  + grid.UnitVector[a * 3 + 2] * grid.UnitVector[b * 3 + 2];
        return Math.Acos(Math.Clamp(dot, -1.0, 1.0)) * 180.0 / Math.PI;
    }
}
