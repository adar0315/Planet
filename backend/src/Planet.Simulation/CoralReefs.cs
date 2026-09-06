namespace Planet.Simulation;

/// <summary>
/// 珊瑚礁：溫暖淺海裡的造礁生物把碳酸鈣一層層堆上來，堆到水面就成為珊瑚島（環礁）。
///
/// ## 三個生長條件都是真的限制，不是為了好看
///
/// - **夠暖**：造礁珊瑚與體內共生的蟲黃藻在低於約18°C 就活不了，
///   所以地球上的珊瑚礁全部集中在南北緯30度之間（大堡礁、加勒比海、紅海）。
/// - **夠淺**：共生藻要行光合作用，超過透光帶（約60公尺）就長不動。
/// - **有空間**：珊瑚不能長出水面（會曬死），所以礁體會**長到海平面就停住**，
///   形成一片非常平坦的礁坪——這是珊瑚礁地形最鮮明的特徵。
///
/// ## 環礁會自己浮現，不需要特別寫規則
///
/// 達爾文的沉降理論：火山島先冒出海面，周圍長出裾礁；火山島隨著海洋地殼冷卻而**下沉**，
/// 珊瑚往上長來追海平面，於是礁與島之間出現潟湖（堡礁）；島完全沉沒後只剩一圈珊瑚，就是環礁。
///
/// 這個模型裡三個要素都已經存在——熱柱造出火山島（<see cref="MantlePlumes"/>）、
/// 海洋地殼隨年齡變深（<see cref="Isostasy"/>）、珊瑚往上追海平面（這裡）——
/// 所以環礁是**自己長出來的**，沒有一行程式碼在畫圓圈。
/// </summary>
public sealed class CoralReefs
{
    /// <summary>造礁珊瑚能存活的最低水溫（攝氏）。這是珊瑚礁只出現在低緯度的原因。</summary>
    private const float MinTemperatureC = 18f;

    /// <summary>珊瑚能生長的最大水深（公尺）。超過透光帶，共生藻的光合作用就不夠支撐造礁。</summary>
    private const float MaxGrowthDepthM = 60f;

    /// <summary>
    /// 最大造礁速率（公尺／千年）。
    /// 實際的珊瑚礁向上生長約每年1~10公釐，也就是每千年1~10公尺，取4是中間值。
    /// **這個速率快過海洋地殼的沉降**（那是每千年零點幾公尺），所以珊瑚追得上海平面——
    /// 追不上的話環礁根本不會形成，會直接變成沉在水下的死礁。
    /// </summary>
    private const float MaxGrowthRateMPerKiloyear = 4f;

    /// <summary>
    /// 礁體可以高出海平面多少公尺。珊瑚本身長不出水面，但風暴會把礁石碎屑堆到潮線以上，
    /// 這就是珊瑚島普遍只有幾公尺高、颱風一來就淹掉的原因。
    /// </summary>
    private const float IslandFreeboardM = 3f;

    /// <summary>
    /// 推進一步的造礁作用。
    /// </summary>
    /// <param name="state">模擬狀態，<see cref="SimulationState.OrganicThickness"/> 會增加。</param>
    /// <param name="deltaYears">這一步代表多少年。</param>
    public void Step(SimulationState state, double deltaYears)
    {
        var maxGrowth = (float)(MaxGrowthRateMPerKiloyear * deltaYears / 1000.0);

        Parallel.For(0, state.CellCount, cell =>
        {
            var depth = state.WaterDepth[cell];
            if (depth <= 0f || depth > MaxGrowthDepthM) return;
            if (state.Temperature[cell] < MinTemperatureC) return;

            // 可以長多高：長到海平面之上一點點（風暴堆積的碎屑）就沒有空間了
            var accommodation = depth + IslandFreeboardM;
            if (accommodation <= 0f) return;

            state.OrganicThickness[cell] += MathF.Min(maxGrowth, accommodation);
        });
    }
}
