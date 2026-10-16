using Planet.Simulation;
using Xunit;

namespace Planet.Simulation.Tests;

/// <summary>
/// 貼圖產生器的測試。重點在**像素→格子的對應表**：
/// 這張表如果算錯，畫出來的星球會扭曲或在極區出現怪異的抹片，
/// 而那種錯誤很容易被誤認成「模擬本身跑錯了」，浪費除錯時間。
/// </summary>
public class SimulationFieldRendererTests
{
    private static readonly double TestRadiusMeters = TestPlanet.RadiusMeters;

    [Fact]
    public void Render_ProducesCorrectlySizedOpaqueRgba()
    {
        var grid = SimulationGrid.Build(TestRadiusMeters, 16);
        var state = SimulationState.CreateInitialOceanWorld(grid);
        var renderer = new SimulationFieldRenderer(grid);

        var pixels = renderer.Render(state, SimulationField.Elevation);

        Assert.Equal(SimulationFieldRenderer.Width * SimulationFieldRenderer.Height * 4, pixels.Length);
        for (var i = 3; i < pixels.Length; i += 4)
        {
            Assert.Equal(255, pixels[i]);
        }
    }

    /// <summary>
    /// renderer 用 bucket 加速選出來的格子，必須跟暴力法算出的最近格**完全一致**。
    ///
    /// 這是驗證 bucket 搜尋（特別是極區把經度搜尋範圍除以 cos(緯度) 那段）有沒有寫對——
    /// 如果極區搜尋範圍不夠寬會抓到錯誤的格子，而畫面上不一定看得出來，
    /// 之後很容易被誤認成「模擬本身跑錯」。抽樣刻意涵蓋兩極、赤道與換日線。
    ///
    /// 允許「距離相同但索引不同」的情況（正好等距的平手），所以比的是角距離不是索引。
    /// </summary>
    [Fact]
    public void PixelToCellMap_PicksTheSameCellAsBruteForceNearest()
    {
        const int n = 24;
        var grid = SimulationGrid.Build(TestRadiusMeters, n);
        var renderer = new SimulationFieldRenderer(grid);

        // 全部 524,288 個像素跑暴力法太慢，抽樣即可——但要刻意涵蓋最容易出錯的位置：
        // 第0/最後一列（極點）、換日線兩側（經度環狀繞回）、赤道。
        int[] sampleRows =
        [
            0, 1, 3, 10,
            SimulationFieldRenderer.Height / 2,
            SimulationFieldRenderer.Height - 11,
            SimulationFieldRenderer.Height - 4,
            SimulationFieldRenderer.Height - 1,
        ];
        int[] sampleCols =
        [
            0, 1,
            SimulationFieldRenderer.Width / 4,
            SimulationFieldRenderer.Width / 2,
            SimulationFieldRenderer.Width - 2,
            SimulationFieldRenderer.Width - 1,
        ];

        var checkedPixels = 0;
        foreach (var y in sampleRows)
        {
            var latitude = 90.0 - (y + 0.5) / SimulationFieldRenderer.Height * 180.0;
            foreach (var x in sampleCols)
            {
                var longitude = (x + 0.5) / SimulationFieldRenderer.Width * 360.0 - 180.0;

                var chosen = renderer.CellAtPixel(x, y);
                var chosenDegrees = AngleToCellDegrees(grid, latitude, longitude, chosen);
                var bestDegrees = BruteForceNearestAngleDegrees(grid, latitude, longitude);

                Assert.True(chosenDegrees - bestDegrees < 1e-9,
                    $"像素({x},{y}) 緯{latitude:F2} 經{longitude:F2}：renderer選了第{chosen}格（距離{chosenDegrees:F4}度），" +
                    $"但最近的格子只有{bestDegrees:F4}度——bucket 搜尋範圍不夠寬");
                checkedPixels++;
            }
        }

        Assert.Equal(sampleRows.Length * sampleCols.Length, checkedPixels);
    }

    /// <summary>
    /// 起始狀態畫出來要**以海為主、但看得到陸地**。
    ///
    /// （原本斷言的是「整顆星球都是海」，2026-09-06 使用者取消了「完全被海水覆蓋」的起始條件，
    /// 因為完全均質的起點跑不出大陸。）
    /// </summary>
    [Fact]
    public void Render_InitialStateIsMostlyWaterWithSomeLand()
    {
        var grid = SimulationGrid.Build(TestRadiusMeters, 16);
        var state = SimulationState.CreateInitialOceanWorld(grid);
        var renderer = new SimulationFieldRenderer(grid);

        var pixels = renderer.Render(state, SimulationField.Elevation);

        // 水色的判準：藍色通道明顯高於紅色通道
        var waterPixels = 0;
        var totalPixels = pixels.Length / 4;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 2] > pixels[i]) waterPixels++;
        }

        var waterFraction = (double)waterPixels / totalPixels;
        Assert.True(waterFraction is > 0.5 and < 1.0,
            $"起始畫面的水域佔 {waterFraction:P1}——應該以海為主但看得到早期大轟炸留下的原始陸塊");
    }

    [Fact]
    public void Render_DifferentFieldsProduceDifferentImages()
    {
        var grid = SimulationGrid.Build(TestRadiusMeters, 16);
        var state = SimulationState.CreateInitialOceanWorld(grid);
        var renderer = new SimulationFieldRenderer(grid);

        var elevation = renderer.Render(state, SimulationField.Elevation);
        var temperature = renderer.Render(state, SimulationField.Temperature);

        Assert.False(elevation.SequenceEqual(temperature),
            "高程圖跟溫度圖畫出來一模一樣，代表欄位選擇沒有生效");
    }

    /// <summary>指定經緯度到某一格中心的角距離（度）。</summary>
    private static double AngleToCellDegrees(SimulationGrid grid, double latitude, double longitude, int cell)
    {
        var (px, py, pz) = ToUnitVector(latitude, longitude);
        var dot = px * grid.UnitVector[cell * 3]
                  + py * grid.UnitVector[cell * 3 + 1]
                  + pz * grid.UnitVector[cell * 3 + 2];
        return Math.Acos(Math.Clamp(dot, -1.0, 1.0)) * 180.0 / Math.PI;
    }

    private static (double X, double Y, double Z) ToUnitVector(double latitude, double longitude)
    {
        var latRad = latitude * Math.PI / 180.0;
        var lonRad = longitude * Math.PI / 180.0;
        var cosLat = Math.Cos(latRad);
        return (cosLat * Math.Cos(lonRad), cosLat * Math.Sin(lonRad), Math.Sin(latRad));
    }

    /// <summary>暴力法找出離指定經緯度最近的格子，回傳角距離（度）。只在測試裡用，正式程式碼用 bucket 加速。</summary>
    private static double BruteForceNearestAngleDegrees(SimulationGrid grid, double latitude, double longitude)
    {
        var latRad = latitude * Math.PI / 180.0;
        var lonRad = longitude * Math.PI / 180.0;
        var cosLat = Math.Cos(latRad);
        var px = cosLat * Math.Cos(lonRad);
        var py = cosLat * Math.Sin(lonRad);
        var pz = Math.Sin(latRad);

        var bestDot = double.NegativeInfinity;
        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            var dot = px * grid.UnitVector[cell * 3]
                      + py * grid.UnitVector[cell * 3 + 1]
                      + pz * grid.UnitVector[cell * 3 + 2];
            if (dot > bestDot) bestDot = dot;
        }

        return Math.Acos(Math.Clamp(bestDot, -1.0, 1.0)) * 180.0 / Math.PI;
    }
}
