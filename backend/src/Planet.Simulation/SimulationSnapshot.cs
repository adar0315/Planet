using System.Runtime.InteropServices;

namespace Planet.Simulation;

/// <summary>
/// 把模擬狀態存成檔案、再讀回來。
///
/// ## 為什麼需要
///
/// 模擬只活在記憶體裡（<c>SimulationService</c> 是 Singleton，每步不寫資料庫——
/// 每步落地會被 I/O 淹沒）。所以服務一重啟，跑了幾千步的星球就沒了，
/// 而 n=150 跑到接近地球的海陸比要約 2,000 步、四到五分鐘。
/// 更麻煩的是交付前的人工處理（碎島清除）也會一起蒸發。
///
/// ## 存什麼、不存什麼
///
/// 存：<see cref="SimulationState"/> 的全部欄位、板塊配置（歐拉極、每格歸屬、種子位置）、
/// 地函熱柱、以及引擎的純量（總水量、各作用距上次執行過了多久）。約 10 MB。
///
/// **不存標記**（marker-in-cell 的位置與屬性，約20 MB）。讀回來時用
/// <c>PlateTectonics.Reset</c> 從格子狀態重建，也就是每格放一個位於中心的標記。
///
/// ### 這代表「續跑不會與未中斷的執行位元相同」
///
/// 標記重建會丟掉次格尺度的分布——原本一格內標記偏左或偏右的資訊沒了。
/// 地形、板塊、水都完全一致，但**接下來的演化會逐漸分岔**。
///
/// 這個取捨是刻意的：省下三分之二的工作量，換一個**本來就還不成立的保證**——
/// 2026-09-10 實測同一個種子在不同行程跑到陸地35%所需的步數就差了18%
/// （離線約2,350步、線上1,975步），跨行程的重現性目前還沒解決。
/// 等那件事查清楚、真的需要位元級續跑時，再把標記加進來。
///
/// ## 格式
///
/// 二進位、小端序，開頭有 magic 與版本。**讀回來時會驗證網格參數**
/// （細分頻率、格數、星球半徑）——存檔是跟特定網格綁定的，
/// 套到不同解析度上只會得到一堆錯位的資料，所以寧可拒絕。
/// </summary>
public static class SimulationSnapshot
{
    /// <summary>檔頭辨識碼。</summary>
    private const ulong Magic = 0x504C414E4554534EUL; // "PLANETSN"

    /// <summary>
    /// 格式版本。**欄位有任何增減都要加一**，並在 <see cref="Restore"/> 拒絕舊版——
    /// 二進位格式讀錯不會拋例外，只會得到亂掉的星球，那比讀不到更難查。
    /// </summary>
    private const int Version = 1;

    /// <summary>把引擎目前的狀態寫進串流。</summary>
    /// <param name="engine">模擬引擎。</param>
    /// <param name="stream">目標串流。</param>
    public static void Write(SimulationEngine engine, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(stream);

        var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(engine.Grid.Frequency);
        writer.Write(engine.Grid.CellCount);
        writer.Write(engine.Grid.PlanetRadiusMeters);
        writer.Write(engine.YearsPerStep);

        engine.State.WriteTo(writer);
        engine.Plates.WriteTo(writer);

        writer.Write(engine.Plumes.Plumes.Length);
        foreach (var plume in engine.Plumes.Plumes)
        {
            writer.Write(plume.X);
            writer.Write(plume.Y);
            writer.Write(plume.Z);
            writer.Write(plume.StrengthKmPerMyr);
            writer.Write(plume.RemainingLifeMyr);
        }

        engine.WriteSnapshotScalars(writer);
        writer.Flush();
    }

    /// <summary>
    /// 從串流把狀態讀回引擎（覆蓋引擎目前的內容）。
    /// </summary>
    /// <param name="engine">要被覆蓋的模擬引擎。</param>
    /// <param name="stream">來源串流。</param>
    /// <exception cref="InvalidDataException">檔頭不對、版本不符，或網格參數跟這個引擎不一致。</exception>
    public static void Restore(SimulationEngine engine, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(stream);

        var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        if (reader.ReadUInt64() != Magic)
        {
            throw new InvalidDataException("這不是模擬存檔（檔頭辨識碼不符）。");
        }

        var version = reader.ReadInt32();
        if (version != Version)
        {
            throw new InvalidDataException(
                $"存檔格式版本是 {version}，這個版本只讀得懂 {Version}。"
                + "二進位格式讀錯不會拋例外、只會得到亂掉的星球，所以直接拒絕。");
        }

        var frequency = reader.ReadInt32();
        var cellCount = reader.ReadInt32();
        var radius = reader.ReadDouble();

        if (frequency != engine.Grid.Frequency
            || cellCount != engine.Grid.CellCount
            || Math.Abs(radius - engine.Grid.PlanetRadiusMeters) > 1e-6)
        {
            throw new InvalidDataException(
                $"存檔的網格是 n={frequency}、{cellCount}格、半徑{radius:F2}公尺，"
                + $"目前的是 n={engine.Grid.Frequency}、{engine.Grid.CellCount}格、"
                + $"半徑{engine.Grid.PlanetRadiusMeters:F2}公尺。存檔跟網格是綁定的，套不過來。");
        }

        engine.YearsPerStep = reader.ReadDouble();

        engine.State.ReadFrom(reader);
        var plates = PlateSet.ReadFrom(reader);

        var plumeCount = reader.ReadInt32();
        var plumes = new MantlePlume[plumeCount];
        for (var i = 0; i < plumeCount; i++)
        {
            plumes[i] = new MantlePlume(
                reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(),
                reader.ReadDouble(), reader.ReadDouble());
        }

        engine.RestoreSnapshotScalars(reader, plates, plumes);
    }

    /// <summary>把一個 blittable 陣列原封不動寫進去（每格一個元素，長度由檔頭的格數決定）。</summary>
    internal static void WriteArray<T>(BinaryWriter writer, T[] values) where T : unmanaged =>
        writer.Write(MemoryMarshal.AsBytes<T>(values));

    /// <summary>
    /// 把一個 blittable 陣列原地讀回來。長度必須已經對了——讀進來的是原始位元組。
    ///
    /// 用 <c>ReadExactly</c> 而不是 <c>Read</c>：後者允許回傳比要求少的位元組
    /// （串流本來就可以分次給），寫成 <c>Read</c> 再自己比對長度的話，
    /// 檔案完好但剛好分段送達時就會誤判成損毀。
    /// </summary>
    internal static void ReadArray<T>(BinaryReader reader, T[] values) where T : unmanaged =>
        reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(new Span<T>(values)));
}
