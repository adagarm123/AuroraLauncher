using System.Collections.Concurrent;
using System.IO;
using AuroraCore.Utils;

namespace AuroraCore.Download;

/// <summary>One download that was interrupted: the engine still has a
/// <c>.part</c> file waiting for it.</summary>
public sealed class PendingDownload
{
    public string Url { get; init; } = "";
    public string Destination { get; init; } = "";
    public string? ExpectedSha1 { get; init; }
    public long ExpectedSize { get; init; }
    public long PartialBytes { get; init; }
    public DateTime LastWriteUtc { get; init; }

    public string FileName => Path.GetFileName(Destination);
    public double CompleteRatio => ExpectedSize > 0 ? Math.Clamp((double)PartialBytes / ExpectedSize, 0, 1) : 0;
}

/// <summary>
/// Round-74: a crash-safe record of what was being downloaded, so a killed
/// process (task manager, power cut) does not force the user to re-download a
/// 5 GB modpack from zero. The engine appends a line per file before writing and
/// a completion line afterwards; the log is replayed on startup.
///
/// Disk access happens on a dedicated writer thread — round-69's lesson (a
/// synchronous <c>File.AppendAllText</c> inside a download callback measured
/// 48–218 ms of UI-thread wait) applies here too.
///
/// Only files above <see cref="MinJournalBytes"/> are tracked: journalling tens
/// of thousands of 4 KB assets would cost more than it saves, and they resume
/// in a second anyway.
/// </summary>
public static class DownloadJournal
{
    public const long MinJournalBytes = 4L * 1024 * 1024;
    private const string FileName = "download-journal.log";

    private static readonly ConcurrentQueue<string> Queue = new();
    private static readonly object Sync = new();
    private static ManualResetEventSlim? _signal;
    private static Thread? _writer;
    private static volatile string? _path;
    /// <summary>Lines queued but not yet on disk. <see cref="Flush"/> waits on
    /// this, NOT on the queue: the writer may have dequeued without writing.</summary>
    private static int _pending;

    public static string PathFor(string rootDirectory) => Path.Combine(rootDirectory, "cache", FileName);

    /// <summary>Should this entry be journalled at all?</summary>
    public static bool ShouldTrack(DownloadEntry entry)
    {
        if (entry.ExpectedSize is long size && size < MinJournalBytes) return false;
        // The updater stages into update/ and has its own recovery rules — it
        // must never show up as "unfinished download" in the UI.
        var root = LauncherConfig.Instance.RootDirectory;
        if (!string.IsNullOrEmpty(root))
        {
            var updateDir = Path.Combine(root, "update");
            if (entry.Destination.StartsWith(updateDir, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return !string.IsNullOrWhiteSpace(entry.Url) && !string.IsNullOrWhiteSpace(entry.Destination);
    }

    public static void Record(DownloadEntry entry)
    {
        if (!ShouldTrack(entry)) return;
        Interlocked.Increment(ref _pending);
        Queue.Enqueue("+\t" + Clean(entry.Url) + "\t" + Clean(entry.Destination) + "\t" +
                      (entry.ExpectedSize ?? 0) + "\t" + Clean(entry.ExpectedSha1 ?? ""));
        EnsureWriter();
    }

    public static void Complete(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) return;
        Interlocked.Increment(ref _pending);
        Queue.Enqueue("-\t" + Clean(destination));
        EnsureWriter();
    }

    /// <summary>Round-88 review: a cleaner deleted the <c>.part</c> of a file the
    /// journal still lists. <see cref="Load"/> would ignore it anyway ("no .part,
    /// no entry"), but the line would sit in the log until the next replay
    /// compacts it — and if the journal is never replayed again (user never
    /// restarts the launcher) it never gets compacted. Writing the same "-" line
    /// the engine writes on success keeps the log honest at the source.</summary>
    public static void Forget(string destination) => Complete(destination);

    /// <summary>Round-88: forget several destinations at once (one queue write
    /// per file, same as the engine does — no rewrite of the whole log).</summary>
    public static void Forget(IEnumerable<string> destinations)
    {
        foreach (var destination in destinations) Forget(destination);
    }

    /// <summary>Waits (bounded) for queued lines to reach disk. Used by exit and
    /// before the recovery scan, so the banner never misses the newest file.</summary>
    public static void Flush(int timeoutMs = 1500)
    {
        try { _signal?.Set(); } catch { }
        var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        while (Volatile.Read(ref _pending) > 0 && Environment.TickCount64 < deadline)
            Thread.Sleep(10);
    }

    /// <summary>Round-87: the destinations the journal still holds, parsed
    /// read-only (no compaction, no disk writes) so a background residue scan can
    /// ask as often as it likes without touching the file the writer thread owns.</summary>
    public static IReadOnlyCollection<string> Destinations(string rootDirectory)
        => Parse(rootDirectory).Keys.ToArray();

    private static Dictionary<string, (string Url, long Size, string Sha1)> Parse(string rootDirectory)
    {
        var path = PathFor(rootDirectory);
        var entries = new Dictionary<string, (string Url, long Size, string Sha1)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path)) return entries;
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length < 3) continue;
                var parts = line.Split('\t');
                if (parts.Length >= 2 && parts[0] == "-")
                {
                    entries.Remove(parts[1]);
                }
                else if (parts.Length >= 4 && parts[0] == "+")
                {
                    entries[parts[2]] = (parts[1], long.TryParse(parts[3], out var s) ? s : 0,
                        parts.Length > 4 ? parts[4] : "");
                }
            }
        }
        catch { /* a corrupt journal means "nothing to resume" */ }
        return entries;
    }

    /// <summary>Replays the journal and keeps only entries whose <c>.part</c>
    /// still exists. Never throws: a corrupt journal means "nothing to resume".</summary>
    public static IReadOnlyList<PendingDownload> Load(string rootDirectory)
    {
        var path = PathFor(rootDirectory);
        if (!File.Exists(path)) return Array.Empty<PendingDownload>();
        var entries = Parse(rootDirectory);

        var result = new List<PendingDownload>();
        foreach (var (dest, info) in entries)
        {
            try
            {
                var part = dest + ".part";
                if (!File.Exists(part)) continue;
                var fileInfo = new FileInfo(part);
                if (fileInfo.Length <= 0) continue;
                result.Add(new PendingDownload
                {
                    Url = info.Url,
                    Destination = dest,
                    ExpectedSize = info.Size,
                    ExpectedSha1 = string.IsNullOrEmpty(info.Sha1) ? null : info.Sha1,
                    PartialBytes = fileInfo.Length,
                    LastWriteUtc = fileInfo.LastWriteTimeUtc
                });
            }
            catch { /* a locked/permission-denied part is simply not offered */ }
        }
        var ordered = result.OrderByDescending(p => p.PartialBytes).ToList();
        // Compaction: without it the log grows by two lines per large file for
        // the lifetime of the installation. Startup is the only writer here.
        try
        {
            var kept = new System.Text.StringBuilder();
            foreach (var pending in ordered)
            {
                kept.Append("+\t").Append(Clean(pending.Url)).Append('\t')
                    .Append(Clean(pending.Destination)).Append('\t')
                    .Append(pending.ExpectedSize).Append('\t')
                    .Append(Clean(pending.ExpectedSha1 ?? "")).Append('\n');
            }
            File.WriteAllText(path, kept.ToString(), new System.Text.UTF8Encoding(false));
        }
        catch { /* best effort */ }
        return ordered;
    }

    /// <summary>Drops the journal (and optionally the leftover <c>.part</c>
    /// files) so the user is not asked again about work they abandoned.</summary>
    public static int Discard(string rootDirectory, bool deletePartials)
    {
        var removed = 0;
        if (deletePartials)
        {
            foreach (var pending in Load(rootDirectory))
            {
                try
                {
                    var part = pending.Destination + ".part";
                    if (File.Exists(part)) { File.Delete(part); removed++; }
                }
                catch { /* best effort */ }
            }
        }
        try { File.Delete(PathFor(rootDirectory)); } catch { }
        return removed;
    }

    private static string Clean(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static void EnsureWriter()
    {
        if (_writer != null) return;
        lock (Sync)
        {
            if (_writer != null) return;
            _signal = new ManualResetEventSlim(false);
            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "aurora-download-journal" };
            _writer.Start();
        }
    }

    private static void WriterLoop()
    {
        var signal = _signal!;
        while (true)
        {
            try
            {
                signal.Wait(250);
                signal.Reset();
                if (Queue.IsEmpty) continue;

                var path = _path ??= PathFor(LauncherConfig.Instance.RootDirectory);
                var batch = new System.Text.StringBuilder();
                var lines = 0;
                while (Queue.TryDequeue(out var line)) { batch.Append(line).Append('\n'); lines++; }
                if (batch.Length == 0) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                lock (Sync)
                {
                    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
                    writer.Write(batch.ToString());
                }
                Interlocked.Add(ref _pending, -lines);
            }
            catch
            {
                // A failed append must never kill the writer or the app — but it
                // must not leave Flush() waiting forever either.
                Interlocked.Exchange(ref _pending, 0);
                Thread.Sleep(250);
            }
        }
    }

    /// <summary>Test/diagnostic hook: pins the journal file so a temp root can be
    /// exercised without touching the real configuration. Null restores the
    /// normal "derive from LauncherConfig" behaviour.</summary>
    public static void OverridePathForTests(string? path) => _path = path;
}
