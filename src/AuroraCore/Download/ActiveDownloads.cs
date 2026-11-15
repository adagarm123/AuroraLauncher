using System.Collections.Concurrent;

namespace AuroraCore.Download;

/// <summary>
/// Round-87: which destination paths does a LIVE download job own right now?
///
/// The residue cleaner must never delete a <c>.part</c> that a running job is
/// still writing (that would turn a 90%-done 5 GB modpack into "start over").
/// Jobs themselves do not know their file list, and <see cref="PathWriteGate"/>
/// only knows "someone is queued", so the engine registers the destination it is
/// about to write and unregisters it when the file is done, failed or cancelled.
///
/// Registered for the WHOLE file operation (including the retry/backoff sleeps),
/// not just the byte-copy loop — a file sleeping between two attempts still owns
/// its <c>.part</c>.
/// </summary>
public static class ActiveDownloads
{
    private static readonly ConcurrentDictionary<string, int> Active = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Marks <paramref name="destination"/> as owned. Dispose to release
    /// (reference counted: two pipelines may legitimately fetch the same library).</summary>
    public static IDisposable Register(string? destination)
    {
        var key = Normalize(destination);
        if (key.Length == 0) return EmptyHandle.Instance;
        Active.AddOrUpdate(key, 1, (_, n) => n + 1);
        return new Handle(key);
    }

    public static bool IsActive(string? destination)
    {
        var key = Normalize(destination);
        return key.Length > 0 && Active.ContainsKey(key);
    }

    /// <summary>Snapshot for the scanner (already normalised).</summary>
    public static IReadOnlyCollection<string> Snapshot() => Active.Keys.ToArray();

    /// <summary>Test hook: forget everything (a leaked handle in one test must not
    /// protect a file in the next).</summary>
    public static void ResetForTests() => Active.Clear();

    private static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch { return path.TrimEnd('\\', '/'); }
    }

    private sealed class Handle : IDisposable
    {
        private readonly string _key;
        private int _disposed;

        public Handle(string key) => _key = key;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            while (true)
            {
                if (!Active.TryGetValue(_key, out var n)) return;
                if (n <= 1) { if (Active.TryRemove(_key, out _)) return; continue; }
                if (Active.TryUpdate(_key, n - 1, n)) return;
            }
        }
    }

    private sealed class EmptyHandle : IDisposable
    {
        public static readonly EmptyHandle Instance = new();
        public void Dispose() { }
    }
}
