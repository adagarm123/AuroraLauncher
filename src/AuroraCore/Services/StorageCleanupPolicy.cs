using System.IO;

namespace AuroraCore.Services;

/// <summary>Round-88: one kind of junk the storage cleaner knows how to find.</summary>
public enum CleanupCategory
{
    /// <summary><c>cache/icons</c> — Modrinth/CurseForge project icons.</summary>
    CacheIcons,
    /// <summary><c>cache/optifine</c> — downloaded OptiFine installers.</summary>
    CacheOptiFine,
    /// <summary><c>cache/version_manifest_v2.json</c> — offline version list.</summary>
    ManifestCache,
    /// <summary>Anything in <c>update/</c> that is not the pending package.</summary>
    UpdateLeftover,
    /// <summary><c>logs/aurora-YYYY-MM-DD.log</c> older than the retention window.</summary>
    OldLogs,
    /// <summary>A <c>.part</c>/<c>.tmp</c> no job owns (round-87 classification).</summary>
    OrphanParts,
    /// <summary><c>versions/&lt;id&gt;/</c> with neither <c>&lt;id&gt;.json</c> nor <c>&lt;id&gt;.jar</c> and no files at all.</summary>
    EmptyVersionDirs,
    /// <summary><c>cache/installers</c> — loader metadata + installer jars (offline fallback).</summary>
    InstallerMetaCache,
    /// <summary>Partials the download journal can still resume — the user's progress.</summary>
    ResumableParts
}

public enum CleanupRisk
{
    /// <summary>Nothing is lost: it is rebuilt or was never useful.</summary>
    Safe,
    /// <summary>Deleting costs an offline capability — never preselected.</summary>
    CostsOffline,
    /// <summary>Never offered by any category (reserved for the protected list).</summary>
    Never
}

public sealed record CleanupCategoryInfo(
    CleanupCategory Category,
    string DisplayName,
    CleanupRisk Risk,
    bool DefaultSelected,
    string Description);

/// <summary>
/// Round-88: the rules behind "scan my storage and clean the junk".
///
/// The danger here is never "did not clean enough" — it is "cleaned too much".
/// So every rule is written as an allow-list, not a deny-list: a path is
/// deletable only if this class explicitly says so. <c>instances/</c>,
/// <c>backups/</c>, <c>libraries/</c>, <c>assets/</c> and <c>java/</c> are simply
/// not on the list, which means a future category cannot "accidentally" reach
/// them by forgetting a check — it has to add one, and the guard test will
/// notice (<c>Guard #107</c>).
/// </summary>
public static class StorageCleanupPolicy
{
    /// <summary>Log files older than this are junk. Two weeks keeps "what
    /// happened last time I played" answerable.</summary>
    public const int DefaultLogKeepDays = 14;

    /// <summary>Files inside <c>update/</c> that must survive every cleanup:
    /// they ARE the pending update (removing them silently cancels it).</summary>
    public static readonly string[] StagedUpdateFileNames =
    {
        "AuroraLauncher.new.exe",
        "build-info.txt"
    };

    /// <summary>Names the cleaner must never touch even when they look like junk.</summary>
    public static readonly string[] ProtectedFileNames =
    {
        "config.json",
        "instances.json",
        "accounts.json",
        "download-journal.log"
    };

    /// <summary>Directories that are never cleaned, at any depth. Listed here so
    /// the guard test and the docs can point at one source of truth.
    ///
    /// <c>libraries/</c> and <c>assets/</c> are deliberately NOT in this list:
    /// their PAYLOAD is untouchable (the switch below only ever allows a
    /// <c>.part</c>/<c>.tmp</c> there), but half-written leftovers of a download
    /// are exactly what round 87 already deletes, and treating them differently
    /// in the two cleanup entry points would just confuse people.</summary>
    public static readonly string[] ProtectedDirectories =
    {
        "instances", "backups", "java"
    };

    public static readonly CleanupCategoryInfo[] All =
    {
        new(CleanupCategory.CacheIcons, "项目图标缓存", CleanupRisk.Safe, true,
            "内容中心里浏览过的模组/整合包图标。删了下次浏览会重新下载，不影响任何功能。"),
        new(CleanupCategory.CacheOptiFine, "OptiFine 安装器缓存", CleanupRisk.Safe, true,
            "下载过的 OptiFine 安装器 jar。下次安装会重新下载。"),
        new(CleanupCategory.ManifestCache, "版本清单离线缓存", CleanupRisk.Safe, true,
            "上次成功获取的版本列表（约 270 KB）。删了下次启动重新拉；弱网时首次打开可能短暂空白。"),
        new(CleanupCategory.UpdateLeftover, "更新包残留", CleanupRisk.Safe, true,
            "update/ 目录里下到一半或已用完的更新文件（不含正在等待安装的那个包）。"),
        new(CleanupCategory.OldLogs, "历史日志", CleanupRisk.Safe, true,
            $"超过 {DefaultLogKeepDays} 天的启动器日志。删了不影响使用，只是旧问题少一份证据。今天的日志永远保留。"),
        new(CleanupCategory.OrphanParts, "无主的下载残片", CleanupRisk.Safe, true,
            "没人认领的 .part/.tmp（下载中断留下的半截文件，且不在断点续传台账里）。"),
        new(CleanupCategory.EmptyVersionDirs, "空壳版本目录", CleanupRisk.Safe, true,
            "versions/ 里既没有 <版本>.json 也没有 <版本>.jar、且没有任何文件的目录（安装失败/中断留下的）。"),
        new(CleanupCategory.InstallerMetaCache, "加载器元数据缓存", CleanupRisk.CostsOffline, false,
            "cache/installers 里的加载器版本清单与安装器 jar。清理后断网时加载器列表可能不可用，下次必须联网重新获取。"),
        new(CleanupCategory.ResumableParts, "可续传的下载残片", CleanupRisk.CostsOffline, false,
            "上次没下完、还能在「下载管理」里继续的文件。清理后这部分进度归零、需要重新下载。")
    };

    public static CleanupCategoryInfo Describe(CleanupCategory category)
        => All.FirstOrDefault(i => i.Category == category)
           ?? new CleanupCategoryInfo(category, category.ToString(), CleanupRisk.Never, false, "");

    /// <summary>Default checked state — the UI must bind THIS, never hardcode
    /// (a CostsOffline item preselected would destroy an offline capability
    /// without the user noticing).</summary>
    public static bool IsDefaultSelected(CleanupCategory category) => Describe(category).DefaultSelected;

    /// <summary>
    /// Shape-level allow-list: may <paramref name="path"/> be deleted at all?
    /// Pure (no disk access) so the matrix is unit-testable. A
    /// <c>versions/&lt;id&gt;</c> result here still has to pass
    /// <see cref="IsEmptyVersionShell"/> before anything is removed, and the
    /// staged update package is refused outright.
    /// </summary>
    public static bool IsAllowedPath(string? dataRoot, string? path)
    {
        if (string.IsNullOrWhiteSpace(dataRoot) || string.IsNullOrWhiteSpace(path)) return false;

        string root, full;
        try
        {
            root = Path.GetFullPath(dataRoot).TrimEnd('\\', '/');
            full = Path.GetFullPath(path).TrimEnd('\\', '/');
        }
        catch { return false; }

        if (string.Equals(root, full, StringComparison.OrdinalIgnoreCase)) return false; // never the root itself
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(root + '/', StringComparison.OrdinalIgnoreCase))
            return false; // outside the data root

        var rel = full[(root.Length + 1)..].Replace('\\', '/');
        var segments = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;

        var top = segments[0].ToLowerInvariant();
        var name = segments[^1];
        var fileName = Path.GetFileName(full);

        // --- 1. hard rejections, before any per-category rule --------------
        // Whole trees the launcher must never prune on the user's behalf.
        if (ProtectedDirectories.Any(d => string.Equals(d, top, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (ProtectedFileNames.Any(f => string.Equals(f, fileName, StringComparison.OrdinalIgnoreCase)))
            return false;

        // --- 2. partial files anywhere the launcher writes them -----------
        var isPartial = fileName.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                        || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

        switch (top)
        {
            case "cache":
                if (segments.Length < 2) return false;
                var sub = segments[1].ToLowerInvariant();
                if (sub is "icons" or "optifine" or "installers") return segments.Length >= 3;
                if (sub == "version_manifest_v2.json") return segments.Length == 2;
                return false;

            case "update":
                if (segments.Length < 2) return false;
                // The pending package is not junk, no matter how old it is.
                return !StagedUpdateFileNames.Any(f => string.Equals(f, fileName, StringComparison.OrdinalIgnoreCase));

            case "logs":
                return segments.Length == 2
                       && fileName.StartsWith("aurora-", StringComparison.OrdinalIgnoreCase)
                       && fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase);

            case "versions":
                // Only the version FOLDER itself; its contents are never deleted
                // file-by-file (a shell contains no files to begin with).
                return segments.Length == 2;

            default:
                // libraries/assets/… : only half-written files, never the payload.
                return isPartial;
        }
    }

    /// <summary>Is this directory a failed-install shell? It must have neither
    /// <c>&lt;id&gt;.json</c> nor <c>&lt;id&gt;.jar</c> AND contain no file at all
    /// (recursively). A half-installed folder that still holds a jar, a json or
    /// even a natives/ payload is left alone — it may be about to be completed.
    /// </summary>
    public static bool IsEmptyVersionShell(string? versionDir, string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionDir) || string.IsNullOrWhiteSpace(versionId)) return false;
        try
        {
            if (!Directory.Exists(versionDir)) return false;
            if (File.Exists(Path.Combine(versionDir, versionId + ".json"))) return false;
            if (File.Exists(Path.Combine(versionDir, versionId + ".jar"))) return false;
            // Any file at all (natives/, logs, leftover .part) → not a shell.
            foreach (var _ in Directory.EnumerateFiles(versionDir, "*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.ReparsePoint
                     }))
                return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Should this log file be removed? Only names produced by
    /// <c>Logger</c> qualify; the date comes from the NAME (not the mtime, which
    /// changes when a file is copied) and anything unparseable is kept.</summary>
    public static bool ShouldDeleteLog(string? path, DateTime nowUtc, int keepDays = DefaultLogKeepDays)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var name = Path.GetFileName(path);
        if (!name.StartsWith("aurora-", StringComparison.OrdinalIgnoreCase)) return false;
        if (!name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) return false;

        var stamp = name["aurora-".Length..^".log".Length];
        if (!DateTime.TryParseExact(stamp, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date))
            return false;

        var days = keepDays < 1 ? DefaultLogKeepDays : keepDays;
        // Today's log is always kept: it is the one being written right now.
        if (date.Date >= nowUtc.Date) return false;
        return (nowUtc.Date - date.Date).TotalDays >= days;
    }

    /// <summary>The list shown to the user as "never touched", so the UI can
    /// make the promise without hardcoding prose in XAML.</summary>
    public static string ProtectedSummary =>
        "不会清理：存档、模组、配置、backups、libraries、assets、一键安装的 Java、以及任何正在下载的文件。";
}
