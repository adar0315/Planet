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

    private sealed record World(
        PlanetRegenerationService Service, SeedingStatus Status, GatedTileRepository Tiles,
        FakeMetadataRepository Metadata, PlanetMaterializationService Materialization);

    private static World Create(GatedTileRepository? tiles = null)
    {
        tiles ??= new GatedTileRepository();
        var metadataRepository = new FakeMetadataRepository();
        var scopes = FakeScopes.Build(services => services
            .AddScoped<ITileRepository>(_ => tiles)
            .AddScoped<IPlanetMetadataRepository>(_ => metadataRepository));
        var status = new SeedingStatus { IsReady = true };
        var metadata = new PlanetMetadataService(scopes, new SimulationGridCache(), NullLogger<PlanetMetadataService>.Instance);
        var overview = new OverviewTextureGenerator(metadata, scopes);
        var service = new PlanetRegenerationService(scopes, status, overview, metadata, NullLogger<PlanetRegenerationService>.Instance);
        // 放大服務只用到它的狀態（重新生成前要確認它沒在跑）；模擬服務在這些路徑上不會被用到
        var materialization = new PlanetMaterializationService(
            scopes, null!, metadata, overview, status, NullLogger<PlanetMaterializationService>.Instance);
        return new World(service, status, tiles, metadataRepository, materialization);
    }

    /// <summary>等背景作業結束（成功或失敗都會把 IsSeeding 設回 false）。</summary>
    private static async Task WaitUntilFinished(SeedingStatus status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (status.IsSeeding)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("重新生成在 30 秒內沒有結束");
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
        Assert.IsType<ConflictObjectResult>(Controller(world).Materialize("anything"));

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
