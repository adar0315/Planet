namespace Planet.Simulation;

/// <summary>
/// 蓋層的岩石種類（水文地貌規劃 H1，2026-10-24）。
///
/// 基岩分兩層看：底下的**基盤**由地殼型別決定（大陸＝結晶基盤的花崗片麻岩、海洋＝玄武岩），
/// 上面可以有一層**蓋層**（<see cref="SimulationState.CoverThickness"/> 公尺厚），種類記在這裡。
/// 侵蝕先刨蓋層，刨穿了才露出基盤——所以被抬升、侵蝕得夠深的山脈核心自然會露出結晶岩，
/// 不必另外寫「造山帶核心是花崗岩」的規則。
///
/// 蓋層是基岩頂部的**標籤**，不是另一份物質：它的厚度已經算在地殼厚度裡，均衡、高度都不必改。
/// </summary>
public enum CoverRock : byte
{
    /// <summary>沉積岩（砂岩、頁岩、石灰岩）：軟、透水。大陸邊緣的沉積蓋層。</summary>
    Sedimentary = 0,

    /// <summary>火山岩：火山弧、熱柱、裂縫噴發堆出來的。</summary>
    Volcanic = 1,

    /// <summary>混雜岩：隱沒時刮進增積楔的沉積物（台灣、日本的增積岩體）。</summary>
    Melange = 2,
}

/// <summary>地表露出來的是哪一種岩石（給侵蝕、水文、畫圖用）。</summary>
public enum ExposedRock : byte
{
    /// <summary>鬆散沉積物（沉積物厚度超過 <see cref="Lithology.LooseSedimentExposedMeters"/>）。</summary>
    LooseSediment = 0,

    /// <summary>沉積岩蓋層。</summary>
    Sedimentary = 1,

    /// <summary>火山岩蓋層。</summary>
    Volcanic = 2,

    /// <summary>混雜岩蓋層。</summary>
    Melange = 3,

    /// <summary>結晶基盤（大陸地殼沒有蓋層時）。</summary>
    Crystalline = 4,

    /// <summary>海洋地殼的玄武岩（海洋地殼沒有蓋層時）。</summary>
    OceanicBasalt = 5,
}

/// <summary>
/// 岩性的規則與係數。係數都是**相對值**（結晶基盤以外的平均岩石約 1），
/// 侵蝕係數在 H2 乘進流水侵蝕的 K、透水性在 H4 決定地下水基流。
/// </summary>
public static class Lithology
{
    /// <summary>
    /// 沉積物厚過這個值（公尺），地表就算是鬆散沉積物；更薄的話底下的岩石會在河床、坡面上露出來。
    /// </summary>
    public const float LooseSedimentExposedMeters = 20f;

    /// <summary>
    /// 起始大陸邊緣的沉積蓋層厚度（公尺，海岸處）。地球的被動大陸邊緣沉積楔常見 3~10 公里，
    /// 但大部分在海面下；露在陸地上的沿海平原蓋層約 1~3 公里。
    /// </summary>
    public const float CoastalCoverMeters = 3_000f;

    /// <summary>沉積蓋層從海岸往內陸變薄到 0 的距離（公里）。</summary>
    public const double CoverRampKm = 300;

    /// <summary>內陸沉積盆地的蓋層最厚多少（公尺）。克拉通內部也有盆地（北美的威利斯頓盆地、俄羅斯地台）。</summary>
    public const float InteriorBasinCoverMeters = 2_000f;

    /// <summary>
    /// 蓋層最厚多少（公尺）。蓋層只是基岩頂部的標籤，火山弧一直噴、增積楔一直刮的話會無上限地長，
    /// 但超過幾公里之後侵蝕根本刨不穿，再厚也不會改變露出來的岩石——夾住只是讓數字有意義。
    /// </summary>
    public const float MaxCoverMeters = 15_000f;

    /// <summary>火山活動低於這個值（公里地殼／百萬年）不算噴發，不加火山岩蓋層（衰減中的餘值）。</summary>
    public const float MinVolcanicActivityKmPerMyr = 0.01f;

    /// <summary>地表露出來的岩石。</summary>
    /// <param name="crust">地殼型別（決定基盤）。</param>
    /// <param name="coverMeters">蓋層厚度。</param>
    /// <param name="cover">蓋層種類。</param>
    /// <param name="sedimentMeters">鬆散沉積物厚度。</param>
    public static ExposedRock Exposed(CrustType crust, float coverMeters, CoverRock cover, float sedimentMeters)
    {
        if (sedimentMeters >= LooseSedimentExposedMeters) return ExposedRock.LooseSediment;
        if (coverMeters > 0f)
        {
            return cover switch
            {
                CoverRock.Volcanic => ExposedRock.Volcanic,
                CoverRock.Melange => ExposedRock.Melange,
                _ => ExposedRock.Sedimentary,
            };
        }
        return crust == CrustType.Continental ? ExposedRock.Crystalline : ExposedRock.OceanicBasalt;
    }

    /// <summary>某一格地表露出來的岩石。</summary>
    /// <param name="state">模擬狀態。</param>
    /// <param name="cell">格子索引。</param>
    public static ExposedRock ExposedAt(SimulationState state, int cell) =>
        Exposed(state.Crust[cell], state.CoverThickness[cell], state.CoverRock[cell], state.SedimentThickness[cell]);

    /// <summary>
    /// 侵蝕係數（乘在流水侵蝕的 K 上）。硬的結晶岩只有一般岩石的一半，留下山脊與峽谷；
    /// 沉積岩、鬆散沉積物被削成盆地與平原。
    /// </summary>
    /// <param name="rock">露出的岩石。</param>
    public static float Erodibility(ExposedRock rock) => rock switch
    {
        ExposedRock.LooseSediment => 2.0f,
        ExposedRock.Sedimentary => 1.6f,
        ExposedRock.Melange => 1.2f,
        ExposedRock.Volcanic => 0.8f,
        ExposedRock.OceanicBasalt => 0.8f,
        _ => 0.5f, // 結晶基盤
    };

    /// <summary>
    /// 透水性（0~1，給地下水基流用）：雨水有多少比例滲進地下、之後慢慢流回河裡。
    /// 鬆散沉積物與沉積岩是好的含水層；結晶岩只有裂隙水。
    /// </summary>
    /// <param name="rock">露出的岩石。</param>
    public static float Permeability(ExposedRock rock) => rock switch
    {
        ExposedRock.LooseSediment => 0.8f,
        ExposedRock.Sedimentary => 0.6f,
        ExposedRock.Volcanic => 0.5f, // 玄武岩的柱狀節理、熔岩管透水性其實不差（夏威夷、冰島的大泉）
        ExposedRock.Melange => 0.3f,
        ExposedRock.OceanicBasalt => 0.3f,
        _ => 0.15f, // 結晶基盤
    };

    /// <summary>
    /// 基岩被刨掉 <paramref name="surfaceLoweringMeters"/> 公尺：先刨蓋層。
    /// 跟 <see cref="FluvialErosion"/> 刨基岩的那一段一起呼叫，地表降低多少、蓋層就薄多少。
    /// </summary>
    /// <param name="state">模擬狀態。</param>
    /// <param name="cell">格子索引。</param>
    /// <param name="surfaceLoweringMeters">基岩的地表降低量（公尺，不含均衡反彈前的換算）。</param>
    public static void StripCover(SimulationState state, int cell, double surfaceLoweringMeters)
    {
        if (surfaceLoweringMeters <= 0) return;
        state.CoverThickness[cell] = (float)Math.Max(0.0, state.CoverThickness[cell] - surfaceLoweringMeters);
    }

    /// <summary>
    /// 噴發中的格子蓋上一層火山岩（板塊座標系模式每步呼叫，排在火山之後、回寫之前）。
    ///
    /// 不在各個火山作用裡分別寫：火山有熱柱、裂縫、島弧、中洋脊四條路徑，分散寫的話之後加第五條一定會漏
    /// （同一個理由見 <see cref="SimulationState.TryMakeContinental"/>）。<see cref="SimulationState.VolcanicActivity"/>
    /// 是四條路徑共同的出口。中洋脊不加：新生的洋殼本身就是玄武岩基盤。
    /// </summary>
    /// <param name="state">模擬狀態。</param>
    /// <param name="deltaMyr">這一步幾百萬年。</param>
    public static void ApplyVolcanicCover(SimulationState state, double deltaMyr)
    {
        Parallel.For(0, state.CellCount, c =>
        {
            var activity = state.VolcanicActivity[c];
            if (activity < MinVolcanicActivityKmPerMyr || state.Boundary[c] == BoundaryType.Divergent) return;
            var addedMeters = activity * deltaMyr * 1000.0 * Isostasy.IsostaticSurfaceRatio(state.Crust[c]);
            // 新噴出的熔岩蓋在最上面：底下原本是沉積岩的話也被蓋住了（簡化成整層變火山岩）
            state.CoverRock[c] = CoverRock.Volcanic;
            state.CoverThickness[c] = (float)Math.Min(MaxCoverMeters, state.CoverThickness[c] + addedMeters);
        });
    }
}
