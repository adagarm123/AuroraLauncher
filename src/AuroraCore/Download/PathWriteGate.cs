namespace AuroraCore.Download;

/// <summary>Round-73: one writer per destination path, process-wide.
///
/// Round-72 made several installs run at once, and two of them can legitimately
/// want the SAME file: two Fabric instances on 1.21.1 share
/// <c>libraries/</c> and <c>assets/</c>, and both pipelines download the same
/// jar. Each <see cref="DownloadEngine"/> only de-duplicates inside itself, so
/// before this gate two engines could open <c>foo.jar.part</c> at the same time
/// (sharing violation), or one could commit a half-written file that the other
/// had just started.
///
/// The gate is keyed by the FINAL destination, is async, and never nests, so
/// parallel downloads of different files are unaffected.
///
/// Also used for the few shared metadata writes (instance registry) where the
/// same reasoning applies.</summary>
public static class PathWriteGate
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int References;
    }

    private static readonly object MapLock = new();
    private static readonly Dictionary<string, Entry> Map = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of distinct paths currently tracked (diagnostics/tests).</summary>
    public static int TrackedPaths
    {
        get { lock (MapLock) return Map.Count; }
    }

    /// <summary>Waits until this path is free for writing. Dispose the handle to
    /// release. Cancellation aborts the wait and releases the reference.</summary>
    public static async Task<IDisposable> AcquireAsync(string path, CancellationToken ct = default)
    {
        var key = Normalize(path);
        Entry entry;
        lock (MapLock)
        {
            if (!Map.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                Map[key] = entry;
            }
            entry.References++;
        }

        var acquired = false;
        try
        {
            await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
        }
        finally
        {
            if (!acquired) Release(key, entry, false);
        }
        return new Handle(key, entry);
    }

    /// <summary>Blocking variant for short metadata writes on a worker thread.</summary>
    public static IDisposable Acquire(string path)
    {
        var key = Normalize(path);
        Entry entry;
        lock (MapLock)
        {
            if (!Map.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                Map[key] = entry;
            }
            entry.References++;
        }
        entry.Semaphore.Wait();
        return new Handle(key, entry);
    }

    private static void Release(string key, Entry entry, bool releaseSemaphore)
    {
        if (releaseSemaphore)
        {
            try { entry.Semaphore.Release(); } catch { }
        }
        lock (MapLock)
        {
            if (--entry.References > 0) return;
            // Nobody holds or waits for this path any more: drop the entry so a
            // long session (thousands of assets) cannot grow the map forever.
            if (entry.Semaphore.CurrentCount == 1) Map.Remove(key);
        }
    }

    /// <summary>Windows paths are case-insensitive; normalise so Foo.jar and
    /// foo.jar cannot slip past the gate.</summary>
    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            return System.IO.Path.GetFullPath(path).TrimEnd('\\', '/');
        }
        catch
        {
            return path;
        }
    }

    private sealed class Handle : IDisposable
    {
        private readonly string _key;
        private readonly Entry _entry;
        private int _disposed;

        public Handle(string key, Entry entry)
        {
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Release(_key, _entry, true);
        }
    }
}
