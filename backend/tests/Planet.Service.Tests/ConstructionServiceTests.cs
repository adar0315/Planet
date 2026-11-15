using Planet.Domain;
using Planet.Service;
using Planet.Shared;
using static Planet.Service.Tests.TestWorld;

namespace Planet.Service.Tests;

/// <summary>
/// <see cref="ConstructionService"/>（編輯模式：蓋、挖、填、拆）。規則本身在 <c>ConstructionRulesTests</c>；
/// 這裡守的是服務把「目前的修改」「原本的地表」「規則」接起來的方式，以及同時有好幾個請求時城堡不會超過上限。
/// </summary>
public class ConstructionServiceTests
{
    private sealed record Fixture(ConstructionService Service, FakeConstructionRepository Repository, IReadOnlyList<SubTileDto> SubTiles);

    private static async Task<Fixture> CreateAsync(List<Tile>? parents = null)
    {
        parents ??= LandPatch();
        var setup = Create(parents);
        var subTiles = await setup.Service.GetSubTilesAsync(parents.Select(p => p.TileId).ToList(), default);
        return new Fixture(setup.Construction(), setup.Constructions, subTiles);
    }

    private static Task<(ConstructionResultDto? Result, string? Error, bool NotFound)> Do(Fixture f, SubTileDto sub, string action) =>
        f.Service.ApplyAsync(new ConstructionRequest(sub.SubTileId, sub.ParentTileId, action), default);

    /// <summary>從某一格開始沿鄰居往外找，湊出 count 個彼此相連的小格（全在這一批裡）。</summary>
    private static List<SubTileDto> Connected(IReadOnlyList<SubTileDto> subTiles, int count)
    {
        var byId = subTiles.ToDictionary(s => s.SubTileId);
        var start = subTiles[subTiles.Count / 2];
        var result = new List<SubTileDto> { start };
        var seen = new HashSet<long> { start.SubTileId };
        for (var i = 0; i < result.Count && result.Count < count; i++)
        {
            foreach (var id in result[i].NeighborIds)
            {
                if (id is not { } n || !byId.ContainsKey(n) || !seen.Add(n)) continue;
                result.Add(byId[n]);
                if (result.Count == count) break;
            }
        }
        Assert.Equal(count, result.Count);
        return result;
    }

    [Fact]
    public async Task House_OnLand_IsSaved_AndTheSameAgainIsRefused()
    {
        var f = await CreateAsync();
        var sub = f.SubTiles[10];

        var (result, error, notFound) = await Do(f, sub, "House");

        Assert.Null(error);
        Assert.False(notFound);
        Assert.Equal(new ConstructionResultDto(sub.SubTileId, "House", null, false), result);
        var saved = Assert.Single(f.Repository.Items);
        Assert.Equal((sub.SubTileId, sub.ParentTileId, StructureKind.House), (saved.SubTileId, saved.ParentTileId, saved.Structure));

        var again = await Do(f, sub, "house"); // 動作名稱不分大小寫
        Assert.Null(again.Result);
        Assert.Contains("一樣", again.Error);
    }

    [Fact]
    public async Task ADifferentStructure_ReplacesTheOldOne()
    {
        var f = await CreateAsync();
        var sub = f.SubTiles[10];

        await Do(f, sub, "House");
        var (result, error, _) = await Do(f, sub, "Farm");

        Assert.Null(error);
        Assert.Equal("Farm", result!.Structure);
        Assert.Equal(StructureKind.Farm, Assert.Single(f.Repository.Items).Structure);
    }

    [Fact]
    public async Task WaterAndLandRules_FollowTheOriginalSurface()
    {
        var land = await CreateAsync();
        var sea = await CreateAsync(SeaPatch());

        Assert.NotNull((await Do(land, land.SubTiles[3], "Bridge")).Error); // 陸地上不能蓋橋
        Assert.NotNull((await Do(sea, sea.SubTiles[3], "House")).Error); // 海上不能蓋房子
        Assert.Null((await Do(sea, sea.SubTiles[3], "Bridge")).Error);
        Assert.Empty(land.Repository.Items); // 被拒絕的不會存
    }

    [Fact]
    public async Task Channel_MakesLandIntoWater_SoBridgesFitAndHousesDoNot()
    {
        var f = await CreateAsync();
        var sub = f.SubTiles[10];

        var (dug, error, _) = await Do(f, sub, "Channel");
        Assert.Null(error);
        Assert.Equal(new ConstructionResultDto(sub.SubTileId, null, "Channel", true), dug);

        Assert.NotNull((await Do(f, sub, "Channel")).Error); // 已經是水了
        Assert.NotNull((await Do(f, sub, "House")).Error); // 挖過的地方是水
        var (bridge, bridgeError, _) = await Do(f, sub, "Bridge");
        Assert.Null(bridgeError);
        Assert.Equal(new ConstructionResultDto(sub.SubTileId, "Bridge", "Channel", false), bridge);
        Assert.NotNull((await Do(f, sub, "Fill")).Error); // 上面有橋，先拆
    }

    [Fact]
    public async Task Demolish_RemovesTheStructureFirst_ThenTheTerrainEdit_ThenRefuses()
    {
        var f = await CreateAsync();
        var sub = f.SubTiles[10];
        Assert.NotNull((await Do(f, sub, "Demolish")).Error); // 什麼都沒有

        await Do(f, sub, "Channel");
        await Do(f, sub, "Bridge");

        var first = await Do(f, sub, "Demolish");
        Assert.Equal(new ConstructionResultDto(sub.SubTileId, null, "Channel", false), first.Result);
        var second = await Do(f, sub, "Demolish");
        Assert.Equal(new ConstructionResultDto(sub.SubTileId, null, null, true), second.Result);
        Assert.NotNull((await Do(f, sub, "Demolish")).Error);
    }

    [Fact]
    public async Task Fill_OnTheSea_ThenChannel_CancelsOut()
    {
        var f = await CreateAsync(SeaPatch());
        var sub = f.SubTiles[10];

        var filled = await Do(f, sub, "Fill");
        Assert.Equal(new ConstructionResultDto(sub.SubTileId, null, "Fill", true), filled.Result);
        Assert.Null((await Do(f, sub, "House")).Error); // 填過的地方是陸地

        Assert.NotNull((await Do(f, sub, "Channel")).Error); // 上面有房子
        await Do(f, sub, "Demolish");
        var undone = await Do(f, sub, "Channel");
        // 填平再挖回來＝回到原本的海，不是在填出來的陸地上再挖一條河道
        Assert.Equal(new ConstructionResultDto(sub.SubTileId, null, null, true), undone.Result);
    }

    [Theory]
    [InlineData("Spaceship")]
    [InlineData("7")] // Enum.TryParse 會吃數字字串
    [InlineData("1")]
    [InlineData("")]
    public async Task UnknownActions_AreRefused_WithoutSavingAnything(string action)
    {
        var f = await CreateAsync();

        var (result, error, notFound) = await Do(f, f.SubTiles[10], action);

        Assert.Null(result);
        Assert.NotNull(error);
        Assert.False(notFound);
        Assert.Empty(f.Repository.Items);
    }

    [Fact]
    public async Task UnknownSubTileOrParent_IsNotFound()
    {
        var f = await CreateAsync();
        var sub = f.SubTiles[10];

        Assert.True((await f.Service.ApplyAsync(new ConstructionRequest(123, sub.ParentTileId, "House"), default)).NotFound);
        Assert.True((await f.Service.ApplyAsync(new ConstructionRequest(sub.SubTileId, 999_999_999, "House"), default)).NotFound);
        Assert.Empty(f.Repository.Items);
    }

    [Fact]
    public async Task Castle_UpToSevenConnectedCells_TheEighthIsRefused()
    {
        var f = await CreateAsync();
        var cells = Connected(f.SubTiles, ConstructionRules.MaxCastleCells + 1);

        foreach (var cell in cells.Take(ConstructionRules.MaxCastleCells))
            Assert.Null((await Do(f, cell, "Castle")).Error);
        var eighth = await Do(f, cells[^1], "Castle");

        Assert.Contains("最多", eighth.Error);
        Assert.Equal(ConstructionRules.MaxCastleCells, f.Repository.Items.Count);
    }

    [Fact]
    public async Task Castle_SimultaneousRequests_NeverExceedTheLimit()
    {
        // 服務裡的鎖就是為了這個：沒有鎖的話，每個請求都看到「旁邊還沒滿」而同時通過。
        // 剛好 8 格相連：不論誰先誰後，8 格全蓋就是一座 8 格的城堡，所以一定恰好有一格被拒。
        // （格子再多就不成立：上限是「每座相連的城堡」，成功的格子可能分成兩座、總數超過 7 是合法的。）
        var f = await CreateAsync();
        var cells = Connected(f.SubTiles, ConstructionRules.MaxCastleCells + 1);
        // 每次存取都要等一下，請求之間才會真的交錯（見 FakeConstructionRepository.Latency）
        f.Repository.Latency = TimeSpan.FromMilliseconds(5);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var pending = cells.Select(cell => Task.Run(async () =>
        {
            await start.Task;
            return await Do(f, cell, "Castle");
        })).ToList();
        start.SetResult();
        var outcomes = await Task.WhenAll(pending);

        Assert.Equal(ConstructionRules.MaxCastleCells, outcomes.Count(o => o.Error is null));
        Assert.Equal(ConstructionRules.MaxCastleCells, f.Repository.Items.Count(c => c.Structure == StructureKind.Castle));
    }
}
