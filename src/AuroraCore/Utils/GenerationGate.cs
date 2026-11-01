namespace AuroraCore.Utils;

/// <summary>
/// Monotonic generation gate for "optimistic UI + stale-response discard"
/// flows (e.g. the loader cards rebuilt on every version selection).
/// Each Begin() bumps the generation; a result captured under an older
/// generation is stale and must be discarded. This covers the case where
/// two rapid selections cause their network responses to arrive out of
/// order — the older response must never overwrite the newer card state.
/// </summary>
public sealed class GenerationGate
{
    private long _current;

    /// <summary>Starts a new operation and returns its generation token.</summary>
    public long Begin() => Interlocked.Increment(ref _current);

    /// <summary>True when <paramref name="generation"/> is still the newest
    /// operation — only then may its result be applied.</summary>
    public bool IsCurrent(long generation) => Volatile.Read(ref _current) == generation;
}
