namespace Planet.Simulation;

/// <summary>
/// 風蝕與黃土（水文地貌規劃 H5，2026-10-24；只在板塊座標系模式用）。
///
/// 乾燥、沒有植被的地方，風把鬆散沉積物吹走：**細的粉砂**飄得遠，順著風一路往下風處，
/// 落在比較濕、有植被攔得住的地方——那就是黃土（黃土高原的粉砂來自西北的戈壁與沙漠）。
/// **粗的砂**跳不遠，留在原地堆成沙海（放大時畫成沙丘，不在這裡搬）。
///
/// 物質守恆：吹走多少就落下多少（陸地是黃土、飄到海上的落進海底的沉積物）。
/// 只吹鬆散沉積物、不吹基岩：風蝕基岩（雅丹）的速率比流水慢好幾個量級，在 15 公里的格子上看不出來。
/// </summary>
public sealed class AeolianProcesses
{
    /// <summary>
    /// 完全乾燥、風速最大時，一年吹走多少鬆散沉積物（公尺／百萬年）。
    /// 現代沙漠的風蝕約每年 0.01~0.1 公釐，取 50 公尺／百萬年（0.05 公釐／年）。
    /// </summary>
    private const double DeflationMetersPerMyr = 50.0;

    /// <summary>吹走的物質裡細粉砂（會飄到下風處成為黃土）的比例；其餘是砂，留在原地。</summary>
    private const double FineFraction = 0.4;

    /// <summary>降水低於全球平均的這個比例才開始有風蝕（越乾越強）。</summary>
    private const double AridPrecipitationShare = 0.5;

    /// <summary>塵土落下的地方：降水至少是全球平均的這個比例（有植被攔得住）。</summary>
    private const double TrapPrecipitationShare = 0.6;

    /// <summary>塵土最遠飄多遠（公尺）。黃土高原離粉砂的源頭約一兩千公里。</summary>
    private const double MaxDustTransportMeters = 2_000_000.0;

    /// <summary>
    /// 塵土在乾燥的地方每飄這麼遠（公尺），有 1−1/e 落下來。不設這個、只在「攔得住的地方」落下的話，
    /// 風停的地方（副熱帶高壓、赤道無風帶）找不到落點，整條路的塵土全倒在最後那一格——
    /// 2026-10-24 第一次在 n=150 跑：41 Myr 堆出一座 21 公里高的塵土柱。
    /// </summary>
    private const double DrySettlingMeters = 500_000.0;

    /// <summary>乾燥的格子風速至少這麼大（帶狀風的向量長度）塵土才會落：落下來之後吹得走。</summary>
    private const float MinRedeflationWind = 0.2f;

    /// <summary>移動平均的時間常數（百萬年）。</summary>
    private const double RateAveragingMyr = 1.0;

    private readonly SimulationGrid _grid;
    private readonly int _maxHops;
    private readonly double _drySettleFraction;
    private readonly float[] _deflation;
    private readonly float[] _dustArrival;

    /// <summary>最近一步吹走的鬆散沉積物（立方公里）。</summary>
    public double LastDeflatedKm3 { get; private set; }

    /// <summary>最近一步落回地表的（立方公里：砂留在原地＋粉砂落在下風處），應該等於 <see cref="LastDeflatedKm3"/>。</summary>
    public double LastDustDepositedKm3 { get; private set; }

    /// <summary>建立風蝕模型。</summary>
    /// <param name="grid">網格。</param>
    public AeolianProcesses(SimulationGrid grid)
    {
        _grid = grid;
        _maxHops = Math.Max(1, (int)Math.Round(MaxDustTransportMeters / grid.AverageCellSpacingMeters));
        _drySettleFraction = 1 - Math.Exp(-grid.AverageCellSpacingMeters / DrySettlingMeters);
        _deflation = new float[grid.CellCount];
        _dustArrival = new float[grid.CellCount];
    }

    /// <summary>推進一步：用年平均的降水與風。排在流水侵蝕之後、均衡注水之前（跟外營力同一個慣例）。</summary>
    /// <param name="state">模擬狀態：沉積物、風蝕與黃土的速率會被更新。</param>
    /// <param name="climate">氣候（風速、下風方向）。</param>
    /// <param name="deltaYears">這一步代表多少年。</param>
    public void Step(SimulationState state, Climate climate, double deltaYears)
    {
        var deltaMyr = deltaYears / 1_000_000.0;
        var n = state.CellCount;
        var meanPrecipitation = state.Precipitation.Average();
        if (meanPrecipitation <= 0) return;
        Array.Clear(_deflation);
        Array.Clear(_dustArrival);

        // 1. 吹蝕（每格只改自己，可以平行）
        Parallel.For(0, n, c =>
        {
            if (state.WaterDepth[c] > 0f || state.IceThickness[c] > 1f) return;
            var sediment = state.SedimentThickness[c];
            if (sediment <= 0f) return;
            var aridity = Math.Clamp(1 - state.Precipitation[c] / (AridPrecipitationShare * meanPrecipitation), 0, 1);
            if (aridity <= 0) return;
            var wind = Math.Min(1f, climate.WindSpeedAt(c));
            var take = (float)Math.Min(sediment, DeflationMetersPerMyr * aridity * wind * deltaMyr);
            if (take <= 0f) return;
            state.SedimentThickness[c] = sediment - take;
            _deflation[c] = take;
        });

        // 2. 細粉砂順著風飄到第一個攔得住的地方；砂留在原地（放回去）
        double deflated = 0, deposited = 0;
        var trap = TrapPrecipitationShare * meanPrecipitation;
        for (var c = 0; c < n; c++)
        {
            var take = _deflation[c];
            if (take <= 0f) continue;
            deflated += take;
            var fine = take * FineFraction;
            state.SedimentThickness[c] += (float)(take - fine); // 砂：留在原地成為沙海
            deposited += take - fine;

            // 順著風一格一格飄：乾燥的地方每格落下一點，到了攔得住的地方（濕潤、有植被，或海上）全部落下
            var remaining = fine;
            var cur = c;
            for (var hop = 0; hop < _maxHops && remaining > 0; hop++)
            {
                var next = climate.DownwindNeighbor(cur);
                if (next < 0) break; // 風停了：剩下的平均鋪回走過的路上（下面）
                cur = next;
                var trapped = state.WaterDepth[cur] > 0f || state.Precipitation[cur] >= trap;
                // 乾燥的地方只在有風的格子落：之後還吹得走。無風的乾燥格子（副熱帶高壓）收了塵土永遠吹不走，
                // 再加上沒有雨、流水也搬不動，2026-10-24 在那裡堆出 15 公里厚的沉積
                var settle = trapped ? remaining : climate.WindSpeedAt(cur) >= MinRedeflationWind ? remaining * _drySettleFraction : 0;
                state.SedimentThickness[cur] += (float)settle;
                // 只有被植被攔下來的才算黃土；落在乾燥地方的塵土之後還會再被吹走
                if (trapped) _dustArrival[cur] += (float)settle;
                remaining -= settle;
                deposited += settle;
            }
            if (remaining > 0)
            {
                // 風停了還沒落下的：等於沒被吹走，放回原地。不能倒在風停的那一格——
                // 那一格每一步都收到整條路的塵土、自己又沒有風可以吹走，會堆成一根柱子（2026-10-24 量到 21 公里）
                state.SedimentThickness[c] += (float)remaining;
                deposited += remaining;
            }
        }

        var alpha = (float)Math.Min(1.0, deltaMyr / RateAveragingMyr);
        Parallel.For(0, n, c =>
        {
            state.DeflationRate[c] += (float)(_deflation[c] / deltaMyr - state.DeflationRate[c]) * alpha;
            state.LoessRate[c] += (float)(_dustArrival[c] / deltaMyr - state.LoessRate[c]) * alpha;
        });

        var cellKm2 = _grid.AverageCellAreaSquareMeters / 1e6;
        LastDeflatedKm3 = deflated / 1000.0 * cellKm2;
        LastDustDepositedKm3 = deposited / 1000.0 * cellKm2;
    }
}
