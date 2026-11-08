using Planet.Domain;
using Xunit;

namespace Planet.Generation.Tests;

/// <summary>小格的河道從水源往低處挖（2026-11-08）：不高過兩岸、不往高處流、高地邊緣不變成長柱。</summary>
public class RiverChannelShapingTests
{
    private static SubTileSurface River(int level) => new(TerrainType.Gravel, 0, level, 0, 7, 50, 120, false);
    private static SubTileSurface Land(int level) => new(TerrainType.Grassland, 0, level, 0, null, 0, 0, false);

    /// <summary>
    /// 一條往下游的河（索引 0..n-1），每格兩邊各一格岸（索引 n+2i、n+2i+1）。回傳挖好之後河的高度。
    /// </summary>
    private static int[] Dig(int[] riverBed, int[] leftBank, int[] rightBank)
    {
        var n = riverBed.Length;
        var surfaces = new List<SubTileSurface>();
        surfaces.AddRange(riverBed.Select(River));
        for (var i = 0; i < n; i++)
        {
            surfaces.Add(Land(leftBank[i]));
            surfaces.Add(Land(rightBank[i]));
        }
        var neighbours = new List<int>[surfaces.Count];
        for (var i = 0; i < surfaces.Count; i++) neighbours[i] = [];
        void Link(int a, int b) { neighbours[a].Add(b); neighbours[b].Add(a); }
        for (var i = 0; i < n; i++)
        {
            if (i + 1 < n) Link(i, i + 1);
            Link(i, n + 2 * i);
            Link(i, n + 2 * i + 1);
        }
        var shaped = RiverChannelShaping.Shape(surfaces, neighbours);
        // 岸不動
        for (var i = n; i < surfaces.Count; i++) Assert.Equal(surfaces[i], shaped[i]);
        return shaped.Take(n).Select(s => s.ElevationLevel).ToArray();
    }

    [Fact]
    public void RiverIsNeverAboveItsBanks()
    {
        // 原本的河床 50，兩岸 40：河高出地面。挖下去之後比低的那一岸再低一級
        Assert.Equal([39], Dig([50], [40], [45]));
    }

    [Fact]
    public void RiverInAValleyKeepsItsBed()
    {
        // 河床本來就比兩岸低很多（谷底）：不會被拉高到岸邊
        Assert.Equal([10], Dig([10], [80], [90]));
    }

    [Fact]
    public void PlateauEdgeIsDugDownInsteadOfAColumn()
    {
        // 高地上的河 100、90，在第二格碰到高地邊緣（一邊是 20 的崖下），之後都在崖下
        var levels = Dig([100, 90, 80], [120, 120, 20], [120, 20, 25]);
        Assert.Equal([100, 19, 19], levels);
    }

    [Fact]
    public void WaterNeverFlowsUphill()
    {
        // 中間那格岸很低，被挖到 39；下游的岸又變高，河也不能升回去
        var levels = Dig([60, 58, 56], [70, 40, 70], [70, 45, 70]);
        Assert.Equal([60, 39, 39], levels);
        for (var i = 1; i < levels.Length; i++) Assert.True(levels[i] <= levels[i - 1]);
    }

    [Fact]
    public void RiverMouthStaysAtSeaLevel()
    {
        Assert.Equal([0], Dig([3], [0], [0]));
    }

    [Fact]
    public void LakesAndLandAreUntouched()
    {
        var lake = new SubTileSurface(TerrainType.Freshwater, 2, 95, 100, null, 0, 0, false);
        var land = Land(30);
        var shaped = RiverChannelShaping.Shape([lake, land], [[1], [0]]);
        Assert.Equal(lake, shaped[0]);
        Assert.Equal(land, shaped[1]);
    }

    [Fact]
    public void RiverWithAWaterSurfaceIsDugDownWithItsWater()
    {
        // 實際踩到的：河道是「有水面的淡水」，河床 162、水面 163，旁邊的地 1 級——高地邊緣的水柱
        var column = new SubTileSurface(TerrainType.Freshwater, 1, 162, 163, 7, 30, 45, false);
        var shaped = RiverChannelShaping.Shape([column, Land(1), Land(3)], [[1, 2], [0], [0]]);
        Assert.Equal(0, shaped[0].ElevationLevel); // 1 − 1 級
        Assert.Equal(1, shaped[0].SeaLevel - shaped[0].ElevationLevel); // 水深不變
    }
}
