using System.IO;
using AuroraCore.Utils;

namespace AuroraCore.Services;

/// <summary>
/// Round-87: <c>update/</c> is a staging area, not an archive. A check that gets
/// interrupted (app closed, network died, disk full) leaves
/// <c>AuroraLauncher.patch</c>, <c>*.merged.exe</c> or a multi-megabyte
/// <c>.part</c> behind, and nothing ever removed them — the folder only grew.
///
/// Every update check now sweeps it with a one-day retention. The currently
/// staged package is exempt by path, not by age: it is the thing the next launch
/// will install, and deleting it because the user left the launcher open for two
/// days would silently cancel their pending update.
/// </summary>
public static class UpdateStaging
{
    public const int RetentionHours = 24;

    /// <summary>Pure selector: which files in <paramref name="updateDir"/> may be
    /// removed right now? Exposed for tests — no IO beyond stat.</summary>
    public static IReadOnlyList<string> SelectStale(
        string updateDir, DateTime nowUtc, IEnumerable<string>? keepPaths, int retentionHours = RetentionHours)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(updateDir) || !Directory.Exists(updateDir)) return result;

        var hours = retentionHours < 1 ? 1 : retentionHours;
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (keepPaths != null)
        {
            foreach (var path in keepPaths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                try { keep.Add(Path.GetFullPath(path).TrimEnd('\\', '/')); } catch { }
            }
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(updateDir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    string full;
                    try { full = Path.GetFullPath(file).TrimEnd('\\', '/'); } catch { full = file; }
                    if (keep.Contains(full)) continue;
                    var age = nowUtc - File.GetLastWriteTimeUtc(file);
                    if (age.TotalHours >= hours) result.Add(file);
                }
                catch { /* vanished between enumerate and stat */ }
            }
        }
        catch { /* unreadable staging folder: nothing to clean */ }

        return result;
    }

    /// <summary>Deletes the stale files and logs one line. A file in use (a running
    /// updater host) simply fails to delete — never propagated.</summary>
    public static int Clean(string updateDir, DateTime nowUtc, IEnumerable<string>? keepPaths,
        int retentionHours = RetentionHours)
    {
        var removed = 0;
        long freed = 0;
        var failed = 0;
        foreach (var file in SelectStale(updateDir, nowUtc, keepPaths, retentionHours))
        {
            try
            {
                var size = new FileInfo(file).Length;
                File.Delete(file);
                removed++;
                freed += size;
            }
            catch { failed++; }
        }

        if (removed > 0)
            Logger.Instance.Info("Update",
                $"清理更新暂存目录：删除 {removed} 个超过 {retentionHours} 小时的残留文件（共 {JobProgressPolicy.FormatBytes(freed)}）" +
                (failed > 0 ? $"，{failed} 个正被占用" : ""));
        return removed;
    }
}
