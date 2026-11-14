using Planet.Simulation;

namespace Planet.Service;

/// <summary>模擬目前的概況，給前端顯示與除錯用。</summary>
/// <param name="Frequency">粗網格的細分頻率。</param>
/// <param name="CellCount">格數。</param>
/// <param name="PlanetRadiusMeters">星球半徑（公尺），跟正式的2,000萬格星球是同一顆。</param>
/// <param name="AverageCellEdgeMeters">粗網格每格的大致邊長（公尺），方便對照「一格代表多大範圍」。</param>
/// <param name="ElapsedYears">模擬已經跑了多少年。</param>
/// <param name="StepCount">已經跑了幾步。</param>
/// <param name="YearsPerStep">每一步代表多少年。</param>
/// <param name="PlateCount">板塊數量。</param>
/// <param name="PlumeCount">目前活躍的地函熱柱數量。</param>
/// <param name="LandFraction">
/// 陸地佔全球表面的比例（0~1）。水量守恆、海平面由水量反解（見 <c>SimulationEngine.SolveSeaLevel</c>），
/// 所以可以跟地球的 0.29 對照。
/// </param>
/// <param name="TextureWidth">模擬貼圖的寬度（像素）。前端**不要寫死**，一律從這裡拿。</param>
/// <param name="TextureHeight">模擬貼圖的高度（像素）。</param>
/// <param name="Continents">最近一次檢查的陸地配置（停止條件的判斷依據），還沒檢查過是 null。</param>
/// <param name="StoppedByCondition">這次推進是不是因為停止條件成立而提早停下。前端看到 true 就要停止播放。</param>
/// <param name="AutoSaveName">停止條件成立時自動存檔的名稱。</param>
public record SimulationStatusDto(
    int Frequency,
    int CellCount,
    double PlanetRadiusMeters,
    double AverageCellEdgeMeters,
    double ElapsedYears,
    long StepCount,
    double YearsPerStep,
    int PlateCount,
    int PlumeCount,
    double LandFraction,
    int TextureWidth,
    int TextureHeight,
    ContinentConfigurationReport? Continents = null,
    bool StoppedByCondition = false,
    string? AutoSaveName = null);

/// <summary>固定儀表（見 <see cref="SimulationService.GetDashboardAsync"/>）。</summary>
/// <param name="Status">概況。</param>
/// <param name="Continents">陸地配置（現算的，不是停止條件上次檢查的快取）。</param>
/// <param name="Ocean">海洋連通性。</param>
/// <param name="Hypsometry">高度分布（相對海平面，每 100 公尺一格）。</param>
/// <param name="LandMasses">陸塊大小分布。</param>
/// <param name="Mechanisms">各機制上一步的觸發量。</param>
/// <param name="Timings">每步耗時分項（平均毫秒）。</param>
/// <param name="TimingSteps">耗時累積了幾步。</param>
/// <param name="StateHash">整個狀態的雜湊，跨行程重現檢查用。</param>
public record SimulationDashboardDto(
    SimulationStatusDto Status,
    ContinentConfigurationReport Continents,
    OceanTopologyReport Ocean,
    IReadOnlyList<HypsometryBin> Hypsometry,
    IReadOnlyList<LandMassBucket> LandMasses,
    MechanismCounts Mechanisms,
    IReadOnlyList<StepTimingEntry> Timings,
    int TimingSteps,
    string StateHash);
