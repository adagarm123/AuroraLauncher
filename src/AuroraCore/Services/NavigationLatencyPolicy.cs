namespace AuroraCore.Services;

/// <summary>Round-69: navigation timing that tells the truth.
///
/// Round-68 logged a single number: "点击 → dispatcher 空闲". The user's own log
/// then showed `切换页面 Download 用时 62ms（已缓存）` on a page whose flip had just
/// been measured at 0.3 ms. The page was NOT slow — the UI thread was busy with
/// other work (that run was mid-manifest-fetch and mid-four-route loader race),
/// so our Background-priority callback simply waited in the queue. Blaming the
/// page would send the next optimization in the wrong direction (round-66
/// already proved how expensive a wrong guess is).
///
/// So the flip is measured in three phases:
///   flip  — synchronous Visibility changes inside the click handler;
///   paint — flip → first Render-priority turn (measure/arrange done);
///   queue — paint → first Background turn (everything else the UI thread had).
/// Page cost = flip + paint. Thread contention = queue.</summary>
public enum NavigationSlowKind
{
    /// <summary>Under the threshold — nothing to report.</summary>
    None,

    /// <summary>The page itself took too long to lay out (flip + paint).</summary>
    PageRender,

    /// <summary>The page was fast; the UI thread was busy with other work.</summary>
    UiThreadBusy
}

/// <summary>One measured navigation, in milliseconds.</summary>
public readonly record struct NavigationTiming(double FlipMs, double PaintMs, double QueueMs)
{
    public double PageMs => FlipMs + PaintMs;

    public double TotalMs => FlipMs + PaintMs + QueueMs;
}

public static class NavigationLatencyPolicy
{
    /// <summary>50 ms ≈ 3 dropped frames at 60 Hz: the point where a click stops
    /// feeling instant.</summary>
    public const double SlowThresholdMs = 50;

    public static NavigationTiming FromPhases(double flipMs, double toPaintMs, double toIdleMs)
    {
        var flip = NonNegative(flipMs);
        var paint = NonNegative(toPaintMs);
        // Clamp the raw marks BEFORE deriving the queue, so clock skew cannot
        // invent "the UI thread was busy for N ms" out of negative deltas.
        var queue = NonNegative(NonNegative(toIdleMs) - paint);
        return new NavigationTiming(flip, paint, queue);
    }

    public static NavigationSlowKind Classify(NavigationTiming timing, double threshold = SlowThresholdMs)
    {
        if (timing.TotalMs <= threshold) return NavigationSlowKind.None;
        return timing.PageMs > threshold ? NavigationSlowKind.PageRender : NavigationSlowKind.UiThreadBusy;
    }

    /// <summary>One log line. Always states the page's own cost, so a "busy
    /// thread" report still carries the number that matters for page tuning.</summary>
    public static string Describe(string page, NavigationTiming timing, bool built, bool initial)
    {
        var pageText = $"页面 {(built ? "首次构建" : "已缓存")} · 本页 {timing.PageMs:F0}ms" +
                       $"（切换 {timing.FlipMs:F1} + 布局 {timing.PaintMs:F0}）";
        var kind = Classify(timing);

        if (initial)
            return $"首次进入 {page} 用时 {timing.TotalMs:F0}ms，{pageText}";

        return kind switch
        {
            NavigationSlowKind.PageRender =>
                $"切换页面 {page} 用时 {timing.TotalMs:F0}ms，{pageText} — 页面排版本身偏慢",
            NavigationSlowKind.UiThreadBusy =>
                $"切换页面 {page} 用时 {timing.TotalMs:F0}ms，{pageText} — 等待 UI 线程 {timing.QueueMs:F0}ms（其他任务占用，非本页慢）",
            _ =>
                $"切换页面 {page} 用时 {timing.TotalMs:F1}ms，{pageText}"
        };
    }

    private static double NonNegative(double v) => double.IsNaN(v) || v < 0 ? 0 : v;
}
