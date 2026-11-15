using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Planet.Data;
using Planet.Domain;
using Planet.Generation;
using Planet.Service;
using Planet.Service.Controllers;

namespace Planet.Service.Tests;

/// <summary>
/// 重新生成星球（<see cref="PlanetRegenerationService"/> 與 <see cref="RegenerationController"/>）。
///
/// 真的重新生成會清空 2,000 萬列，正式站上不能拿來試，所以這裡是它唯一被執行的地方。
/// 倉儲是假的，而且可以在任何一步停住（<see cref="GatedTileRepository"/>）：
/// 要驗的是「檢查有沒有在跑」與「標成正在跑」是同一個原子動作（2026-11-14 修的 bug：旗標原本在 TRUNCATE 之後才設，
/// 中間那段時間的第二個請求照樣通過檢查），以及成功、失敗之後狀態有沒有收乾淨。
///
/// 也驗「重新生成」與「放大存檔」不會同時跑（2026-11-15 修的 bug：兩邊各用自己的鎖、各看對方的旗標）。
/// 放大會先查星球中繼資料，所以讓中繼資料倉儲卡在閘門上（<see cref="GatedMetadataRepository"/>），
/// 放大就會真的停在「進行中」；放行後它因為找不到中繼資料而失敗，順便驗「放大失敗也會放開位置」。
/// </summary>
public class PlanetRegenerationServiceTests
{
    /// <summary>每一步都記下來，而且可以在清空或寫入時停住，直到測試放行。</summary>
    private sealed class GatedTileRepository() : FakeTileRepository([])
    {
        public List<string> Steps { get; } = [];
        public TaskCompletionSource ClearGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource InsertGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ClearReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource InsertReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? FailOnClear { get; set; }
        public Exception? FailOnInsert { get; set; }
        public List<Tile> FirstTiles { get; } = [];

        public override async Task ClearAllTilesAsync(CancellationToken ct = default)
        {
            lock (Steps) Steps.Add("清空地塊");
            ClearReached.TrySetResult();
            await ClearGate.Task;
            if (FailOnClear is not null) throw FailOnClear;
        }

        public override Task ClearPlanetCellsAsync(CancellationToken ct = default)
        {
            lock (Steps) Steps.Add("清空粗格");
            return Task.CompletedTask;
        }

        public override async Task BulkInsertAsync(IEnumerable<Tile> tiles, Action<long>? onProgress = null, CancellationToken ct = default)
        {
            lock (Steps) Steps.Add("寫入地塊");
            // 只看前幾個（生成是延遲的，整顆星球 2,000 萬格不會真的產生）
            FirstTiles.AddRange(tiles.Take(3));
            onProgress?.Invoke(3);
            InsertReached.TrySetResult();
            await InsertGate.Task;
            if (FailOnInsert is not null) throw FailOnInsert;
        }
    }

    /// <summary>中繼資料倉儲：讀取時卡在閘門上（放大存檔的第一步會讀它），放行後回傳「沒有中繼資料」；寫入照常記錄。</summary>
    private sealed class GatedMetadataRepository : IPlanetMetadataRepository
    {
        public FakeMetadataRepository Inner { get; } = new();
        public TaskCompletionSource ReadGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PlanetMetadata?> GetAsync(CancellationToken ct = default)
        {
            ReadReached.TrySetResult();
            await ReadGate.Task;
            return null;
        }

        public Task UpsertAsync(PlanetMetadata metadata, CancellationToken ct = default) => Inner.UpsertAsync(metadata, ct);
    }

    private sealed record World(
        PlanetRegenerationService Service, SeedingStatus Status, GatedTileRepository Tiles,
        GatedMetadataRepository MetadataGate, PlanetMaterializationService Materialization)
    {
        public FakeMetadataRepository Metadata => MetadataGate.Inner;
    }

    /// <summary>放大要讀的存檔：內容不重要（還沒讀到內容就會因為沒有中繼資料而失敗），檔案要存在。</summary>
    private const string SnapshotName = "held";

    private static World Create(GatedTileRepository? tiles = null)
    {
        tiles ??= new GatedTileRepository();
        var metadataRepository = new GatedMetadataRepository();
        var scopes = FakeScopes.Build(services => services
            .AddScoped<ITileRepository>(_ => tiles)
            .AddScoped<IPlanetMetadataRepository>(_ => metadataRepository));
        var status = new SeedingStatus { IsReady = true };
        var metadata = new PlanetMetadataService(scopes, new SimulationGridCache(), NullLogger<PlanetMetadataService>.Instance);
        var overview = new OverviewTextureGenerator(metadata, scopes);
        var service = new PlanetRegenerationService(scopes, status, overview, metadata, NullLogger<PlanetRegenerationService>.Instance);

        var snapshotDirectory = Path.Combine(Path.GetTempPath(), "planet-regeneration-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(snapshotDirectory);
        var snapshots = new SnapshotStore(snapshotDirectory);
        File.WriteAllBytes(snapshots.PathOf(SnapshotName), [0]);
        var simulation = new SimulationService(metadata, snapshots, new SimulationGridCache());
        var materialization = new PlanetMaterializationService(
            scopes, simulation, metadata, overview, status, NullLogger<PlanetMaterializationService>.Instance);
        return new World(service, status, tiles, metadataRepository, materialization);
    }

    /// <summary>等背景作業結束、位置也放開了（成功或失敗都會把 IsSeeding 設回 false，最後才放開位置）。</summary>
    private static async Task WaitUntilFinished(SeedingStatus status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (status.IsSeeding || status.ExclusiveOwner is not null)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"背景作業在 30 秒內沒有結束（位置：{status.ExclusiveOwner}）");
            await Task.Delay(10);
        }
    }

    private static async Task WithTimeout(Task task) =>
        await task.WaitAsync(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task WhileClearing_TheSecondRequestIsRejected()
    {
        // 正是 2026-11-14 修的那段時間：已受理、還在清空、還沒開始寫入
        var world = Create();
        world.Service.StartInBackground(TerrainGenerationOptions.Default, clearExisting: true);
        await WithTimeout(world.Tiles.ClearReached.Task);

        Assert.True(world.Service.IsRunning);
        Assert.False(world.Status.IsReady); // 清空期間地圖不能用
        Assert.Throws<InvalidOperationException>(() =>
            world.Service.StartInBackground(TerrainGenerationOptions.Default, clearExisting: true));

        world.Tiles.ClearGate.SetResult();
        world.Tiles.InsertGate.SetResult();
        await WaitUntilFinished(world.Status);
        Assert.Equal(1, world.Tiles.Steps.Count(s => s == "清空地塊"));
    }

    [Fact]
    public async Task ManySimultaneousRequests_ExactlyOneIsAccepted()
    {
        var world = Create();
        const int requests = 32;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var pending = Enumerable.Range(0, requests).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            try
            {
                world.Service.StartInBackground(TerrainGenerationOptions.Default, clearExisting: true);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        })).ToList();
        start.SetResult();
        var outcomes = await Task.WhenAll(pending);

        Assert.Equal(1, outcomes.Count(accepted => accepted));
        world.Tiles.ClearGate.SetResult();
        world.Tiles.InsertGate.SetResult();
        await WaitUntilFinished(world.Status);
        Assert.Equal(["清空地塊", "清空粗格", "寫入地塊"], world.Tiles.Steps);
    }

    [Fact]
    public async Task Success_ClearsThenWritesThenRecords_AndLeavesTheMapReady()
    {
        var world = Create();
        world.Service.StartInBackground(TerrainGenerationOptions.Default, clearExisting: true);
        world.Tiles.ClearGate.SetResult();
        await WithTimeout(world.Tiles.InsertReached.Task);

        // 寫入中：總數已知、地圖還不能用、中繼資料還沒寫（寫了就會有一段時間宣稱一顆還沒寫完的星球）
        var generator = new GoldbergPlanetGenerator();
        Assert.Equal(generator.TotalTileCount, world.Status.TotalTiles);
        Assert.Equal(3, world.Status.TilesWritten);
        Assert.False(world.Status.IsReady);
        Assert.Empty(world.Metadata.Upserts);

        world.Tiles.InsertGate.SetResult();
        await WaitUntilFinished(world.Status);

        Assert.Equal(["清空地塊", "清空粗格", "寫入地塊"], world.Tiles.Steps);
        Assert.True(world.Status.IsReady);
        Assert.Null(world.Status.ErrorMessage);
        Assert.Equal(world.Status.TotalTiles, world.Status.TilesWritten);
        var recorded = Assert.Single(world.Metadata.Upserts);
        Assert.Equal(GoldbergPlanetGenerator.SubdivisionFrequency, recorded.SubdivisionFrequency);
        Assert.Equal(generator.TotalTileCount, recorded.TotalTileCount);
        Assert.Null(recorded.SimulationFrequency); // 程序化地形，不是模擬放大的
        Assert.Equal(3, world.Tiles.FirstTiles.Count); // 寫進去的是真的地塊
        Assert.Equal(0, world.Tiles.FirstTiles[0].TileId);

        // 完成後可以再來一次
        world.Service.StartInBackground(TerrainGenerationOptions.Default, clearExisting: false);
        await WaitUntilFinished(world.Status);
    }

    [Fact]
    public async Task WithoutClearing_NothingIsDeleted()
    {
        var tiles = new GatedTileRepository();
        tiles.ClearGate.SetResult();
        tiles.InsertGate.SetResult();
        var world = Create(tiles);

        world.Service.StartInBackground(TerrainGenerationOptions.Default, clearExisting: false);
        await WaitUntilFinished(world.Status);

        Assert.Equal(["寫入地塊"], world.Tiles.Steps);
        Assert.True(world.Status.IsReady);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failure_ReportsTheError_ReleasesTheFlag_AndAllowsARetry(bool failWhileClearing)
    {
        var tiles = new GatedTileRepository
        {
            FailOnClear = failWhileClearing ? new InvalidOperationException("資料庫連線中斷") : null,
            FailOnInsert = failWhileClearing ? null : new InvalidOperationException("資料庫連線中斷"),
        };
        tiles.ClearGate.SetResult();
        tiles.InsertGate.SetResult();
        var world = Create(tiles);

        world.Service.StartInBackground(TerrainGenerationOptions.Default, clearExisting: true);
        await WaitUntilFinished(world.Status);

        Assert.False(world.Service.IsRunning);
        Assert.Equal("資料庫連線中斷", world.Status.ErrorMessage);
        Assert.False(world.Status.IsReady); // 已經清掉一部分了，不能宣稱地圖可用
        Assert.Empty(world.Metadata.Upserts); // 失敗的星球不能被記成「目前這一顆」
        if (failWhileClearing) Assert.Equal(["清空地塊"], world.Tiles.Steps);

        // 旗標放掉了：可以重試，重試成功後錯誤訊息清掉、地圖可用
        tiles.FailOnClear = null;
        tiles.FailOnInsert = null;
        world.Service.StartInBackground(TerrainGenerationOptions.Default, clearExisting: true);
        await WaitUntilFinished(world.Status);
        Assert.Null(world.Status.ErrorMessage);
        Assert.True(world.Status.IsReady);
        Assert.Single(world.Metadata.Upserts);
    }

    // ───────────────────────── 與放大存檔互斥 ─────────────────────────

    [Fact]
    public async Task WhileMaterializing_TheRegenerationServiceItselfRefuses_AndLeavesTheMapAlone()
    {
        // 舊的寫法只有控制器會先看放大有沒有在跑，服務本身不看：繞過控制器（或兩個請求剛好交錯）就會兩件一起跑
        var world = Create();
        Assert.True(world.Materialization.TryStart(SnapshotName));
        await WithTimeout(world.MetadataGate.ReadReached.Task);
        Assert.Equal(PlanetMaterializationService.ExclusiveOwnerName, world.Status.ExclusiveOwner);

        Assert.Throws<InvalidOperationException>(() =>
            world.Service.StartInBackground(TerrainGenerationOptions.Default, clearExisting: true));
        Assert.IsType<ConflictObjectResult>(Controller(world).Regenerate());

        // 被拒絕的重新生成不能動到任何狀態：地圖仍然可用、沒有清空任何東西
        Assert.False(world.Status.IsSeeding);
        Assert.True(world.Status.IsReady);
        Assert.Empty(world.Tiles.Steps);

        // 放大放行之後失敗（測試裡沒有中繼資料）：位置要放開，之後重新生成就可以開始
        world.MetadataGate.ReadGate.SetResult();
        await WaitUntilFinished(world.Status);
        Assert.False(world.Materialization.Status.IsRunning);
        Assert.NotNull(world.Materialization.Status.Error);

        world.Tiles.ClearGate.SetResult();
        world.Tiles.InsertGate.SetResult();
        Assert.IsType<AcceptedResult>(Controller(world).Regenerate());
        await WaitUntilFinished(world.Status);
        Assert.True(world.Status.IsReady);
    }

    [Fact]
    public async Task WhileRegenerating_MaterializationRefuses_AndKeepsItsOwnStatus()
    {
        var world = Create();
        world.Service.StartInBackground(TerrainGenerationOptions.Default, clearExisting: true);
        await WithTimeout(world.Tiles.ClearReached.Task);

        Assert.False(world.Materialization.TryStart(SnapshotName));
        Assert.IsType<ConflictObjectResult>(Controller(world).Materialize(SnapshotName));
        // 被拒絕的放大不能把自己的狀態改成「進行中」
        Assert.False(world.Materialization.Status.IsRunning);
        Assert.Null(world.Materialization.Status.SnapshotName);
        Assert.False(world.MetadataGate.ReadReached.Task.IsCompleted);

        world.Tiles.ClearGate.SetResult();
        world.Tiles.InsertGate.SetResult();
        await WaitUntilFinished(world.Status);
    }

    [Fact]
    public async Task RegenerateAndMaterialize_SimultaneousRequests_ExactlyOneOfAllIsAccepted()
    {
        var world = Create();
        const int eachKind = 16;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var pending = Enumerable.Range(0, eachKind * 2).Select(i => Task.Run(async () =>
        {
            await start.Task;
            return i % 2 == 0 ? Controller(world).Regenerate() : Controller(world).Materialize(SnapshotName);
        })).ToList();
        start.SetResult();
        var results = await Task.WhenAll(pending);

        Assert.Equal(1, results.Count(r => r is AcceptedResult));
        Assert.Equal(eachKind * 2 - 1, results.Count(r => r is ConflictObjectResult));
        // 受理的是哪一件，位置就是誰的，而且另一件完全沒有開始
        if (world.Status.ExclusiveOwner == PlanetRegenerationService.ExclusiveOwnerName)
        {
            Assert.False(world.Materialization.Status.IsRunning);
        }
        else
        {
            Assert.Equal(PlanetMaterializationService.ExclusiveOwnerName, world.Status.ExclusiveOwner);
            Assert.False(world.Status.IsSeeding);
            Assert.Empty(world.Tiles.Steps);
        }

        world.MetadataGate.ReadGate.SetResult();
        world.Tiles.ClearGate.SetResult();
        world.Tiles.InsertGate.SetResult();
        await WaitUntilFinished(world.Status);
    }

    [Fact]
    public void ExclusiveSlot_OnlyTheOwnerCanRelease_AndItCanBeTakenAgainAfterwards()
    {
        var status = new SeedingStatus();
        var callbacks = 0;

        Assert.True(status.TryBeginExclusive("甲", () => callbacks++));
        Assert.False(status.TryBeginExclusive("乙", () => callbacks++)); // 沒佔到就不執行
        Assert.Equal(1, callbacks);
        Assert.Throws<InvalidOperationException>(() => status.EndExclusive("乙"));
        Assert.Equal("甲", status.ExclusiveOwner);

        status.EndExclusive("甲");
        Assert.Null(status.ExclusiveOwner);
        Assert.Throws<InvalidOperationException>(() => status.EndExclusive("甲")); // 放兩次也是程式寫錯
        Assert.True(status.TryBeginExclusive("乙"));
    }

    // ───────────────────────── 控制器 ─────────────────────────

    private static RegenerationController Controller(World world) =>
        new(new TerrainOptionsStore(), world.Service, world.Materialization);

    [Fact]
    public async Task Regenerate_WhenIdle_IsAccepted_AndWhileRunning_IsConflict()
    {
        var world = Create();

        Assert.IsType<AcceptedResult>(Controller(world).Regenerate());
        await WithTimeout(world.Tiles.ClearReached.Task);
        Assert.IsType<ConflictObjectResult>(Controller(world).Regenerate());
        // 重新生成中也不能開始放大（兩者都會整個換掉地塊表）
        Assert.IsType<ConflictObjectResult>(Controller(world).Materialize(SnapshotName));

        world.Tiles.ClearGate.SetResult();
        world.Tiles.InsertGate.SetResult();
        await WaitUntilFinished(world.Status);
    }

    [Fact]
    public async Task Regenerate_SimultaneousRequests_OneAcceptedTheRestConflict()
    {
        // 控制器先檢查 IsRunning、再呼叫 StartInBackground：兩個請求可能同時通過檢查，
        // 後到的那個要靠 StartInBackground 的例外變成 409，而不是 500
        var world = Create();
        const int requests = 32;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var pending = Enumerable.Range(0, requests).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            return Controller(world).Regenerate();
        })).ToList();
        start.SetResult();
        var results = await Task.WhenAll(pending);

        Assert.Equal(1, results.Count(r => r is AcceptedResult));
        Assert.Equal(requests - 1, results.Count(r => r is ConflictObjectResult));
        world.Tiles.ClearGate.SetResult();
        world.Tiles.InsertGate.SetResult();
        await WaitUntilFinished(world.Status);
    }
}
