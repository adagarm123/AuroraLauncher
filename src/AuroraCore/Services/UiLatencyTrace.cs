namespace AuroraCore.Services;

/// <summary>Round-68: measurable UI latency instead of guesswork.
///
/// Rounds 63–67 fixed navigation twice, and the second fix (round-66) made
/// things WORSE because it was a guess. The layout benchmark added this round
/// (`--nav-bench`) showed a warm page flip costs 0.3 ms — so any remaining
/// "卡顿" is either a one-off cold build (5–60 ms) or comes from data the
/// benchmark cannot reproduce. This tiny trace records what the user actually
/// experienced, with the page name attached, so the next report is evidence.
///
/// Deliberately dependency-free (no WPF, no I/O): the caller decides where the
/// numbers go.</summary>
public sealed class UiLatencyTrace
{
    /// <summary>A switch taking longer than this is logged. 50 ms ≈ 3 dropped
    /// frames at 60 Hz — the point where a click starts to feel sticky rather
    /// than instant.</summary>
    public const double SlowThresholdMs = 50;

    private readonly Dictionary<string, double> _last = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _count = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _slow = new(StringComparer.Ordinal);

    /// <summary>Records one sample. Returns true when it exceeded the
    /// threshold (i.e. the caller should write it to the log).</summary>
    public bool Record(string stage, double ms, double threshold = SlowThresholdMs)
    {
        if (string.IsNullOrWhiteSpace(stage)) return false;
        if (double.IsNaN(ms) || ms < 0) return false;

        _last[stage] = ms;
        _count[stage] = Count(stage) + 1;

        var slow = ms > threshold;
        if (slow) _slow[stage] = SlowCount(stage) + 1;
        return slow;
    }

    public double LastMs(string stage) => _last.TryGetValue(stage, out var v) ? v : -1;

    public int Count(string stage) => _count.TryGetValue(stage, out var v) ? v : 0;

    public int SlowCount(string stage) => _slow.TryGetValue(stage, out var v) ? v : 0;

    /// <summary>Total slow samples across every stage — shown in diagnostics so a
    /// user can answer "is switching actually slow?" with a number.</summary>
    public int TotalSlow => _slow.Values.Sum();

    public int TotalSamples => _count.Values.Sum();

    /// <summary>The slowest stage seen so far, e.g. "nav:Mods".</summary>
    public string? SlowestStage
    {
        get
        {
            string? best = null;
            var bestMs = -1.0;
            foreach (var (stage, ms) in _last)
            {
                if (ms <= bestMs) continue;
                bestMs = ms;
                best = stage;
            }
            return best;
        }
    }

    public void Reset()
    {
        _last.Clear();
        _count.Clear();
        _slow.Clear();
    }

    /// <summary>One-line human summary, e.g.
    /// "切页 9 次，超时 1 次，最慢 nav:Mods 118ms".</summary>
    public string Summary(string stagePrefix = "nav:")
    {
        var samples = 0;
        var slow = 0;
        var worst = -1.0;
        var worstStage = "";
        foreach (var (stage, count) in _count)
        {
            if (!stage.StartsWith(stagePrefix, StringComparison.Ordinal)) continue;
            samples += count;
            slow += SlowCount(stage);
            var ms = LastMs(stage);
            if (ms > worst)
            {
                worst = ms;
                worstStage = stage;
            }
        }
        if (samples == 0) return "暂无切页计时数据";
        return worst <= 0
            ? $"切页 {samples} 次，无慢速记录"
            : $"切页 {samples} 次，超时 {slow} 次，最慢 {worstStage} {worst:F0}ms";
    }
}
