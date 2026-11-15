using Planet.Service;

namespace Planet.Service.Tests;

/// <summary>
/// <see cref="SnapshotStore"/>（存檔的檔案操作）的測試。每個測試用自己的暫存目錄，不碰部署目錄的 snapshots。
/// 這些存檔一個約 32 MB、是「不要因為服務重啟就丟掉幾千步的星球」用的，所以名稱檢查、寫到一半失敗、
/// 清理舊檢查點這幾條路徑出錯的代價都是丟存檔。
/// </summary>
public sealed class SnapshotStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "planet-snapshot-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SnapshotStore _store;

    public SnapshotStoreTests()
    {
        _store = new SnapshotStore(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static Action<Stream> Bytes(params byte[] content) => stream => stream.Write(content);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("a b")]
    [InlineData("存檔")]
    [InlineData("a.planet")]
    public void PathOf_RejectsNamesThatCouldEscapeTheDirectory(string name)
    {
        Assert.Throws<ArgumentException>(() => _store.PathOf(name));
    }

    [Fact]
    public void PathOf_LengthLimitIs64()
    {
        Assert.EndsWith(".planet", _store.PathOf(new string('a', 64)));
        Assert.Throws<ArgumentException>(() => _store.PathOf(new string('a', 65)));
    }

    [Fact]
    public void PathOf_StaysInsideTheDirectory()
    {
        var path = _store.PathOf("Run_1-final");
        Assert.Equal(_directory, Path.GetDirectoryName(path));
        Assert.Equal("Run_1-final.planet", Path.GetFileName(path));
    }

    [Fact]
    public void Write_ThenList_ReportsSizeAndSidecarDetails()
    {
        var size = _store.Write("first", SnapshotKind.Manual, stepCount: 12, elapsedYears: 600_000, Bytes(1, 2, 3, 4, 5));

        Assert.Equal(5, size);
        var info = Assert.Single(_store.List());
        Assert.Equal("first", info.Name);
        Assert.Equal(5, info.SizeBytes);
        Assert.Equal(SnapshotKind.Manual, info.Kind);
        Assert.Equal(12, info.StepCount);
        Assert.Equal(600_000, info.ElapsedYears);
        // 內容不是真的存檔格式：要標成不相容，而不是丟例外讓整個列表失敗
        Assert.False(info.Compatible);
    }

    [Fact]
    public void Write_Overwrites_AndLeavesNoTemporaryFile()
    {
        _store.Write("slot", SnapshotKind.Manual, 1, 1, Bytes(1, 1, 1));
        _store.Write("slot", SnapshotKind.Checkpoint, 2, 2, Bytes(9, 9));

        using var stream = _store.OpenRead("slot");
        Assert.Equal(2, stream.Length);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        Assert.Equal(SnapshotKind.Checkpoint, Assert.Single(_store.List()).Kind);
    }

    [Fact]
    public void Write_FailingHalfway_KeepsThePreviousSaveAndCleansUpTheTemporaryFile()
    {
        _store.Write("slot", SnapshotKind.Manual, 1, 1, Bytes(1, 2, 3));

        Assert.Throws<InvalidOperationException>(() => _store.Write("slot", SnapshotKind.Manual, 2, 2, stream =>
        {
            stream.Write([7, 7, 7, 7, 7, 7]);
            throw new InvalidOperationException("寫到一半失敗");
        }));

        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        using var stream = _store.OpenRead("slot");
        var content = new byte[stream.Length];
        stream.ReadExactly(content);
        Assert.Equal<byte>([1, 2, 3], content);
        // 旁檔也不能被新的步數蓋掉：它跟檔案內容是一組的
        Assert.Equal(1, Assert.Single(_store.List()).StepCount);
    }

    [Fact]
    public void OpenRead_MissingSave_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() => _store.OpenRead("nothing"));
    }

    [Fact]
    public void List_WhenDirectoryDoesNotExist_IsEmpty()
    {
        Assert.Empty(_store.List());
    }

    [Fact]
    public void List_NewestFirst_IgnoresTemporaryAndSidecarFiles()
    {
        _store.Write("old", SnapshotKind.Manual, 1, 1, Bytes(1));
        _store.Write("new", SnapshotKind.Manual, 2, 2, Bytes(1));
        File.SetLastWriteTimeUtc(_store.PathOf("old"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(_store.PathOf("new"), new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        File.WriteAllBytes(Path.Combine(_directory, "half.planet.tmp"), [1]);

        Assert.Equal(["new", "old"], _store.List().Select(s => s.Name).ToArray());
    }

    [Fact]
    public void Delete_RemovesTheSaveAndItsSidecar()
    {
        _store.Write("gone", SnapshotKind.Manual, 1, 1, Bytes(1));

        _store.Delete("gone");

        Assert.Empty(Directory.GetFiles(_directory));
        Assert.Throws<FileNotFoundException>(() => _store.Delete("gone"));
    }

    [Fact]
    public void PruneCheckpoints_KeepsTheNewestAndNeverTouchesOtherKinds()
    {
        string[] names = ["c1", "c2", "c3", "c4"];
        for (var i = 0; i < names.Length; i++)
        {
            _store.Write(names[i], SnapshotKind.Checkpoint, i, i, Bytes(1));
            File.SetLastWriteTimeUtc(_store.PathOf(names[i]), new DateTime(2026, 1, 1 + i, 0, 0, 0, DateTimeKind.Utc));
        }
        _store.Write("mine", SnapshotKind.Manual, 0, 0, Bytes(1));
        _store.Write("stop", SnapshotKind.StopCondition, 0, 0, Bytes(1));
        // 手動與停止條件的存檔比所有檢查點都舊：就算排在最後也不能被清掉
        File.SetLastWriteTimeUtc(_store.PathOf("mine"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(_store.PathOf("stop"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        _store.PruneCheckpoints(keep: 2);

        var remaining = _store.List().Select(s => s.Name).OrderBy(n => n).ToArray();
        Assert.Equal(["c3", "c4", "mine", "stop"], remaining);
    }

    [Fact]
    public void PruneCheckpoints_NegativeKeep_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _store.PruneCheckpoints(-1));
    }
}
