namespace AuroraCore.Services;

/// <summary>
/// Coalescing single-flight for UI-triggered async work (round-64).
///
/// Contract — the two easy ways to get this wrong:
///  * QUEUE degradation: every trigger while running is queued, so 5 rapid
///    clicks cause 5 extra runs (i.e. no coalescing at all).
///  * DROP degradation: only the first in-flight request is kept and later
///    triggers are discarded, losing the newest choice.
///
/// Correct semantics: while a run is in progress, remember ONE pending flag
/// plus the LATEST argument; when the run finishes, do exactly ONE more run
/// with that latest argument. No queue, no loss.
///
/// Pure logic with an injectable runner, so the contract is unit-testable.
/// All callers are expected on the same (UI dispatcher) thread — no locks by
/// design; the VM methods that use it are dispatcher-affine.
/// </summary>
public sealed class CoalescingSingleFlight<TArg>
{
    private readonly Func<TArg, Task> _run;
    private bool _running;
    private bool _pending;
    private TArg _latest = default!;

    public CoalescingSingleFlight(Func<TArg, Task> run) =>
        _run = run ?? throw new ArgumentNullException(nameof(run));

    /// <summary>Number of actual runner invocations (tests / diagnostics).</summary>
    public int RunCount { get; private set; }
    public bool IsRunning => _running;
    public TArg Latest => _latest;

    /// <summary>Requests a run with <paramref name="arg"/>. The first caller
    /// owns the execution loop; coalesced callers receive a completed Task and
    /// only update the pending flag + latest argument.</summary>
    public Task RequestAsync(TArg arg)
    {
        _latest = arg;
        if (_running)
        {
            _pending = true;
            return Task.CompletedTask;
        }
        return RunLoopAsync();
    }

    private async Task RunLoopAsync()
    {
        _running = true;
        try
        {
            do
            {
                _pending = false;
                var snapshot = _latest;
                RunCount++;
                await _run(snapshot).ConfigureAwait(true);
            } while (_pending);
        }
        finally
        {
            _running = false;
        }
    }
}
