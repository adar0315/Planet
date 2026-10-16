using System.Diagnostics;

namespace Planet.Simulation;

/// <summary>
/// 每步耗時分項（v2 規劃 P0：即時觀看有效能預算）。
///
/// 每個新機制上線都要看它吃掉多少時間——超過預算就降頻或分攤，不能悄悄拖慢。
/// v1 的經驗是「慢」通常要到很久以後才被注意到，那時已經分不清是哪一項造成的。
///
/// **累積平均，不是只看最後一步**：氣候每 40 步才解一次、海洋出口每 4 步檢查一次，
/// 只看最後一步的話，那些項目大部分時候是 0、偶爾是一個尖峰，看不出真實負擔。
/// 這裡累積到 <see cref="Reset"/> 為止，回報的是「平均每步」。
/// </summary>
public sealed class StepTimings
{
    private readonly List<string> _order = [];
    private readonly Dictionary<string, long> _ticks = [];
    private long _last;

    /// <summary>從上次 <see cref="Reset"/> 以來累積了幾步。</summary>
    public int StepsMeasured { get; private set; }

    /// <summary>每一項平均每步花幾毫秒，依執行順序排列。最後一項 <c>合計</c> 是全部加總。</summary>
    public IReadOnlyList<StepTimingEntry> AverageMillisecondsPerStep()
    {
        if (StepsMeasured == 0) return [];
        var perTick = 1000.0 / Stopwatch.Frequency / StepsMeasured;
        var entries = _order.Select(s => new StepTimingEntry(s, _ticks[s] * perTick)).ToList();
        entries.Add(new StepTimingEntry("合計", entries.Sum(e => e.AverageMilliseconds)));
        return entries;
    }

    /// <summary>清掉累積的數字，重新開始量。</summary>
    public void Reset()
    {
        _ticks.Clear();
        _order.Clear();
        StepsMeasured = 0;
    }

    internal void BeginStep() => _last = Stopwatch.GetTimestamp();

    /// <summary>把「上一個標記到現在」記在這一項名下。</summary>
    internal void Mark(string section)
    {
        var now = Stopwatch.GetTimestamp();
        if (_ticks.TryGetValue(section, out var total)) _ticks[section] = total + (now - _last);
        else
        {
            _ticks[section] = now - _last;
            _order.Add(section);
        }
        _last = now;
    }

    internal void EndStep() => StepsMeasured++;
}

/// <summary>一項的平均耗時。</summary>
/// <param name="Section">哪一項。</param>
/// <param name="AverageMilliseconds">平均每步幾毫秒。</param>
public sealed record StepTimingEntry(string Section, double AverageMilliseconds);
