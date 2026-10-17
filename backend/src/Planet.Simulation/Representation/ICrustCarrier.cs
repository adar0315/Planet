namespace Planet.Simulation.Representation;

/// <summary>
/// 「地殼跟著板塊移動」的一種表示法（v2 規劃 P2 的候選）。
///
/// 只負責**搬運**：給定板塊的剛體旋轉，把地殼的性質從上一步的位置帶到這一步的位置。
/// 地質作用（隱沒、造山、侵蝕…）不在這裡，比較時刻意關掉——P2 要回答的只有
/// 「純粹搬運本身會不會製造雜訊」。v1 的海岸碎裂，源頭就是搬運的取樣雜訊。
///
/// 選定的是 <see cref="PlateFrameCarrier"/>（2026-10-17）。落選的標記法與半拉格朗日在提交 3446c57 裡，
/// 評比數字見 docs/planet-evolution-v2-plan.md 的 P2 結果。介面留著，是讓 <see cref="RepresentationBenchmark"/>
/// 之後還能拿新的變體（例如加上碰撞規則的版本）跟精確解比。
/// </summary>
internal interface ICrustCarrier
{
    /// <summary>給比較表看的名稱。</summary>
    string Name { get; }

    /// <summary>用格子上的地殼初始化。</summary>
    /// <param name="grid">網格。</param>
    /// <param name="plates">板塊（用它目前的歸屬當起點）。</param>
    /// <param name="thicknessKm">每格地殼厚度（公里）。</param>
    /// <param name="continental">每格是不是大陸地殼。</param>
    void Initialize(SimulationGrid grid, PlateSet plates, float[] thicknessKm, bool[] continental);

    /// <summary>
    /// 推進一步。呼叫前 <paramref name="plates"/> 已經 <see cref="PlateSet.Advance"/> 過了
    /// （種子已轉到新位置、歸屬可能已重新分配）。
    /// </summary>
    /// <param name="grid">網格。</param>
    /// <param name="plates">板塊。</param>
    /// <param name="deltaMyr">這一步的時間（百萬年）。</param>
    void Step(SimulationGrid grid, PlateSet plates, double deltaMyr);

    /// <summary>把目前格子上看得到的地殼寫出來。</summary>
    /// <param name="thicknessKm">寫入每格厚度。</param>
    /// <param name="continental">寫入每格是不是大陸。</param>
    void Read(float[] thicknessKm, bool[] continental);

    /// <summary>
    /// 地質作用改完格子之後，把新的厚度寫回表示法。v1 的教訓：地質作用改的是格子，
    /// 不寫回的話，下一次取樣就把這一步的作用整個蓋掉（見 <see cref="PlateTectonics.WriteGridBackToMarkers"/>）。
    /// </summary>
    /// <param name="grid">網格。</param>
    /// <param name="thicknessKm">地質作用之後每格的厚度。</param>
    void WriteBack(SimulationGrid grid, float[] thicknessKm);
}
