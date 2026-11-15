namespace AuroraCore.Services;

/// <summary>
/// Round-93: a one-slot re-entrancy gate for UI commands.
///
/// The problem it solves is not a race — WPF runs commands on the UI thread — it
/// is a *user*: a click that shows no visible result within ~200 ms reads as "I
/// must have missed", so the button gets clicked again. For an async command
/// that is still awaiting (network, disk, process start), the second click would
/// start a second copy of the same work: duplicate account rows, two Java
/// detection sweeps, two installers writing the same files.
///
/// Keeping the state machine here (instead of inside the WPF command) means the
/// behaviour is unit-testable without a Dispatcher.
/// </summary>
public sealed class CommandGate
{
    private int _running;

    private int _suppressed;

    /// <summary>True while the guarded operation has not finished.</summary>
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>How many clicks were swallowed. Evidence that a user did
    /// double-click, which is otherwise invisible in a log.</summary>
    public int SuppressedClicks => Volatile.Read(ref _suppressed);

    /// <summary>Claims the slot. False means "already running, ignore this click".</summary>
    public bool TryEnter()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) == 0) return true;
        Interlocked.Increment(ref _suppressed);
        return false;
    }

    /// <summary>Releases the slot. Safe to call when not entered.</summary>
    public void Exit() => Interlocked.Exchange(ref _running, 0);

    /// <summary>Test/teardown helper.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _running, 0);
        Interlocked.Exchange(ref _suppressed, 0);
    }
}
