using Planet.Domain;
using Xunit;

namespace Planet.Generation.Tests;

/// <summary>編輯模式的規則（2026-11-08）：什麼能蓋在哪裡、挖河道與填平之後的地形、相反的修改等於復原。</summary>
public class ConstructionRulesTests
{
    private static readonly SubTileSurface Land = new(TerrainType.Grassland, 1, 40, 0, null, 0, 0, false);
    private static readonly SubTileSurface Sea = new(TerrainType.Seawater, 3, -20, 0, null, 0, 0, false);
    private static readonly SubTileSurface Lake = new(TerrainType.Freshwater, 2, 95, 100, null, 0, 0, false);

    /// <summary>河中心線 100 公尺內、河寬 60 公尺：比畫面上最少畫的半寬（220 公尺）還近，所以畫成河。</summary>
    private static readonly SubTileSurface RiverBed = new(TerrainType.Gravel, 0, 30, 0, 7, 100, 60, false);

    /// <summary>同一條河，但離中心線 900 公尺：是河岸。</summary>
    private static readonly SubTileSurface RiverBank = RiverBed with { RiverDistanceMeters = 900 };

    [Fact]
    public void WaterIncludesSeaLakeAndVisibleRiver()
    {
        Assert.True(ConstructionRules.IsWater(Sea));
        Assert.True(ConstructionRules.IsWater(Lake));
        Assert.True(ConstructionRules.IsWater(RiverBed));
        Assert.False(ConstructionRules.IsWater(RiverBank));
        Assert.False(ConstructionRules.IsWater(Land));
    }

    [Theory]
    [InlineData(StructureKind.House)]
    [InlineData(StructureKind.Castle)]
    [InlineData(StructureKind.Road)]
    public void LandStructuresOnlyOnLand(StructureKind kind)
    {
        Assert.True(ConstructionRules.CanPlace(Land, kind, out _));
        Assert.True(ConstructionRules.CanPlace(RiverBank, kind, out _));
        Assert.False(ConstructionRules.CanPlace(Sea, kind, out var reason));
        Assert.NotNull(reason);
        Assert.False(ConstructionRules.CanPlace(RiverBed, kind, out _));
    }

    [Fact]
    public void BridgeOnlyOnWater()
    {
        Assert.True(ConstructionRules.CanPlace(RiverBed, StructureKind.Bridge, out _));
        Assert.True(ConstructionRules.CanPlace(Lake, StructureKind.Bridge, out _));
        Assert.False(ConstructionRules.CanPlace(Land, StructureKind.Bridge, out _));
    }

    [Fact]
    public void ChannelIsWaterBelowTheOldGround()
    {
        var channel = ConstructionRules.Apply(Land, TerrainEditKind.Channel);
        Assert.True(ConstructionRules.IsWater(channel));
        Assert.True(channel.ElevationLevel < channel.SeaLevel);
        Assert.True(channel.SeaLevel < Land.ElevationLevel); // 水面略低於原地面
        Assert.Null(channel.RiverCellId); // 不跟季節變：是靜止的水
        Assert.True(ConstructionRules.CanPlace(channel, StructureKind.Bridge, out _));
    }

    [Fact]
    public void FillRaisesToBanksAndAtLeastToWaterSurface()
    {
        var river = ConstructionRules.Apply(RiverBed, TerrainEditKind.Fill, landNeighbourLevel: 36);
        Assert.False(ConstructionRules.IsWater(river));
        Assert.Equal(36, river.ElevationLevel);

        // 湖中間沒有相鄰陸地：填到湖面
        var lake = ConstructionRules.Apply(Lake, TerrainEditKind.Fill);
        Assert.False(ConstructionRules.IsWater(lake));
        Assert.Equal(Lake.SeaLevel, lake.ElevationLevel);

        // 相鄰陸地比水面低也不會填到水面下
        var sea = ConstructionRules.Apply(Sea, TerrainEditKind.Fill, landNeighbourLevel: -5);
        Assert.False(ConstructionRules.IsWater(sea));
        Assert.Equal(0, sea.ElevationLevel);
    }

    [Fact]
    public void OppositeEditRestoresOriginal()
    {
        Assert.True(ConstructionRules.TryEditTerrain(Land, null, null, TerrainEditKind.Channel, out var dug, out _));
        Assert.Equal(TerrainEditKind.Channel, dug);
        Assert.True(ConstructionRules.TryEditTerrain(Land, dug, null, TerrainEditKind.Fill, out var restored, out _));
        Assert.Null(restored);

        Assert.True(ConstructionRules.TryEditTerrain(Sea, null, null, TerrainEditKind.Fill, out var filled, out _));
        Assert.Equal(TerrainEditKind.Fill, filled);
        Assert.True(ConstructionRules.TryEditTerrain(Sea, filled, null, TerrainEditKind.Channel, out var back, out _));
        Assert.Null(back);
    }

    [Fact]
    public void TerrainEditsRejectWrongSurfaceOrStructures()
    {
        Assert.False(ConstructionRules.TryEditTerrain(Sea, null, null, TerrainEditKind.Channel, out _, out _));
        Assert.False(ConstructionRules.TryEditTerrain(Land, null, null, TerrainEditKind.Fill, out _, out _));
        Assert.False(ConstructionRules.TryEditTerrain(Land, null, StructureKind.House, TerrainEditKind.Channel, out _, out var reason));
        Assert.NotNull(reason);
        Assert.False(ConstructionRules.TryEditTerrain(RiverBed, null, StructureKind.Bridge, TerrainEditKind.Fill, out _, out _));
    }
}
