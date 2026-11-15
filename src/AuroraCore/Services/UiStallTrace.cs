using System.Diagnostics;

namespace AuroraCore.Services;

/// <summary>Round-70: who is blocking the UI thread?
///
/// Round-69 already proved the page flip is fast (0.3–8 ms) while the measured
/// "wait for the UI thread" was 48–218 ms — i.e. some OTHER work owns the
/// dispatcher. Two very different causes look identical from the outside:
///   (a) our own code doing CPU work on the UI thread, and
///   (b) a garbage collection pause, which blocks every thread including the UI.
/// They need opposite fixes (move work off-thread vs. allocate less), so this
/// trace attributes each stall using GC.GetTotalPauseDuration(): the watchdog
/// samples on a DispatcherTimer and compares the wall-clock gap with the GC
/// pause accumulated inside that same gap.
///
/// Semantics that matter: a tick is EXPECTED to arrive one interval later, so
/// only the EXCESS is a stall. Round-70's first run reported "停顿约 1ms" on
/// every tick because it compared the raw gap; the threshold now applies to the
/// excess.</summary>
public sealed class UiStallTrace
{
    /// <summary>Watchdog cadence. A tick arriving much later than this is a
    /// stall — the timer only fires when the dispatcher is free.</summary>
    public const int DefaultIntervalMs = 250;

    /// <summary>Excess over the interval that counts as a stall. 150 ms ≈ 9
    /// dropped frames at 60 Hz: clearly felt, and far above timer jitter.</summary>
    public const double DefaultStallThresholdMs = 150;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastSampleMs;
    private TimeSpan _lastGc;

    public UiStallTrace()
    {
        _lastGc = SafeGcPause();
    }

    /// <summary>Call on every watchdog tick. Returns a sample only when the
    /// dispatcher was blocked for longer than expected + threshold.</summary>
    public StallSample? Sample(double thresholdMs = DefaultStallThresholdMs,
                               double expectedIntervalMs = DefaultIntervalMs)
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        var elapsed = now - _lastSampleMs;
        var gcNow = SafeGcPause();
        var gcDelta = (gcNow - _lastGc).TotalMilliseconds;
        _lastSampleMs = now;
        _lastGc = gcNow;

        var stall = elapsed - expectedIntervalMs;
        if (stall < thresholdMs) return null;

        if (gcDelta < 0) gcDelta = 0;
        if (gcDelta > stall) gcDelta = stall;
        return new StallSample(stall, gcDelta);
    }

    public void Reset()
    {
        _lastSampleMs = _clock.Elapsed.TotalMilliseconds;
        _lastGc = SafeGcPause();
    }

    private static TimeSpan SafeGcPause()
    {
        try { return GC.GetTotalPauseDuration(); }
        catch { return TimeSpan.Zero; }
    }

    /// <summary>Plain-language attribution for the log.</summary>
    public static string Describe(StallSample sample)
    {
        var where = sample.IsGcDominated
            ? $"其中 GC 暂停 {sample.GcPauseMs:F0}ms — 垃圾回收，不是页面或网络在算"
            : sample.GcPauseMs >= 30
                ? $"其中 GC 暂停 {sample.GcPauseMs:F0}ms，其余 {sample.OtherMs:F0}ms 为界面线程上的工作"
                : $"界面线程被占用，GC 仅 {sample.GcPauseMs:F0}ms";
        return $"界面停顿约 {sample.StallMs:F0}ms（{where}）";
    }
}

/// <summary>One watchdog observation. <see cref="StallMs"/> is the excess over
/// the expected interval (i.e. what the user actually feels);
/// <see cref="GcPauseMs"/> is the GC pause inside it.</summary>
public readonly record struct StallSample(double StallMs, double GcPauseMs)
{
    public double OtherMs => Math.Max(0, StallMs - GcPauseMs);

    public bool IsGcDominated => GcPauseMs > 0 && GcPauseMs * 2 >= StallMs;
}
