namespace Planet.Simulation;

/// <summary>
/// 地殼均衡（Isostasy）：由地殼厚度與年齡換算地表高度。
///
/// 這是「為什麼大陸站得比海床高」的物理原因，不是隨便給的規則——地殼浮在密度更大的地函上，
/// 像木頭浮在水上：厚的浮得高、露出得也多。地球上大陸地殼約35公里厚、露出海面約840公尺；
/// 海洋地殼只有約7公里厚，所以整片沉在水下約3,700公尺。青藏高原下方地殼厚達70公里，
/// 因此有全球最高的地面——這就是 <c>地球科學/板塊運動.md</c> 說的「地殼均衡與山根」。
///
/// **兩種地殼用不同的公式，這是刻意的**：
/// 大陸地殼幾乎不會被隱沒消耗，年齡沒有意義，高度主要由厚度決定；
/// 海洋地殼則相反，厚度幾乎固定在7公里，深度主要由**年齡**決定——
/// 從中洋脊新生時最淺，之後隨著冷卻收縮而愈來愈深，這是海底地形最主要的規律。
/// </summary>
public static class Isostasy
{
    /// <summary>海洋地殼的典型厚度（公里）。全球差異很小，所以海底深度用年齡算而不是用厚度算。</summary>
    public const float OceanicCrustThicknessKm = 7f;

    /// <summary>大陸地殼的典型厚度（公里），對應「平均陸地高度」。</summary>
    public const float ContinentalCrustThicknessKm = 35f;

    /// <summary>厚度超過這個值就視為已經成熟到算大陸地殼（島弧不斷增生最終形成大陸的門檻）。</summary>
    public const float ContinentalThresholdKm = 25f;

    /// <summary>地殼厚度上限（公里）。青藏高原下方約70公里，這裡留一點餘裕當作物理上限，避免碰撞區無限增厚。</summary>
    public const float MaxCrustThicknessKm = 75f;

    /// <summary>典型厚度的大陸地殼所對應的地表高度（公尺）。地球陸地的平均高度約840公尺。</summary>
    private const float ContinentalReferenceElevationM = 840f;

    /// <summary>
    /// 地殼每多1公里厚度，地表升高幾公尺。
    ///
    /// 這不是湊出來的數字，是 Airy 均衡的結果：e = h·(ρ地函 − ρ地殼)/ρ地函。
    /// 代入地函3,300、大陸地殼2,700 kg/m³ → (3300−2700)/3300 = 0.182，即每公里厚度換到182公尺高度。
    /// 驗算：70公里厚的地殼 → 840 + (70−35)×182 ≈ 7,200公尺，跟青藏高原＋喜馬拉雅同一個量級
    /// （實際約5,000公尺，差距來自侵蝕與動力支撐，侵蝕會在階段4補上）。
    ///
    /// 公開是因為 <c>SimulationState.KeepEverythingBelowSeaLevel</c> 要反解「還能有多厚」。
    /// </summary>
    public const float MetersPerKmOfCrust = 182f;

    /// <summary>中洋脊處（年齡0）的海底深度（公尺）。地球的中洋脊頂部約在水下2,500公尺。</summary>
    private const float RidgeDepthM = 2500f;

    /// <summary>
    /// 海底深度隨年齡平方根增加的係數（公尺／√百萬年）。
    ///
    /// 深度 ∝ √年齡 是海洋地殼冷卻收縮的標準經驗律（half-space cooling model），不是隨便挑的函數形狀。
    /// 取350：80百萬年的老海床 → 2500 + 350×√80 ≈ 5,630公尺，符合實際的深海平原深度。
    /// </summary>
    private const float DepthPerSqrtMyrM = 350f;

    /// <summary>海底深度上限（公尺）。再老的海洋地殼也會趨於平衡，不會無限下沉。</summary>
    private const float MaxOceanDepthM = 6000f;

    /// <summary>地函密度（公斤／立方公尺）。Airy 均衡的分母。</summary>
    public const float MantleDensity = 3300f;

    /// <summary>典型海洋地殼的密度（公斤／立方公尺）。</summary>
    public const float OceanicCrustDensity = 2900f;

    /// <summary>典型大陸地殼的密度（公斤／立方公尺）。比海洋地殼輕，所以浮得高、隱沒不下去。</summary>
    public const float ContinentalCrustDensity = 2700f;

    /// <summary>地殼厚度的下限（公里）。侵蝕不能把地殼整個磨光，那在物理上沒有意義。</summary>
    public const float MinCrustThicknessKm = 3f;

    /// <summary>
    /// **移除1單位地殼厚度，地表只會降低多少**（等均衡反彈之後的淨值）。
    ///
    /// 這就是 Airy 均衡的 Δρ/ρ地函：地殼浮在地函上，拿掉一部分之後剩下的會浮起來補回大半。
    /// 大陸地殼是 (3300−2700)/3300 = 0.182，海洋地殼是 (3300−2900)/3300 = 0.121。
    ///
    /// **侵蝕一定要用這個換算**：地表每降1公尺，實際要移除 1/0.182 ≈ 5.5 公尺的大陸地殼。
    /// 少了它會低估5.5倍，侵蝕看起來有在跑卻完全擋不住地殼增厚。
    /// </summary>
    /// <param name="crust">地殼類型。</param>
    public static float IsostaticSurfaceRatio(CrustType crust) =>
        crust == CrustType.Continental
            ? (MantleDensity - ContinentalCrustDensity) / MantleDensity
            : (MantleDensity - OceanicCrustDensity) / MantleDensity;

    /// <summary>沉積物的密度（公斤／立方公尺）。壓實、含水的碎屑沉積物約2,400~2,600。</summary>
    public const float SedimentDensity = 2500f;

    /// <summary>有機沉積（珊瑚礁的碳酸鈣、石灰岩）的密度。比碎屑沉積物略高。</summary>
    // 目前沒有地方用到——礁灰岩刻意不算在 LoadDepressionMeters 的載重裡，理由見那個方法
    // 的說明（撓曲波長）。留著是因為它是真實的物理常數，等岩石圈撓曲做出來就會用到；
    // **不要因為「沒人用」就把礁體加回局部均衡**，那會讓礁體長到14公里。
    public const float OrganicDensity = 2600f;

    /// <summary>冰的密度。</summary>
    public const float IceDensity = 917f;

    /// <summary>
    /// 疊在基岩上的載重會把地殼壓下去多少（公尺）。
    ///
    /// 這是 Airy 均衡的另一半：地殼浮在地函上，**上面壓東西就會沉下去**，
    /// 沉降量 = 載重厚度 × 載重密度 ÷ 地函密度。
    ///
    /// 少了這一項會缺兩個真實的地質現象（見 <c>地球科學/地球表面的「隆起」與「凹陷」.md</c>）：
    ///
    /// - **沉積盆地的正回饋**：泥沙堆積 → 變重 → 地殼下沉 → 容納更多泥沙 → 更重 → 更沉。
    ///   這是厚達數公里的沉積盆地（也是石油、天然氣的形成環境）的成因。
    ///   沒有它的話沉積物只會把地表**墊高**，河口與盆地永遠不會下陷。
    /// - **冰後回彈**：南極的基岩有相當面積被三公里厚的冰壓到海平面以下；
    ///   斯堪地那維亞至今仍以每年約1公分上升，那是上一次冰期的載重卸除後還沒回彈完。
    ///
    /// 淨效果：每加1公尺沉積物，地表只升高 1 − 2500/3300 ≈ 0.24 公尺；
    /// 每加1公尺冰，地表升高 1 − 917/3300 ≈ 0.72 公尺。
    /// 驗算：南極3公里厚的冰壓下 3000 × 917/3300 ≈ 833 公尺，與實測的量級相符。
    ///
    /// 植被的質量小到可以忽略，不計入。
    ///
    /// ## ⚠️ 珊瑚礁**不算**在載重裡（2026-09-07 量測後決定）
    ///
    /// 局部 Airy 均衡只對「比岩石圈撓曲波長（約100~200公里）**寬得多**」的載重成立。
    /// 冰蓋（數千公里）與沉積盆地（數百公里）符合；珊瑚礁不符合——一格才15公里（n=150）
    /// 到93公里（n=24），這種尺度的載重是被岩石圈的**剛性**撐住的，不是被地函浮力。
    ///
    /// 算進來的話每長1公尺礁灰岩會壓下 2600/3300 = 0.79 公尺、淨升只有0.21，
    /// 於是要填1公尺的空間得長 1/0.21 = **4.76 公尺**，而那4.76公尺又壓出新的空間。
    /// 實測（n=24，100 Myr）：地殼冷卻的總下沉是 350×√100 = 3,500 公尺，
    /// 礁體卻長到 14,347 公尺——比值 4.1，就是這個倍率。
    ///
    /// 真實世界給得出正確的比值：恩尼威托克環礁底下有1.4公里的礁灰岩，
    /// 對應的冷卻下沉約1.0公里，比值 **1.4**，不是4.76。差在岩石圈的撓曲支撐。
    /// 排除礁體等於取比值1.0，比留著的4.76接近事實得多。
    ///
    /// 正解是加上岩石圈撓曲（用彈性板取代局部補償），那是還沒做的機制；
    /// 在那之前，排除比套一個量測上明顯錯誤的補償好。
    /// </summary>
    /// <param name="sedimentMeters">沉積物厚度（公尺）。</param>
    /// <param name="iceMeters">冰蓋厚度（公尺）。</param>
    public static float LoadDepressionMeters(float sedimentMeters, float iceMeters) =>
        (sedimentMeters * SedimentDensity + iceMeters * IceDensity) / MantleDensity;

    /// <summary>
    /// 由地殼類型、厚度、年齡、密度算出基岩表面高度（公尺，相對海平面）。
    ///
    /// 密度是以「相對於該類型的典型密度」的方式加成的：密度低於典型值就浮得更高，
    /// 幅度依 Airy 均衡 <c>Δe = h·Δρ/ρ地函</c>。這樣既保留了原本已經對得上地球的
    /// 厚度／年齡公式，又讓早期大轟炸留下的密度差異真的產生高度差
    /// （見 <c>SimulationState.ApplyEarlyBombardment</c>）。
    /// </summary>
    /// <param name="crust">地殼類型。</param>
    /// <param name="thicknessKm">地殼厚度（公里）。</param>
    /// <param name="ageMyr">地殼年齡（百萬年），只有海洋地殼會用到。</param>
    /// <param name="densityKgPerM3">地殼密度（公斤／立方公尺）。</param>
    public static float BedrockElevation(CrustType crust, float thicknessKm, float ageMyr, float densityKgPerM3)
    {
        var referenceDensity = crust == CrustType.Continental ? ContinentalCrustDensity : OceanicCrustDensity;
        var densityLift = thicknessKm * 1000f * (referenceDensity - densityKgPerM3) / MantleDensity;

        if (crust == CrustType.Continental)
        {
            return ContinentalReferenceElevationM
                + (thicknessKm - ContinentalCrustThicknessKm) * MetersPerKmOfCrust
                + densityLift;
        }

        var depth = RidgeDepthM + DepthPerSqrtMyrM * MathF.Sqrt(MathF.Max(ageMyr, 0f));

        // 海洋地殼的厚度變化也要反映到高度上（早期大轟炸會讓它一開始就不是均勻的7公里）
        var thicknessLift = (thicknessKm - OceanicCrustThicknessKm) * MetersPerKmOfCrust;

        return -MathF.Min(depth, MaxOceanDepthM) + thicknessLift + densityLift;
    }
}
