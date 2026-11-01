namespace AuroraCore.Download;

/// <summary>
/// Aggregate slow-transfer watchdog (pure logic, unit-testable; the clock is
/// injected). A download route that is SLOW-BUT-ALIVE used to crawl forever
/// (e.g. 4.9 KB/s from cdn.modrinth.com) because the failover policy only
/// triggered on full failures — the progress bar looked frozen. This watcher
/// samples the aggregate speed and reports a stall when the average over the
/// rolling window stays below the threshold for the given number of
/// consecutive windows, so the caller can abort the route and fail over.
///
/// Not per-file: the engine downloads with bounded concurrency, so only the
/// AGGREGATE speed is meaningful (a single file of sixteen parallel streams
/// is legitimately slow while the batch as a whole is not).
///
/// Stall semantics (round-56, deliberate): each window is evaluated
/// INDEPENDENTLY — a window whose average is at/above the threshold RESETS
/// the consecutive-slow counter to zero. So one good 5-second window in the
/// middle means no abort, no matter how bad the neighbours were. That is the
/// conservative choice: an oscillating route (slow/fast/slow) never gets
/// killed because its user is still getting decent average throughput; a
/// permanently crawling route aborts after exactly `windowsToAbort` slow
/// windows. Covered by the round-52 unit test (recovery resets the counter).
/// </summary>
public sealed class SlowTransferWatcher
{
    private readonly double _minBytesPerSec;
    private readonly TimeSpan _window;
    private readonly int _windowsToAbort;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Queue<(DateTimeOffset At, double Speed)> _samples = new();
    private DateTimeOffset _nextEvaluation;
    private int _slowWindows;

    public SlowTransferWatcher(
        double minBytesPerSec = 16 * 1024,
        TimeSpan? window = null,
        int windowsToAbort = 4,
        Func<DateTimeOffset>? clock = null)
    {
        _minBytesPerSec = minBytesPerSec;
        _window = window ?? TimeSpan.FromSeconds(5);
        _windowsToAbort = windowsToAbort;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _nextEvaluation = _clock();
    }

    /// <summary>Average speed (bytes/sec) over the last evaluated window —
    /// for diagnostic logging at abort time ("线路 X 被判定为爬行，聚合速度
    /// Y KB/s"). Valid after OnSample; 0 before the first evaluation.</summary>
    public double LastWindowAverage { get; private set; }

    /// <summary>Feeds one aggregate-speed sample (bytes/sec). Returns true
    /// exactly once when the transfer should be aborted (sustained crawl
    /// detected); afterwards the watcher keeps evaluating for the next
    /// decision (e.g. the mirror route may use the same instance).</summary>
    public bool OnSample(double bytesPerSec)
    {
        var now = _clock();
        _samples.Enqueue((now, bytesPerSec));
        while (_samples.Count > 0 && now - _samples.Peek().At > _window)
            _samples.Dequeue();
        if (now < _nextEvaluation) return false;
        _nextEvaluation = now + _window;
        // No samples in a whole window means the progress pipe went silent
        // (bytes not moving) — count that as maximal slowness, not "healthy".
        var avg = _samples.Count == 0 ? 0 : _samples.Average(s => s.Speed);
        LastWindowAverage = avg;
        _slowWindows = avg < _minBytesPerSec ? _slowWindows + 1 : 0;
        return _slowWindows >= _windowsToAbort;
    }
}
