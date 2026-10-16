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
/// ## 存什麼
///
/// **完整狀態，讀回續跑與沒中斷的執行位元相同**（版本 2，2026-10-16）。
/// 守門的是 <c>SimulationSnapshotTests.RoundTrip_ResumeIsBitIdenticalToUninterruptedRun</c>：
/// 在不整齊的第 317 步存檔、續跑 300 步，每一步每個欄位都比對。
///
/// - <see cref="SimulationState"/> 的全部欄位
/// - 板塊：歐拉極、每格歸屬、種子位置，以及**重新分配的累積狀態**（種子漂移量、上次全掃的勝負差）
/// - marker-in-cell 的標記與每格的取樣快照（約 20 MB）
/// - 地函熱柱，以及它的亂數產生器抽過幾次（讀回時重播）
/// - 引擎的純量：總水量、各週期作用距上次執行過了多久
///
/// 每步開頭就重算的暫存（聚合速率、線段、造山帶、氣候的中間量…）不存。
///
/// ### 版本 1 為什麼不夠
///
/// 版本 1 不存標記，讀回時從格子重建，以為「地形一樣、只差次格尺度」。實測讀回續跑
/// **第 1 步完全相同、第 2 步就有 9 個欄位分歧**：標記約每兩步重新取樣到格子上一次，一取樣就露餡。
/// 補上標記之後還是第 2 步分歧——兇手是 <c>PlateSet._seedDriftMeters</c>：板塊歸屬只在種子累積
/// 漂移超過 0.25 格距時才重算，那個累積量歸零的一顆會晚幾步才重算。
///
/// **找漏存的狀態不要靠讀程式碼**：版本 1 時列的清單漏了它，補標記時又以為只差增量分配的快取。
/// 真正找到它的方法是讀檔後用反射掃過每個子系統的所有私有欄位，跟沒中斷的那一個逐欄比對雜湊——
/// 第 1 步之後還不一樣、又不是暫存的，就是漏存的。
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
    private const int Version = 3; // 2：加上標記、板塊重新分配的累積狀態、熱柱亂數（續跑位元相同）；3：停止條件的計時與邊緣觸發狀態

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
