using System.Text.Json;
using System.Text.Json.Serialization;
using Planet.Simulation;

namespace Planet.Service;

/// <summary>存檔是怎麼來的。以字串序列化（前端與說明檔都看名稱，不看數字）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SnapshotKind>))]
public enum SnapshotKind
{
    /// <summary>使用者手動存的（含分岔時另存的新名字）。</summary>
    Manual,

    /// <summary>跑的時候每 <see cref="SimulationService.CheckpointIntervalYears"/> 自動存的檢查點，只留最近幾個。</summary>
    Checkpoint,

    /// <summary>停止條件成立時自動存的。</summary>
    StopCondition,
}

/// <summary>一個存檔的概況。</summary>
/// <param name="Name">存檔名稱（不含副檔名）。</param>
/// <param name="SizeBytes">檔案大小（位元組）。</param>
/// <param name="SavedAtUtc">存檔時間（UTC）。</param>
/// <param name="Kind">存檔怎麼來的（舊版沒有說明檔的存檔是 null）。</param>
/// <param name="StepCount">存檔時的步數（舊版存檔是 null）。</param>
/// <param name="ElapsedYears">存檔時的年代（年，舊版存檔是 null）。</param>
/// <param name="Compatible">這個版本讀不讀得回來（舊格式、v1 的存檔是 false，列出來但不能載入或放大）。</param>
public record SnapshotInfo(
    string Name,
    long SizeBytes,
    DateTime SavedAtUtc,
    SnapshotKind? Kind = null,
    long? StepCount = null,
    double? ElapsedYears = null,
    bool Compatible = true);

/// <summary>存檔旁邊的說明檔（<c>名稱.json</c>）：列表不必把幾十 MB 的存檔讀進來就知道步數與年代。</summary>
/// <param name="Kind">存檔怎麼來的。</param>
/// <param name="StepCount">步數。</param>
/// <param name="ElapsedYears">年代（年）。</param>
internal record SnapshotSidecar(SnapshotKind Kind, long StepCount, double ElapsedYears);

/// <summary>
/// 模擬存檔的檔案存取：名稱檢查、寫入、讀取、列表、刪除、清理檢查點。
///
/// 從 <see cref="SimulationService"/> 拆出來（2026-11-14）：那邊只管引擎與它的鎖，這裡只管檔案。
/// 這裡不認識引擎——寫入時由呼叫端給一個「把內容寫進串流」的委派（呼叫端自己負責在引擎的鎖裡寫）。
///
/// 檔案操作用自己的一把鎖互斥：刪除、清理檢查點不需要、也不應該去搶引擎的鎖
/// （引擎推進一段可能好幾秒，列表與刪除不必等它）。
/// </summary>
public sealed class SnapshotStore
{
    /// <summary>存檔的副檔名。</summary>
    private const string SnapshotExtension = ".planet";

    /// <summary>存檔名稱的長度上限。</summary>
    private const int MaxNameLength = 64;

    /// <summary>檔案操作的互斥鎖（寫入換檔、刪除、清理不能交錯）。</summary>
    private readonly Lock _fileLock = new();

    /// <summary>
    /// 存檔放在服務執行檔旁邊的 <c>snapshots</c> 目錄。
    ///
    /// 不做成設定值：這是「不要因為服務重啟就丟掉幾千步的星球」用的，
    /// 放在部署目錄旁邊最不容易在重新 publish 時被清掉（publish 不會刪除不認識的子目錄）。
    /// </summary>
    public string DirectoryPath { get; } = Path.Combine(AppContext.BaseDirectory, "snapshots");

    /// <summary>
    /// 檢查存檔名稱並換成完整路徑。**只允許英數、底線與連字號**。
    ///
    /// 這是路徑穿越的防線：名稱會直接接到目錄後面，
    /// <c>..\..\appsettings.Production.json</c> 這種名稱可以寫到部署目錄外面去。
    /// 用白名單而不是黑名單——黑名單漏一個就破功。
    /// </summary>
    /// <param name="name">存檔名稱。</param>
    /// <returns>完整路徑。</returns>
    /// <exception cref="ArgumentException">名稱不合規。</exception>
    public string PathOf(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength
            || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-'))
        {
            throw new ArgumentException(
                $"存檔名稱只能用英數、底線與連字號，長度1~{MaxNameLength}。", nameof(name));
        }

        return Path.Combine(DirectoryPath, name + SnapshotExtension);
    }

    /// <summary>
    /// 寫一個存檔（旁邊寫一份說明檔，見 <see cref="SnapshotSidecar"/>）。
    /// 先寫到暫存檔再換過去：中途失敗的話原本的存檔還在，不會變成「有一個讀不回來的半截檔案」。
    /// </summary>
    /// <param name="name">存檔名稱（英數、底線、連字號）。</param>
    /// <param name="kind">存檔怎麼來的。</param>
    /// <param name="stepCount">存檔時的步數（寫進說明檔）。</param>
    /// <param name="elapsedYears">存檔時的年代（寫進說明檔）。</param>
    /// <param name="writeContent">把存檔內容寫進串流。</param>
    /// <returns>存檔的位元組數。</returns>
    public long Write(string name, SnapshotKind kind, long stepCount, double elapsedYears, Action<Stream> writeContent)
    {
        ArgumentNullException.ThrowIfNull(writeContent);
        var path = PathOf(name);
        var temporary = path + ".tmp";
        lock (_fileLock)
        {
            Directory.CreateDirectory(DirectoryPath);
            try
            {
                using (var stream = File.Create(temporary))
                {
                    writeContent(stream);
                }

                File.Move(temporary, path, overwrite: true);
            }
            catch
            {
                // 寫到一半失敗：暫存檔不留下來佔空間（一個約 32 MB）
                TryDelete(temporary);
                throw;
            }

            File.WriteAllText(SidecarPath(path), JsonSerializer.Serialize(new SnapshotSidecar(kind, stepCount, elapsedYears)));
            return new FileInfo(path).Length;
        }
    }

    /// <summary>開一個存檔來讀。</summary>
    /// <param name="name">存檔名稱。</param>
    /// <exception cref="FileNotFoundException">找不到這個存檔。</exception>
    public FileStream OpenRead(string name)
    {
        var path = PathOf(name);
        if (!File.Exists(path)) throw new FileNotFoundException($"找不到存檔「{name}」。", path);
        return File.OpenRead(path);
    }

    /// <summary>列出所有存檔，新的在前。</summary>
    /// <returns>每個存檔的名稱、大小與存檔時間。</returns>
    public IReadOnlyList<SnapshotInfo> List()
    {
        if (!Directory.Exists(DirectoryPath)) return [];

        return Directory.EnumerateFiles(DirectoryPath, "*" + SnapshotExtension)
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(Describe)
            .ToList();
    }

    /// <summary>刪除一個存檔（連同說明檔）。</summary>
    /// <param name="name">存檔名稱。</param>
    /// <exception cref="FileNotFoundException">找不到這個存檔。</exception>
    public void Delete(string name)
    {
        var path = PathOf(name);
        lock (_fileLock)
        {
            if (!File.Exists(path)) throw new FileNotFoundException($"找不到存檔「{name}」。", path);
            File.Delete(path);
            File.Delete(SidecarPath(path));
        }
    }

    /// <summary>檢查點超過 <paramref name="keep"/> 個就刪最舊的（只看說明檔標成檢查點的，手動存檔不動）。</summary>
    /// <param name="keep">最多留幾個。</param>
    public void PruneCheckpoints(int keep)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keep);
        var stale = List().Where(s => s.Kind == SnapshotKind.Checkpoint).Skip(keep).ToList(); // List 已經是新的在前
        foreach (var snapshot in stale)
        {
            try
            {
                Delete(snapshot.Name);
            }
            catch (FileNotFoundException)
            {
                // 列出來之後被別的請求刪掉了：本來就是要它消失
            }
        }
    }

    /// <summary>讀出一個存檔的摘要：格式是否相容（正在寫入時當成不相容）、說明檔記錄的步數與年代。</summary>
    /// <param name="file">存檔檔案。</param>
    /// <returns>存檔摘要。</returns>
    private static SnapshotInfo Describe(FileInfo file)
    {
        bool compatible;
        try
        {
            using var stream = file.OpenRead();
            compatible = SimulationSnapshot.IsCurrentFormat(stream);
        }
        catch (IOException)
        {
            compatible = false; // 正在寫入（另一個請求在存檔），或剛被刪掉
        }

        var sidecar = ReadSidecar(SidecarPath(file.FullName));
        return new SnapshotInfo(
            Path.GetFileNameWithoutExtension(file.Name), file.Length, file.LastWriteTimeUtc,
            sidecar?.Kind, sidecar?.StepCount, sidecar?.ElapsedYears, compatible);
    }

    /// <summary>讀說明檔；沒有、讀不到或壞了都當成沒有（存檔本身還是列得出來）。</summary>
    /// <param name="sidecarPath">說明檔路徑。</param>
    private static SnapshotSidecar? ReadSidecar(string sidecarPath)
    {
        try
        {
            return File.Exists(sidecarPath)
                ? JsonSerializer.Deserialize<SnapshotSidecar>(File.ReadAllText(sidecarPath))
                : null;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>存檔的說明檔路徑（同名、副檔名 .json）。</summary>
    /// <param name="snapshotPath">存檔路徑。</param>
    /// <returns>說明檔路徑。</returns>
    private static string SidecarPath(string snapshotPath) => Path.ChangeExtension(snapshotPath, ".json");

    /// <summary>刪檔，刪不掉就算了（收拾殘局用，不能蓋掉原本的例外）。</summary>
    /// <param name="path">檔案路徑。</param>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
