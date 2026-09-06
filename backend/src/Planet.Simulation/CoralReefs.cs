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
///
/// ## ⚠️ 礁體掛在**標記**上，不是掛在格子上（2026-09-07 踩過）
///
/// <c>OrganicThickness</c> 一度是純粹的格子欄位，只被寫入、沒有任何東西移除它，
/// 結果它變成「這一格歷史上每一次剛好淺過」的**化石紀錄**：淺海長礁 → 底下的地殼隨板塊
/// 漂走、隱沒 → 變回深海（停長）→ 下一塊淺地殼漂過來 → 再疊一層。因為海岸線一直在移動，
/// 每一格遲早都會輪到，礁體於是**無上限地累積**——實測 300 Myr 後最厚 10,671 公尺、
/// 而且還在漲，中位數從 110 一路爬到 361。
///
/// 最能說明問題的是位置：那時真正還留在透光帶（水深 ≤60 公尺）裡的礁體只有 **12 格**，
/// 另外 1,386 格躺在深海、1,255 格已經在水面之上，平均都堆了 500 多公尺。
/// 統計分布看起來完全正常，只有「礁體所在的水深」這個交叉檢查會露餡。
///
/// 這正是 CLAUDE.md 那條規則的另一個實例：**地殼的性質掛在標記上**。礁灰岩本來就是
/// 地殼柱的一部分，所以現在它跟厚度、年齡、密度一樣存在 <c>PlateTectonics._markerOrganic</c>，
/// 隨板塊漂移、隨隱沒消滅、在大陸對撞時被抬上山。
///
/// 光是這樣還不夠——被抬出水面的礁體還是永遠不會消失，所以另外加了下面的溶蝕。
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
    /// 礁灰岩露出水面後的溶蝕速率係數（公尺／百萬年，每 1 公釐／年的降水）。
    ///
    /// 碳酸鈣會被溶進帶著二氧化碳的雨水裡，所以石灰岩的剝蝕**是化學溶解、不是機械侵蝕**，
    /// 速率比矽酸鹽岩快得多，而且直接受降水量控制——這就是為什麼喀斯特地形
    /// （桂林、下龍灣、克羅埃西亞）全部出現在多雨的石灰岩區。
    ///
    /// 校準：熱帶多雨區（約2,000公釐／年）實測的灰岩溶蝕約每千年30~100公釐，
    /// 即每百萬年30~100公尺。取0.03：2,000 公釐／年給出 60 公尺／百萬年，落在區間中央。
    ///
    /// 這是抬出水面的礁體**唯一**的去路。沒有它的話礁體只進不出，
    /// 只是從「每一格都會累積」變成「被抬起來的那些格會累積」，一樣是沒有上限的。
    /// </summary>
    private const float DissolutionMetersPerMyrPerMillimetreOfRain = 0.03f;

    /// <summary>
    /// 推進一步的造礁作用。
    /// </summary>
    /// <param name="state">模擬狀態，<see cref="SimulationState.OrganicThickness"/> 會增加。</param>
    /// <param name="deltaYears">這一步代表多少年。</param>
    public void Step(SimulationState state, double deltaYears)
    {
        var maxGrowth = (float)(MaxGrowthRateMPerKiloyear * deltaYears / 1000.0);
        var dissolutionPerMillimetre =
            (float)(DissolutionMetersPerMyrPerMillimetreOfRain * deltaYears / 1_000_000.0);

        Parallel.For(0, state.CellCount, cell =>
        {
            var depth = state.WaterDepth[cell];

            // 露出水面：珊瑚死了，剩下的灰岩開始被雨水溶蝕
            if (depth <= 0f)
            {
                var reef = state.OrganicThickness[cell];
                if (reef <= 0f) return;
                state.OrganicThickness[cell] =
                    MathF.Max(0f, reef - state.Precipitation[cell] * dissolutionPerMillimetre);
                return;
            }

            if (depth > MaxGrowthDepthM) return;
            if (state.Temperature[cell] < MinTemperatureC) return;

            // 可以長多高：長到海平面之上一點點（風暴堆積的碎屑）就沒有空間了
            var accommodation = depth + IslandFreeboardM;
            if (accommodation <= 0f) return;

            state.OrganicThickness[cell] += MathF.Min(maxGrowth, accommodation);
        });
    }
}
