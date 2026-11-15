using System.IO;
using System.Linq;
using AuroraCore.Services;
using AuroraCore.Utils;

namespace AuroraCore.Download;

/// <summary>Who claims a leftover <c>.part</c> file.</summary>
public enum ResidueOwner
{
    /// <summary>A live job is writing it right now — never touch.</summary>
    ActiveTask,
    /// <summary>The journal knows it, but no job is running. Resumable if the user
    /// accepts the banner on the download page; junk once they say "ignore".</summary>
    Journaled,
    /// <summary>Nobody claims it: a &lt;4 MB file the journal never tracked, or an
    /// updater staging leftover in <c>update/</c>.</summary>
    Unowned
}

public sealed class ResidueFile
{
    public string Path { get; init; } = "";
    public string Destination { get; init; } = "";
    public long Bytes { get; init; }
    public ResidueOwner Owner { get; init; }
    public string FileName => System.IO.Path.GetFileName(Path);
}

public sealed class ResidueScan
{
    public IReadOnlyList<ResidueFile> Files { get; init; } = Array.Empty<ResidueFile>();

    public long TotalBytes => Files.Sum(f => f.Bytes);
    public int Count => Files.Count;

    public IEnumerable<ResidueFile> Reclaimable => Files.Where(f => f.Owner != ResidueOwner.ActiveTask);
    public long ReclaimableBytes => Reclaimable.Sum(f => f.Bytes);
    public int ReclaimableCount => Files.Count(f => f.Owner != ResidueOwner.ActiveTask);

    public int ActiveCount => Files.Count(f => f.Owner == ResidueOwner.ActiveTask);
    public int JournaledCount => Files.Count(f => f.Owner == ResidueOwner.Journaled);
    public int UnownedCount => Files.Count(f => f.Owner == ResidueOwner.Unowned);
    public long JournaledBytes => Files.Where(f => f.Owner == ResidueOwner.Journaled).Sum(f => f.Bytes);

    /// <summary>Nothing to tell the user about.</summary>
    public bool IsEmpty => Files.Count == 0;
}

public sealed class ResidueCleanupResult
{
    public int DeletedCount { get; init; }
    public long FreedBytes { get; init; }
    public int KeptCount { get; init; }
    public long KeptBytes { get; init; }
    public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();

    public bool AnythingDeleted => DeletedCount > 0;
}

/// <summary>
/// Round-87: leftover <c>.part</c> files are the one kind of junk a launcher
/// reliably produces and a user cannot safely remove by hand (they have to know
/// which ones are resumable). This class is the single place that decides what a
/// <c>.part</c> file IS, so the settings button, the startup banner and the tests
/// all agree.
///
/// The decision table:
///   · a live job owns the destination      → keep (deleting it destroys progress)
///   · the journal knows the destination    → reclaimable, but the UI must WARN
///     (it is what 「继续下载」 would resume)
///   · anything else                        → plain junk, delete silently
///
/// <c>update/</c> is never journalled (see <see cref="DownloadJournal.ShouldTrack"/>),
/// so an interrupted update package lands in the third row by construction.
/// </summary>
public static class DownloadResidue
{
    /// <summary>Conservative defaults: a normal install peaks at a handful of
    /// MB of partials, so 200 MB / 50 files means something really is stuck.</summary>
    public const long DefaultWarnBytes = 200L * 1024 * 1024;
    public const int DefaultWarnCount = 50;

    public const string PartSuffix = ".part";

    /// <summary>Scans every <c>.part</c> under the data root. Cheap enough to run
    /// on a worker thread at startup (one directory walk, no file reads).
    /// Never throws — an unreadable folder is simply not scanned.</summary>
    public static ResidueScan Scan(string rootDirectory, IReadOnlyCollection<string>? additionallyOwned = null)
    {
        var files = new List<ResidueFile>();
        if (string.IsNullOrWhiteSpace(rootDirectory)) return new ResidueScan();
        if (!Directory.Exists(rootDirectory)) return new ResidueScan();

        var journaled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var dest in DownloadJournal.Destinations(rootDirectory)) journaled.Add(Normalize(dest));
        }
        catch { /* no journal = nothing is claimed */ }

        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dest in ActiveDownloads.Snapshot()) owned.Add(Normalize(dest));
        if (additionallyOwned != null)
            foreach (var dest in additionallyOwned) owned.Add(Normalize(dest));

        IEnumerable<string> found;
        try
        {
            found = Directory.EnumerateFiles(rootDirectory, "*" + PartSuffix, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                // A junction pointing back at an ancestor would loop forever.
                AttributesToSkip = FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false
            });
        }
        catch { return new ResidueScan(); }

        foreach (var partPath in found)
        {
            try
            {
                var destination = partPath[..^PartSuffix.Length];
                var key = Normalize(destination);
                var owner = owned.Contains(key) ? ResidueOwner.ActiveTask
                    : journaled.Contains(key) ? ResidueOwner.Journaled
                    : ResidueOwner.Unowned;
                files.Add(new ResidueFile
                {
                    Path = partPath,
                    Destination = destination,
                    Bytes = new FileInfo(partPath).Length,
                    Owner = owner
                });
            }
            catch { /* vanished or locked between enumerate and stat */ }
        }

        files.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        return new ResidueScan { Files = files };
    }

    /// <summary>The startup banner rule: quiet unless BOTH the byte volume and
    /// the file count are unusual… no — either one alone is suspicious (20 × 300 MB,
    /// or 400 × 1 MB), so this is OR. Callers pass 0 to disable one dimension.</summary>
    public static bool ShouldWarn(ResidueScan scan, long byteThreshold, int countThreshold)
    {
        if (scan == null || scan.IsEmpty) return false;
        var bytes = scan.ReclaimableBytes;
        var count = scan.ReclaimableCount;
        if (count <= 0) return false; // only live downloads left: nothing to say
        var byBytes = byteThreshold > 0 && bytes > byteThreshold;
        var byCount = countThreshold > 0 && count > countThreshold;
        return byBytes || byCount;
    }

    /// <summary>Config-driven variant: the two numbers come from
    /// <c>LauncherConfig.ResidueWarnBytesMb</c> / <c>ResidueWarnCount</c>, where a
    /// value of 0 (or less) disables that dimension.</summary>
    public static bool ShouldWarnFromConfig(ResidueScan scan, int bytesMbThreshold, int countThreshold)
    {
        var bytes = bytesMbThreshold <= 0 ? 0L : (long)bytesMbThreshold * 1024 * 1024;
        return ShouldWarn(scan, bytes, countThreshold <= 0 ? 0 : countThreshold);
    }

    /// <summary>Deletes every reclaimable partial. Files owned by a live job are
    /// kept and reported, never deleted — the whole point of the owner map.</summary>
    public static ResidueCleanupResult Clean(ResidueScan scan)
    {
        var deleted = 0;
        long freed = 0;
        var kept = 0;
        long keptBytes = 0;
        var failures = new List<string>();
        var forgotten = new List<string>();

        foreach (var file in scan.Files)
        {
            if (file.Owner == ResidueOwner.ActiveTask)
            {
                kept++;
                keptBytes += file.Bytes;
                continue;
            }
            try
            {
                // Re-check right before deleting: a job may have started on this
                // very destination during the scan (the map is live).
                if (ActiveDownloads.IsActive(file.Destination))
                {
                    kept++;
                    keptBytes += file.Bytes;
                    continue;
                }
                var size = new FileInfo(file.Path).Length;
                File.Delete(file.Path);
                deleted++;
                freed += size;
                // Round-88 review: the journal must not keep pointing at a file
                // we just deleted (it would be filtered on replay, but the line
                // would linger until the next compaction).
                if (file.Owner == ResidueOwner.Journaled) forgotten.Add(file.Destination);
            }
            catch (Exception ex)
            {
                failures.Add($"{file.FileName}: {ex.Message}");
            }
        }

        if (forgotten.Count > 0) DownloadJournal.Forget(forgotten);

        var summary = $"清理残留下载：删除 {deleted} 个文件，释放 {JobProgressPolicy.FormatBytes(freed)}" +
                      (kept > 0 ? $"，保留 {kept} 个仍在下载的文件（{JobProgressPolicy.FormatBytes(keptBytes)}）" : "") +
                      (failures.Count > 0 ? $"，{failures.Count} 个无法删除" : "");
        if (deleted > 0 || failures.Count > 0) Logger.Instance.Info("Download", summary);
        return new ResidueCleanupResult
        {
            DeletedCount = deleted,
            FreedBytes = freed,
            KeptCount = kept,
            KeptBytes = keptBytes,
            Failures = failures
        };
    }

    /// <summary>One-line human summary of a scan.</summary>
    public static string Describe(ResidueScan scan)
    {
        if (scan.IsEmpty) return "没有发现未完成的下载残留";
        var text = $"检测到 {scan.ReclaimableCount} 个未完成的下载残留（约 {JobProgressPolicy.FormatBytes(scan.ReclaimableBytes)}）";
        if (scan.ActiveCount > 0) text += $"，另有 {scan.ActiveCount} 个正在下载中（不会被清理）";
        return text;
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch { return path.TrimEnd('\\', '/'); }
    }
}
