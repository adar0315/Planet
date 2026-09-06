namespace Planet.Data;

/// <summary>
/// <see cref="ITileRepository.GetTilesNearAsync"/> 的查詢框設定。
/// 原本查詢框是寫死的±1.1度，一旦前端調整「一次載入幾格」就會對不上——
/// 要的格數變多但框沒變大，就只是把同一批格子重排一次而已。
/// 改成依要求的筆數反推需要多大的框。
/// </summary>
public sealed record NearbyQueryOptions
{
    /// <summary>
    /// 每平方度大約有幾個地塊。實測校準值（見 docs/architecture.md）：
    /// ±1.1度約2,400格、±2度約7,758格，兩組都吻合約1,950~1,985格/平方度。
    /// </summary>
    public double TilesPerSquareDegree { get; init; } = 1950;

    /// <summary>查詢框的安全係數：實際涵蓋面積會比理論需求再大一些，避免邊緣剛好不足。</summary>
    public double SafetyFactor { get; init; } = 1.3;

    /// <summary>查詢框半徑的下限（度）。</summary>
    public double MinDeltaDegrees { get; init; } = 0.5;

    /// <summary>
    /// 查詢框半徑的上限（度）。框開太大時候選集合會爆炸、查詢時間非線性上升
    /// （實測±10度約184,000格、3.8秒），必須設上限。
    /// 5萬格需要約5.8度，這裡留到7度給一點餘裕。
    /// </summary>
    public double MaxDeltaDegrees { get; init; } = 7.0;

    /// <summary>
    /// 單次查詢允許的最大筆數，避免異常請求造成分鐘等級的查詢。
    /// 前端目前一次要5萬格（見 PlanetScene 的 NEARBY_TILE_COUNT），這裡留到6萬。
    /// </summary>
    public int MaxCount { get; init; } = 60_000;

    /// <summary>依要求的筆數反推查詢框半徑（度）。</summary>
    /// <param name="count">要求的地塊筆數。</param>
    public double DeltaDegreesFor(int count)
    {
        var delta = Math.Sqrt(count * SafetyFactor / TilesPerSquareDegree);
        return Math.Clamp(delta, MinDeltaDegrees, MaxDeltaDegrees);
    }
}
