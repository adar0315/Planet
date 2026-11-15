using Planet.Domain;
using Planet.Generation;
using Planet.Service;
using Planet.Shared;
using Planet.Simulation;
using static Planet.Service.Tests.TestWorld;

namespace Planet.Service.Tests;

/// <summary>
/// <see cref="SubTileService"/> 整個流程的測試：查地塊與玩家修改 → 幾何 → 取樣 → 挖河道 → 疊玩家修改 → DTO。
///
/// 倉儲是假的（記憶體），其餘都是真的：正式頻率（1415）的地塊與小格幾何、真的取樣器、真的河道整形與編輯規則。
/// 各個零件自己有測試（<c>SubTileGridTests</c>、<c>RiverChannelShapingTests</c>、<c>ConstructionRulesTests</c>），
/// 這裡守的是「接起來的順序與對應」：玩家修改有沒有對到正確的小格、地形修改看的是不是整形前的兩岸、
/// 挖河道有沒有真的套用在取樣結果上——這些錯了不會有例外，只會讓近看的畫面不對。
/// </summary>
public class SubTileServiceTests
{
    private static SubTileSurface SurfaceOf(SubTileDto dto) => new(
        Enum.Parse<TerrainType>(dto.Terrain), dto.HeightCategory, dto.ElevationLevel, dto.SeaLevel,
        dto.RiverCellId, dto.RiverDistanceMeters, dto.RiverWidthMeters, dto.Floodplain, dto.SourceCellId);

    // ───────────────────────── 程序化地形：小格照抄所屬地塊 ─────────────────────────

    [Fact]
    public async Task EverySubTileCopiesItsOwnParent_AndEveryParentHasChildren()
    {
        var parents = LandPatch();
        var service = Create(parents).Service;

        var result = await service.GetSubTilesAsync(parents.Select(p => p.TileId).ToList(), default);

        var byId = parents.ToDictionary(p => p.TileId);
        Assert.Equal(result.Count, result.Select(s => s.SubTileId).Distinct().Count());
        Assert.All(result, sub =>
        {
            var parent = byId[sub.ParentTileId];
            Assert.Equal(parent.Terrain.ToString(), sub.Terrain);
            Assert.Equal(parent.HeightCategory, sub.HeightCategory);
            Assert.Equal(parent.ElevationLevel, sub.ElevationLevel);
            Assert.Equal(parent.SeaLevel, sub.SeaLevel);
            Assert.Null(sub.Structure);
            Assert.Null(sub.TerrainEdit);
            // 座標四捨五入到 6 位（約 0.1 公尺），跟地塊的 DTO 一致
            Assert.Equal(Math.Round(sub.Latitude, 6), sub.Latitude);
            Assert.Equal(Math.Round(sub.Longitude, 6), sub.Longitude);
        });
        Assert.Equal(parents.Count, result.Select(s => s.ParentTileId).Distinct().Count());
        // 細分 4 倍：一個六角地塊約 16 個小格
        Assert.InRange(result.Count / (double)parents.Count, 14, 18);
    }

    [Fact]
    public async Task NeighborIdsAreMutualWithinTheBatch()
    {
        var parents = LandPatch();
        var result = await Create(parents).Service.GetSubTilesAsync(parents.Select(p => p.TileId).ToList(), default);

        var byId = result.ToDictionary(s => s.SubTileId);
        var linked = 0;
        foreach (var sub in result)
        {
            foreach (var id in sub.NeighborIds)
            {
                if (id is null || !byId.TryGetValue(id.Value, out var other)) continue;
                Assert.Contains(sub.SubTileId, other.NeighborIds);
                linked++;
            }
        }
        Assert.True(linked > result.Count * 4, "這一片是相連的，大部分小格的鄰居都應該在同一批裡");
    }

    [Fact]
    public async Task UnknownParentsAreSkipped_AndDuplicatesDoNotDuplicateSubTiles()
    {
        var parents = LandPatch();
        var service = Create(parents).Service;
        var first = parents[0].TileId;

        Assert.Empty(await service.GetSubTilesAsync([999_999_999], default));
        var once = await service.GetSubTilesAsync([first], default);
        var mixed = await service.GetSubTilesAsync([first, 999_999_999, first], default);

        Assert.NotEmpty(once);
        Assert.Equal(once.Select(s => s.SubTileId), mixed.Select(s => s.SubTileId));
    }

    [Fact]
    public async Task ASubTileIsTheSame_WhateverOtherParentsAreRequestedWithIt()
    {
        // 前端分批載入：同一個小格在不同批次裡要長得一樣，不然批次邊界會有接縫
        var parents = LandPatch();
        var service = Create(parents).Service;
        var ids = parents.Select(p => p.TileId).ToList();

        var all = (await service.GetSubTilesAsync(ids, default)).ToDictionary(s => s.SubTileId);
        var reversed = await service.GetSubTilesAsync(ids.AsEnumerable().Reverse().ToList(), default);
        var half = await service.GetSubTilesAsync(ids.Take(ids.Count / 2).ToList(), default);

        Assert.Equal(all.Count, reversed.Count);
        foreach (var sub in reversed)
        {
            Assert.Equal(all[sub.SubTileId] with { NeighborIds = [] }, sub with { NeighborIds = [] });
            Assert.Equal(all[sub.SubTileId].NeighborIds, sub.NeighborIds);
        }

        // 只要一半的地塊：地表一樣；鄰居只連「這一批裡有的」，批次外的那幾格是 null（位置不變，前端靠位置認方向）
        var inHalf = half.Select(s => s.SubTileId).ToHashSet();
        Assert.True(half.Count < all.Count);
        var cutOff = 0;
        foreach (var sub in half)
        {
            var expected = all[sub.SubTileId];
            Assert.Equal(expected with { NeighborIds = [] }, sub with { NeighborIds = [] });
            Assert.Equal(expected.NeighborIds.Length, sub.NeighborIds.Length);
            for (var k = 0; k < expected.NeighborIds.Length; k++)
            {
                var stillThere = expected.NeighborIds[k] is { } id && inHalf.Contains(id);
                Assert.Equal(stillThere ? expected.NeighborIds[k] : null, sub.NeighborIds[k]);
                if (!stillThere && expected.NeighborIds[k] is not null) cutOff++;
            }
        }
        Assert.True(cutOff > 0, "兩批的交界上應該有鄰居落在批次外");
    }

    // ───────────────────────── 玩家修改 ─────────────────────────

    [Fact]
    public async Task StructureAppearsOnExactlyThatSubTile_AndDoesNotChangeTheTerrain()
    {
        var parents = LandPatch();
        var (service, constructions) = Create(parents);
        var ids = parents.Select(p => p.TileId).ToList();
        var before = await service.GetSubTilesAsync(ids, default);
        var target = before[before.Count / 2];
        constructions.Items.Add(new Construction { SubTileId = target.SubTileId, ParentTileId = target.ParentTileId, Structure = StructureKind.House });

        var after = await service.GetSubTilesAsync(ids, default);

        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
        {
            var expected = before[i].SubTileId == target.SubTileId ? before[i] with { Structure = "House" } : before[i];
            Assert.Equal(expected with { NeighborIds = [] }, after[i] with { NeighborIds = [] });
        }
    }

    [Fact]
    public async Task ConstructionForASubTileOutsideTheBatch_IsIgnored()
    {
        // 資料庫裡的修改是依「所屬地塊」查的；小格編號對不上這一批（例如網格改版後留下的舊資料）時不能讓整個請求失敗
        var parents = LandPatch();
        var (service, constructions) = Create(parents);
        constructions.Items.Add(new Construction { SubTileId = 123, ParentTileId = parents[0].TileId, Structure = StructureKind.Castle });

        var result = await service.GetSubTilesAsync(parents.Select(p => p.TileId).ToList(), default);

        Assert.All(result, s => Assert.Null(s.Structure));
    }

    [Fact]
    public async Task Channel_TurnsLandIntoWater_FourLevelsDeepWithOneLevelOfWater()
    {
        var parents = LandPatch();
        var (service, constructions) = Create(parents);
        var ids = parents.Select(p => p.TileId).ToList();
        var target = (await service.GetSubTilesAsync(ids, default))[5];
        constructions.Items.Add(new Construction { SubTileId = target.SubTileId, ParentTileId = target.ParentTileId, TerrainEdit = TerrainEditKind.Channel });

        var dug = (await service.GetSubTilesAsync(ids, default)).Single(s => s.SubTileId == target.SubTileId);

        Assert.Equal("Freshwater", dug.Terrain);
        Assert.Equal("Channel", dug.TerrainEdit);
        Assert.Equal(target.ElevationLevel - ConstructionRules.ChannelBedDepthLevels, dug.ElevationLevel);
        Assert.Equal(target.ElevationLevel - ConstructionRules.ChannelWaterDepthLevels, dug.SeaLevel);
        Assert.True(ConstructionRules.IsWater(SurfaceOf(dug)));
    }

    [Fact]
    public async Task Fill_RaisesWaterToItsLandNeighbours_OrToTheWaterSurfaceWhenThereAreNone()
    {
        // 一半是海（海床 -50、海面 0）、一半是陸地（30 級）
        var patch = RealTiles.Value.Where(t => t.Row is >= 40 and <= 47 && t.Col <= 7).ToList();
        var parents = patch
            .Select(t => t.Row <= 43 ? Copy(t, TerrainType.Seawater, 6, -50, 0) : Copy(t, TerrainType.Grassland, 0, 30, 0))
            .ToList();
        var (service, constructions) = Create(parents);
        var ids = parents.Select(p => p.TileId).ToList();
        var before = await service.GetSubTilesAsync(ids, default);
        var byId = before.ToDictionary(s => s.SubTileId);
        bool IsSea(SubTileDto s) => s.Terrain == "Seawater";
        IEnumerable<SubTileDto> NeighboursOf(SubTileDto s) => s.NeighborIds.Where(id => id is not null && byId.ContainsKey(id.Value)).Select(id => byId[id!.Value]);

        var coastal = before.First(s => IsSea(s) && NeighboursOf(s).Any(n => !IsSea(n)));
        var offshore = before.First(s => IsSea(s) && s.NeighborIds.All(id => id is not null && byId.ContainsKey(id.Value)) && NeighboursOf(s).All(IsSea));
        constructions.Items.Add(new Construction { SubTileId = coastal.SubTileId, ParentTileId = coastal.ParentTileId, TerrainEdit = TerrainEditKind.Fill });
        constructions.Items.Add(new Construction { SubTileId = offshore.SubTileId, ParentTileId = offshore.ParentTileId, TerrainEdit = TerrainEditKind.Fill });

        var after = (await service.GetSubTilesAsync(ids, default)).ToDictionary(s => s.SubTileId);

        // 靠岸的：跟相鄰的陸地一樣高
        Assert.Equal("Grassland", after[coastal.SubTileId].Terrain);
        Assert.Equal(30, after[coastal.SubTileId].ElevationLevel);
        Assert.False(ConstructionRules.IsWater(SurfaceOf(after[coastal.SubTileId])));
        // 四周都是海的：填到海面
        Assert.Equal(0, after[offshore.SubTileId].ElevationLevel);
        Assert.False(ConstructionRules.IsWater(SurfaceOf(after[offshore.SubTileId])));
        Assert.Equal("Fill", after[offshore.SubTileId].TerrainEdit);
    }

    // ───────────────────────── 模擬地形：取樣 + 挖河道 ─────────────────────────

    /// <summary>
    /// 一顆全是草地（100 公尺）的星球，其中一個粗格是 1,000 公尺的高地，一條 2 公里寬的河從高地流到鄰格。
    /// 回傳河段附近的一片地塊、取樣器、以及那兩個粗格的編號。
    /// </summary>
    private static (List<Tile> Parents, SimulatedTerrainSampler Sampler, int From, int To) RiverOffAPlateau()
    {
        var radius = new GoldbergPlanetGenerator().PlanetRadiusMeters;
        var grid = SimulationGrid.Build(radius, 24);
        var u = grid.UnitVector;
        static (double X, double Y, double Z) Unit(double lat, double lon)
        {
            double la = lat * Math.PI / 180, lo = lon * Math.PI / 180;
            return (Math.Cos(la) * Math.Cos(lo), Math.Cos(la) * Math.Sin(lo), Math.Sin(la));
        }

        // 高地選在這 20 萬個地塊的範圍中間，河段才落在有地塊的地方
        var middle = RealTiles.Value[60_000];
        var m = Unit(middle.Latitude, middle.Longitude);
        var from = Enumerable.Range(0, grid.CellCount).MaxBy(c => u[c * 3] * m.X + u[c * 3 + 1] * m.Y + u[c * 3 + 2] * m.Z);
        Span<int> neighbors = stackalloc int[SimulationGrid.MaxNeighborsPerCell];
        grid.GetNeighbors(from, neighbors);
        var to = neighbors[0];

        var cells = Enumerable.Range(0, grid.CellCount)
            .Select(c => new PlanetCell { CellId = c, Terrain = TerrainType.Grassland, SurfaceElevationMeters = 100f })
            .ToArray();
        cells[from].RiverDownstreamCellId = to;
        cells[from].RiverSize = 0.5f;
        cells[from].RiverWidthMeters = 2_000f;
        cells[from].SurfaceElevationMeters = 1_000f;

        // 河段上離高地中心 20% 的那一點附近的 400 個地塊（半徑約 18 公里）
        double px = u[from * 3] + 0.2 * (u[to * 3] - u[from * 3]);
        double py = u[from * 3 + 1] + 0.2 * (u[to * 3 + 1] - u[from * 3 + 1]);
        double pz = u[from * 3 + 2] + 0.2 * (u[to * 3 + 2] - u[from * 3 + 2]);
        var length = Math.Sqrt(px * px + py * py + pz * pz);
        (px, py, pz) = (px / length, py / length, pz / length);
        double AngleTo(Tile t)
        {
            var v = Unit(t.Latitude, t.Longitude);
            return Math.Acos(Math.Clamp(v.X * px + v.Y * py + v.Z * pz, -1, 1));
        }
        var parents = RealTiles.Value.OrderBy(AngleTo).Take(400).ToList();
        Assert.True(AngleTo(parents[^1]) * radius < 25_000, "選出來的地塊沒有圍著河段：測試的前提不成立");

        return (parents, new SimulatedTerrainSampler(grid, cells), from, to);
    }

    [Fact]
    public async Task SimulatedTerrain_RiverIsSampledAndNeverStandsAboveItsBanks()
    {
        var (parents, sampler, from, to) = RiverOffAPlateau();
        var service = Create(parents, sampler).Service;

        var result = await service.GetSubTilesAsync(parents.Select(p => p.TileId).ToList(), default);

        var byId = result.ToDictionary(s => s.SubTileId);
        var river = result.Where(s => RiverChannelShaping.IsRiverBand(SurfaceOf(s))).ToList();
        Assert.True(river.Count > 100, $"河只取樣到 {river.Count} 個小格");
        Assert.All(river, s => Assert.Contains(s.RiverCellId!.Value, new[] { from, to }));
        Assert.All(result, s => Assert.NotNull(s.SourceCellId));

        var banksChecked = 0;
        foreach (var sub in river)
        {
            foreach (var id in sub.NeighborIds)
            {
                if (id is null || !byId.TryGetValue(id.Value, out var neighbour)) continue;
                var bank = SurfaceOf(neighbour);
                if (RiverChannelShaping.IsRiverBand(bank) || ConstructionRules.IsWater(bank)) continue;
                // 河面比岸低一級（不低於海面 0 級）
                var limit = Math.Max(0, bank.ElevationLevel - ConstructionRules.ChannelWaterDepthLevels);
                Assert.True(sub.ElevationLevel <= limit,
                    $"河格 {sub.SubTileId}（{sub.ElevationLevel} 級）高過岸 {neighbour.SubTileId}（{bank.ElevationLevel} 級）");
                banksChecked++;
            }
        }
        Assert.True(banksChecked > 50, $"只檢查到 {banksChecked} 組河與岸");
    }

    [Fact]
    public async Task SimulatedTerrain_RawSurfaceIsTheSampleBeforeDigging_AndDiggingActuallyChangedSomething()
    {
        // GetRawSurfaceAsync 是編輯規則用的「原本的地表」：不能含挖河道，也不能含玩家修改
        var (parents, sampler, _, _) = RiverOffAPlateau();
        var (service, constructions) = Create(parents, sampler);
        var result = await service.GetSubTilesAsync(parents.Select(p => p.TileId).ToList(), default);
        var river = result.Where(s => RiverChannelShaping.IsRiverBand(SurfaceOf(s))).ToList();

        var lowered = 0;
        foreach (var sub in river.Where((_, i) => i % 7 == 0))
        {
            var raw = await service.GetRawSurfaceAsync(sub.ParentTileId, sub.SubTileId, default);
            Assert.NotNull(raw);
            Assert.True(raw.Value.ElevationLevel >= sub.ElevationLevel, "挖河道只會往下挖");
            if (raw.Value.ElevationLevel > sub.ElevationLevel) lowered++;
        }
        // 這個場景（河從高地邊緣流下來）就是會出現「河高過兩岸」的地方：如果一格都沒被挖，代表整形沒有接上
        Assert.True(lowered > 0, "沒有任何河格被往下挖：挖河道沒有套用，或這個場景已經測不到它");

        var land = result.First(s => !ConstructionRules.IsWater(SurfaceOf(s)));
        constructions.Items.Add(new Construction { SubTileId = land.SubTileId, ParentTileId = land.ParentTileId, TerrainEdit = TerrainEditKind.Channel });
        var rawLand = await service.GetRawSurfaceAsync(land.ParentTileId, land.SubTileId, default);
        Assert.Equal(land.ElevationLevel, rawLand!.Value.ElevationLevel);
        Assert.Equal(land.Terrain, rawLand.Value.Terrain.ToString());

        Assert.Null(await service.GetRawSurfaceAsync(999_999_999, land.SubTileId, default));
        Assert.Null(await service.GetRawSurfaceAsync(land.ParentTileId, 123, default));
    }
}
