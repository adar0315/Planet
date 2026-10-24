namespace Planet.Simulation;

/// <summary>河的水情（水文地貌規劃 H4）。</summary>
public enum FlowRegime : byte
{
    /// <summary>不是河。</summary>
    None = 0,

    /// <summary>常流河：每個月都有相當的水（地下水、湖、冰雪撐住了乾季）。</summary>
    Perennial = 1,

    /// <summary>季節河：有明顯的枯水期，但不會乾掉太久。</summary>
    Seasonal = 2,

    /// <summary>間歇河：一年有一半以上的時間幾乎是乾河床（沙漠的乾溝、雨季才有水的河）。</summary>
    Ephemeral = 3,
}

/// <summary>
/// 四季的水文（水文地貌規劃 H4，2026-10-24）：每個時段的降水先經過積雪、冰川、地下水、湖泊這幾個「水庫」，
/// 再沿著 <see cref="Hydrology"/> 的流向匯成河。
///
/// ## 為什麼要分水庫
///
/// 全年平均的水系只知道一年流多少水，不知道什麼時候流。河流不乾涸的關鍵是**時間差**：
///
/// - **地下水**：雨季滲進地下，乾季以泉水、滲流慢慢流回河床（基流）。透水的岩石（沉積岩、鬆散沉積物）存得多、放得慢。
/// - **積雪與冰川**：冬天把水凍在山上，春夏融化。
/// - **湖泊**：豐水期蓄水、枯水期放水。
/// - **乾燥地區的河道損失**：沿途蒸發、滲漏，雪山流進沙漠的河可能消失在半路。
///
/// ## 年總量不變
///
/// 每格各時段的逕流加總，會縮放成全年平均水系（<see cref="Hydrology.Discharge"/> 的輸入）的逕流——
/// 四季只改變水「什麼時候」來；侵蝕、湖泊用的年平均水系完全不受影響。只有河道損失會讓下游的水變少，
/// 那是刻意的（外來河）。
///
/// 一年的循環要反覆跑到每年都一樣（水庫的初值不影響結果），有次數上限，超過就丟例外（CLAUDE.md 3.7）。
/// </summary>
public static class SeasonalHydrology
{
    /// <summary>
    /// 度日融雪係數：月均溫每高過 0°C 一度，一年可以融掉多少雪（相對全球平均年降水的倍數）。
    /// 高山集水區四個月、平均 5°C 要融掉約一整年的降水，所以約 0.6。
    /// </summary>
    private const double MeltPerDegreePerMeanPrecipitation = 0.6;

    /// <summary>逕流裡最多有多少比例先滲進地下（透水性 1 的時候）。</summary>
    private const double MaxInfiltrationShare = 0.8;

    /// <summary>地下水的滯留時間（年）＝ 最短 + 透水性 × 這個值。結晶岩的裂隙水幾個月就流完，厚的沉積含水層撐一整年以上。</summary>
    private const double GroundwaterResidenceYearsPerPermeability = 1.2;

    /// <summary>地下水最短的滯留時間（年）。</summary>
    private const double MinGroundwaterResidenceYears = 0.1;

    /// <summary>湖泊調節的時間常數（年）：入流的起伏被湖面吸收，出流慢慢跟上。</summary>
    private const double LakeRegulationYears = 0.5;

    /// <summary>
    /// 乾燥地區河道每公里損失多少比例（完全乾燥時）。尼羅河流過撒哈拉的上千公里損失約三成以上；
    /// 每公里 0.15% 在 500 公里的沙漠裡損失約一半。損失隨乾燥程度遞減，年降水到全球平均以上就沒有損失。
    /// </summary>
    private const double AridLossPerKm = 0.0015;

    /// <summary>冰川（冰厚超過這個值，公尺）上的水整年存著，只在比 −2°C 暖的時段放出來。</summary>
    private const float GlacierIceMeters = 10f;

    /// <summary>淡水（河、湖）結冰的月均溫（攝氏）。</summary>
    private const float FreshwaterFreezeC = -2f;

    /// <summary>海水結冰的月均溫（攝氏）：鹽水的冰點約 −1.8°C，月均溫還要再冷一點才會整片結冰。</summary>
    private const float SeaFreezeC = -4f;

    /// <summary>積雪水當量超過全球平均年降水的這個比例，就算地面被雪蓋住。</summary>
    private const double SnowCoverShareOfMeanPrecipitation = 0.02;

    /// <summary>常流河：每個時段的流量都至少是平均的這個比例。</summary>
    private const double PerennialMinShare = 0.15;

    /// <summary>流量低於平均的這個比例算「乾河床」。</summary>
    private const double DryShare = 0.05;

    /// <summary>洪峰係數的上限。</summary>
    private const float MaxFloodFactor = 3f;

    /// <summary>一年的循環最多反覆幾次。</summary>
    private const int MaxSpinUpYears = 40;

    /// <summary>連續兩年水庫的差異小於全年逕流的這個比例，就算收斂。</summary>
    private const double ConvergenceTolerance = 1e-4;

    /// <summary>
    /// 解四季的水文。要先解好年平均的水系（<see cref="Hydrology.ComputeDischarge"/>）與四季的氣候（<see cref="Climate.SolveSeasons"/>）。
    /// </summary>
    /// <param name="grid">網格。</param>
    /// <param name="state">模擬狀態（年平均的溫度、降水、岩性、冰）。</param>
    /// <param name="hydrology">年平均的水系（流向、湖、湖的出流）。</param>
    /// <param name="climate">四季的氣候。</param>
    public static SeasonalHydrologyResult Solve(SimulationGrid grid, SimulationState state, Hydrology hydrology, SeasonalClimate climate)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(hydrology);
        ArgumentNullException.ThrowIfNull(climate);
        var n = state.CellCount;
        var seasons = climate.Seasons;
        var dt = 1.0 / seasons; // 年
        var seaLevel = state.SeaLevelMeters;
        var flood = hydrology.FloodLevel;
        var lakeOf = hydrology.LakeOf;
        var basinOf = hydrology.BasinOf;
        var parent = hydrology.FlowParent;
        var order = hydrology.FlowOrder;
        var orderCount = hydrology.FlowOrderCount;
        var meanPrecipitation = state.Precipitation.Average();
        var result = new SeasonalHydrologyResult(seasons, n);

        // 會產生逕流的格：陸地、不在海裡、不在湖裡（跟 Hydrology.ComputeDischarge 同一個判斷）
        bool Contributes(int c) => flood[c] >= seaLevel && lakeOf[c] < 0;

        // ---- 1. 每格的逕流：積雪、冰川、地下水 ----
        var quick = new double[seasons][];
        var baseflow = new double[seasons][];
        var melt = new double[seasons][];
        for (var m = 0; m < seasons; m++) { quick[m] = new double[n]; baseflow[m] = new double[n]; melt[m] = new double[n]; }
        var meltPerDegree = MeltPerDegreePerMeanPrecipitation * meanPrecipitation;
        var snowCover = SnowCoverShareOfMeanPrecipitation * meanPrecipitation;
        var failed = 0;

        Parallel.For(0, n, c =>
        {
            if (!Contributes(c)) return;
            var permeability = Lithology.Permeability(Lithology.ExposedAt(state, c));
            var infiltration = MaxInfiltrationShare * permeability;
            var tau = MinGroundwaterResidenceYears + GroundwaterResidenceYearsPerPermeability * permeability;
            var release = 1 - Math.Exp(-dt / tau);
            var glacier = state.IceThickness[c] > GlacierIceMeters;

            double snowpack = 0, groundwater = 0, previousSnow = double.NaN, previousGround = double.NaN;
            var converged = false;
            var perennialSnow = glacier || AlwaysFrozen(climate, c);
            var generated = new double[seasons];
            for (var year = 0; year < MaxSpinUpYears && !converged; year++)
            {
                var yearGenerated = 0.0;
                for (var m = 0; m < seasons; m++)
                {
                    var p = climate.Precipitation[m][c] * dt;
                    var t = climate.Temperature[m][c];
                    var snowShare = Climate.SnowFraction(t);
                    var rain = p * (1 - snowShare);
                    snowpack += p * snowShare;
                    double meltWater;
                    if (glacier)
                    {
                        // 冰川：雪與雨都先存著，暖的時段才放出來（下面依暖的程度分配）
                        snowpack += rain;
                        rain = 0;
                        meltWater = 0;
                    }
                    else
                    {
                        meltWater = Math.Min(snowpack, meltPerDegree * Math.Max(t, 0f) * dt);
                        snowpack -= meltWater;
                    }
                    var runoff = rain * Climate.RunoffFraction + meltWater * Hydrology.SnowmeltRunoffFraction;
                    var recharge = runoff * infiltration;
                    groundwater += recharge;
                    var outflow = groundwater * release;
                    groundwater -= outflow;
                    quick[m][c] = runoff - recharge;
                    baseflow[m][c] = outflow;
                    melt[m][c] = meltWater * Hydrology.SnowmeltRunoffFraction * (1 - infiltration);
                    generated[m] = runoff;
                    yearGenerated += runoff;
                    result.SnowWater[m][c] = (float)snowpack;
                }
                if (perennialSnow)
                {
                    // 冰川、終年冰凍的地方積雪年年增加，不會收斂：一年的水依各時段暖的程度放出來（全冷就平均）。
                    // 這些地方的雪是冰蓋的一部分，冰蓋本身的質量平衡在 Cryosphere。
                    snowpack = 0;
                    converged = true;
                    break;
                }
                var scale = Math.Max(yearGenerated, 1e-12);
                var groundSettled = Math.Abs(groundwater - previousGround) < ConvergenceTolerance * scale;
                if (year >= 3 && groundSettled && snowpack - previousSnow > ConvergenceTolerance * scale)
                {
                    // 夏天太短、融不完一年的雪：雪年年累積，是萬年雪（粒雪盆）——跟冰川同樣處理
                    perennialSnow = true;
                    converged = true;
                    break;
                }
                converged = Math.Abs(snowpack - previousSnow) < ConvergenceTolerance * scale && groundSettled;
                previousSnow = snowpack;
                previousGround = groundwater;
            }
            if (!converged) Interlocked.Increment(ref failed);

            if (perennialSnow)
            {
                var weights = new double[seasons];
                var sum = 0.0;
                for (var m = 0; m < seasons; m++) { weights[m] = Math.Max(climate.Temperature[m][c] - FreshwaterFreezeC, 0f); sum += weights[m]; }
                for (var m = 0; m < seasons; m++)
                {
                    var share = sum > 0 ? weights[m] / sum : 1.0 / seasons;
                    var total = state.Precipitation[c] * Hydrology.SnowmeltRunoffFraction * share;
                    quick[m][c] = total;
                    melt[m][c] = total;
                    baseflow[m][c] = 0;
                }
            }

            // 全年的總量縮放成年平均水系的逕流：四季只改變水什麼時候來
            var annual = 0.0;
            for (var m = 0; m < seasons; m++) annual += (quick[m][c] + baseflow[m][c]) * seasons;
            annual /= seasons;
            var target = state.Precipitation[c] * Hydrology.RunoffFractionAtCell(state, c);
            var k = annual > 1e-12 ? target / annual : 0;
            for (var m = 0; m < seasons; m++)
            {
                // 每個時段的逕流換成「年率」（跟 Discharge 同單位）
                quick[m][c] *= k * seasons;
                baseflow[m][c] *= k * seasons;
                melt[m][c] *= k * seasons;
            }
        });
        if (failed > 0)
            throw new InvalidOperationException($"四季水文有 {failed} 格的積雪或地下水跑了 {MaxSpinUpYears} 年還沒有收斂。");

        // ---- 2. 沿流向匯流：湖泊調節、乾燥地區的河道損失 ----
        var basinCount = hydrology.BasinCount;
        var basinsByOutlet = new Dictionary<int, List<int>>();
        for (var b = 0; b < basinCount; b++)
        {
            if (!hydrology.HoldsWater(b)) continue;
            var outlet = hydrology.BasinOutlet(b);
            if (outlet < 0) continue;
            if (!basinsByOutlet.TryGetValue(outlet, out var list)) basinsByOutlet[outlet] = list = [];
            list.Add(b);
        }
        var spacingKm = grid.AverageCellSpacingMeters / 1000.0;
        var keep = new float[n];
        Parallel.For(0, n, c =>
        {
            var aridity = Math.Clamp(1 - state.Precipitation[c] / Math.Max(meanPrecipitation, 1e-9), 0, 1);
            keep[c] = (float)Math.Exp(-AridLossPerKm * aridity * spacingKm);
        });

        var total = new double[n];
        var fromBase = new double[n];
        var fromMelt = new double[n];
        var lakeIn = new double[basinCount];
        var lakeInBase = new double[basinCount];
        var lakeInMelt = new double[basinCount];
        var lakeAnomaly = new double[basinCount];
        var lakeMeanIn = new double[basinCount];
        var lakeYearIn = new double[basinCount];
        var lakeShareBase = new double[basinCount];
        var lakeShareMelt = new double[basinCount];
        var annualBase = new double[n];
        var annualMelt = new double[n];

        // 湖的調節要跑幾年才穩定（平均入流用上一年的）；河道的匯流本身每個時段都是穩態
        const int LakeYears = 6;
        for (var year = 0; year < LakeYears; year++)
        {
            Array.Clear(lakeYearIn);
            for (var m = 0; m < seasons; m++)
            {
                for (var c = 0; c < n; c++)
                {
                    total[c] = Contributes(c) ? quick[m][c] + baseflow[m][c] : 0;
                    fromBase[c] = Contributes(c) ? baseflow[m][c] : 0;
                    fromMelt[c] = Contributes(c) ? melt[m][c] : 0;
                }
                Array.Clear(lakeIn);
                Array.Clear(lakeInBase);
                Array.Clear(lakeInMelt);
                for (var i = orderCount - 1; i >= 0; i--)
                {
                    var c = order[i];
                    if (flood[c] < seaLevel) continue;
                    if (basinsByOutlet.TryGetValue(c, out var basins))
                    {
                        foreach (var b in basins) InjectLakeOutflow(b, c);
                    }
                    var basin = basinOf[c];
                    if (basin >= 0 && hydrology.HoldsWater(basin))
                    {
                        lakeIn[basin] += total[c];
                        lakeInBase[basin] += fromBase[c];
                        lakeInMelt[basin] += fromMelt[c];
                        continue;
                    }
                    var p = parent[c];
                    if (p < 0) continue;
                    total[p] += total[c] * keep[c];
                    fromBase[p] += fromBase[c] * keep[c];
                    fromMelt[p] += fromMelt[c] * keep[c];
                }
                for (var b = 0; b < basinCount; b++)
                {
                    lakeYearIn[b] += lakeIn[b] / seasons;
                    if (lakeIn[b] > 0)
                    {
                        lakeShareBase[b] = lakeInBase[b] / lakeIn[b];
                        lakeShareMelt[b] = lakeInMelt[b] / lakeIn[b];
                    }
                }
                if (year == LakeYears - 1)
                {
                    for (var c = 0; c < n; c++)
                    {
                        var river = flood[c] >= seaLevel && lakeOf[c] < 0 ? total[c] : 0;
                        result.Discharge[m][c] = (float)river;
                        annualBase[c] += (flood[c] >= seaLevel && lakeOf[c] < 0 ? fromBase[c] : 0) / seasons;
                        annualMelt[c] += (flood[c] >= seaLevel && lakeOf[c] < 0 ? fromMelt[c] : 0) / seasons;
                    }
                }
            }
            Array.Copy(lakeYearIn, lakeMeanIn, basinCount);
        }

        void InjectLakeOutflow(int b, int outlet)
        {
            // 湖的出流：平均值照年平均的水系，起伏被湖面吸收（線性水庫），比入流平緩、落後
            var mean = hydrology.BasinOutflow(b);
            if (mean <= 0) return;
            var meanIn = lakeMeanIn[b] > 0 ? lakeMeanIn[b] : lakeIn[b];
            lakeAnomaly[b] += (lakeIn[b] - meanIn) * dt;
            var release = lakeAnomaly[b] * (1 - Math.Exp(-dt / LakeRegulationYears)) / dt;
            lakeAnomaly[b] -= release * dt;
            var ratio = meanIn > 0 ? mean / meanIn : 0;
            var outflow = Math.Max(0, mean + release * ratio);
            total[outlet] += outflow;
            fromBase[outlet] += outflow * lakeShareBase[b];
            fromMelt[outlet] += outflow * lakeShareMelt[b];
        }

        // ---- 3. 水情分類、洪峰係數、積雪、結冰 ----
        Parallel.For(0, n, c =>
        {
            var mean = 0.0;
            var max = 0.0;
            for (var m = 0; m < seasons; m++)
            {
                mean += result.Discharge[m][c];
                max = Math.Max(max, result.Discharge[m][c]);
            }
            mean /= seasons;
            result.MeanDischarge[c] = (float)mean;
            if (mean > 0)
            {
                result.BaseflowShare[c] = (float)Math.Clamp(annualBase[c] / mean, 0, 1);
                result.MeltShare[c] = (float)Math.Clamp(annualMelt[c] / mean, 0, 1);
                result.FloodFactor[c] = (float)Math.Clamp(Math.Sqrt(max / mean), 1.0, MaxFloodFactor);
                var dryMonths = 0;
                var minShare = double.MaxValue;
                for (var m = 0; m < seasons; m++)
                {
                    var share = result.Discharge[m][c] / mean;
                    minShare = Math.Min(minShare, share);
                    if (share < DryShare) dryMonths++;
                }
                result.Regime[c] = dryMonths * 2 >= seasons ? FlowRegime.Ephemeral
                    : minShare >= PerennialMinShare ? FlowRegime.Perennial
                    : FlowRegime.Seasonal;
            }
            else
            {
                result.FloodFactor[c] = 1f;
            }

            for (var m = 0; m < seasons; m++)
            {
                var t = climate.Temperature[m][c];
                var water = state.WaterDepth[c] > 0f;
                var sea = water && lakeOf[c] < 0;
                result.Frozen[m][c] = sea ? t < SeaFreezeC : t < FreshwaterFreezeC;
                result.SnowCovered[m][c] = !water && (result.SnowWater[m][c] > snowCover || state.IceThickness[c] > GlacierIceMeters);
            }
        });
        return result;
    }

    /// <summary>每個時段都在冰點以下（積雪永遠不會融）。</summary>
    private static bool AlwaysFrozen(SeasonalClimate climate, int c)
    {
        for (var m = 0; m < climate.Seasons; m++) if (climate.Temperature[m][c] > 0f) return false;
        return true;
    }
}

/// <summary><see cref="SeasonalHydrology.Solve"/> 的結果。流量跟 <see cref="Hydrology.Discharge"/> 同單位（降水的相對單位 × 格，年率）。</summary>
public sealed class SeasonalHydrologyResult
{
    /// <summary>建立空的結果。</summary>
    /// <param name="seasons">時段數。</param>
    /// <param name="cellCount">格數。</param>
    public SeasonalHydrologyResult(int seasons, int cellCount)
    {
        Seasons = seasons;
        Discharge = Make<float>(seasons, cellCount);
        SnowWater = Make<float>(seasons, cellCount);
        Frozen = Make<bool>(seasons, cellCount);
        SnowCovered = Make<bool>(seasons, cellCount);
        MeanDischarge = new float[cellCount];
        BaseflowShare = new float[cellCount];
        MeltShare = new float[cellCount];
        FloodFactor = new float[cellCount];
        Regime = new FlowRegime[cellCount];
    }

    private static T[][] Make<T>(int seasons, int cellCount)
    {
        var arrays = new T[seasons][];
        for (var m = 0; m < seasons; m++) arrays[m] = new T[cellCount];
        return arrays;
    }

    /// <summary>時段數。</summary>
    public int Seasons { get; }

    /// <summary>[時段][格] 河的流量（海與湖裡是 0）。</summary>
    public float[][] Discharge { get; }

    /// <summary>[時段][格] 時段結束時的積雪水當量（降水的相對單位）。</summary>
    public float[][] SnowWater { get; }

    /// <summary>[時段][格] 結冰：河、湖（月均溫低於 −2°C）、海冰（低於 −4°C）。陸地沒有意義。</summary>
    public bool[][] Frozen { get; }

    /// <summary>[時段][格] 地面被雪蓋住（陸地；冰川終年都算）。</summary>
    public bool[][] SnowCovered { get; }

    /// <summary>各時段的平均流量（有河道損失，所以沙漠下游會比年平均水系的流量小）。</summary>
    public float[] MeanDischarge { get; }

    /// <summary>流量裡來自地下水基流的比例（0~1，年平均）。</summary>
    public float[] BaseflowShare { get; }

    /// <summary>流量裡來自融雪與冰川的比例（0~1，年平均）。</summary>
    public float[] MeltShare { get; }

    /// <summary>洪峰係數：√(最大時段流量 ÷ 平均)，1~3。給 <see cref="FluvialErosion"/> 用。</summary>
    public float[] FloodFactor { get; }

    /// <summary>水情分類（不是河的格子也有值：用它自己的流量算）。</summary>
    public FlowRegime[] Regime { get; }
}
