using Planet.Simulation;
using Xunit;

namespace Planet.Simulation.Tests;

/// <summary>
/// 岩石圈淨浮力：決定什麼會隱沒的量。
///
/// **板塊不等於地殼**——板塊是岩石圈（地殼 ＋ 下方已冷卻到具剛性的地函），
/// 而海洋地殼（2,900）其實比地函（3,300）**輕**，光靠地殼永遠不會隱沒。
/// 會沉下去的是整個岩石圈：冷卻後增厚的地函部分密度超過軟流圈，把上面較輕的地殼一起拖下去。
///
/// 這裡守的是那個**由物理自己算出來、沒有調過任何參數**的結論：
/// 年輕的海洋岩石圈是浮的、老的才會沉，符號翻轉發生在約18百萬年。
/// </summary>
public class LithosphereTests
{
    /// <summary>年輕的海洋岩石圈浮得住——這是它抗拒隱沒的原因。</summary>
    [Fact]
    public void YoungOceanicLithosphereIsBuoyant()
    {
        var buoyancy = Isostasy.LithosphereBuoyancyMeters(
            CrustType.Oceanic, Isostasy.OceanicCrustThicknessKm, Isostasy.OceanicCrustDensity, 0f);

        Assert.True(buoyancy > 0,
            $"剛生成的海洋岩石圈淨浮力是 {buoyancy:F0} 公尺——應該是正的（地殼比地函輕）");
    }

    /// <summary>老的海洋岩石圈會沉——冷卻增厚的地函部分把它拖下去。</summary>
    [Fact]
    public void OldOceanicLithosphereSinks()
    {
        var buoyancy = Isostasy.LithosphereBuoyancyMeters(
            CrustType.Oceanic, Isostasy.OceanicCrustThicknessKm, Isostasy.OceanicCrustDensity, 100f);

        Assert.True(buoyancy < 0,
            $"1億年的海洋岩石圈淨浮力是 {buoyancy:F0} 公尺——應該是負的，否則什麼都不會隱沒");
    }

    /// <summary>
    /// 符號翻轉發生在10~30百萬年之間（地球的觀測範圍）。
    ///
    /// **這個數字不是調出來的**：地殼的正浮力 7 × (3,300 − 2,900) 與岩石圈地函的負浮力
    /// L × 66 相等時 L ≈ 42 公里，對應年齡 (42/10)² ≈ 18 百萬年。
    /// 三個常數（厚度成長率、密度超出量、地殼厚度）各自有獨立的物理來源，
    /// 這個時刻是它們的必然結果。**如果這個測試失敗，代表有人為了別的目的動了那三個常數之一。**
    /// </summary>
    [Fact]
    public void BuoyancyCrossoverIsWithinEarthRange()
    {
        var crossover = -1f;
        for (var age = 0f; age <= 60f; age += 0.1f)
        {
            var buoyancy = Isostasy.LithosphereBuoyancyMeters(
                CrustType.Oceanic, Isostasy.OceanicCrustThicknessKm, Isostasy.OceanicCrustDensity, age);
            if (buoyancy > 0) continue;
            crossover = age;
            break;
        }

        Assert.InRange(crossover, 10f, 30f);
    }

    /// <summary>
    /// 大陸的浮力遠大於任何年齡的海洋岩石圈——所以大陸碰撞是縫合成高山，不是一邊沉下去。
    ///
    /// 大陸的岩石圈地函在化學上是虧損的（早期熔融抽走較重的鐵鎂成分），
    /// 所以即使又冷又厚也不提供負浮力。
    /// </summary>
    [Fact]
    public void ContinentalLithosphereCannotSubduct()
    {
        var continental = Isostasy.LithosphereBuoyancyMeters(
            CrustType.Continental, Isostasy.ContinentalCrustThicknessKm, Isostasy.ContinentalCrustDensity, 2000f);

        var oldestOceanic = Isostasy.LithosphereBuoyancyMeters(
            CrustType.Oceanic, Isostasy.OceanicCrustThicknessKm, Isostasy.OceanicCrustDensity, 4000f);

        Assert.True(continental > 0, $"20億年的大陸淨浮力是 {continental:F0} 公尺——大陸不該沉");
        Assert.True(continental > oldestOceanic * -1,
            "大陸的浮力應該壓過最老的海洋岩石圈，碰撞時才會是海洋那一側隱沒");
    }

    /// <summary>
    /// 岩石圈厚度會停在上限，不會隨年齡無限成長。
    /// 冷卻到某個深度之後底下的地函對流會把熱補回來（板塊模型）。
    /// </summary>
    [Fact]
    public void LithosphereThicknessSaturates()
    {
        var atFourBillion = Isostasy.LithosphereBuoyancyMeters(
            CrustType.Oceanic, Isostasy.OceanicCrustThicknessKm, Isostasy.OceanicCrustDensity, 4000f);
        var atOneBillion = Isostasy.LithosphereBuoyancyMeters(
            CrustType.Oceanic, Isostasy.OceanicCrustThicknessKm, Isostasy.OceanicCrustDensity, 1000f);

        Assert.Equal(atOneBillion, atFourBillion, 3);
    }
}
