using System.IO;
using AuroraCore.Download;
using AuroraCore.Utils;

namespace AuroraCore.Services;

/// <summary>One file (or one empty version folder) that a scan proposes to remove.</summary>
public sealed class CleanupItem
{
    public CleanupCategory Category { get; init; }
    public string Path { get; init; } = "";
    /// <summary>0 for directories (an empty shell has nothing to weigh).</summary>
    public long Bytes { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsLocked { get; init; }
    public string FileName => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
}

/// <summary>Everything found for ONE category — this is what the UI lists.</summary>
public sealed class CleanupGroup
{
    public CleanupCategory Category { get; init; }
    public CleanupCategoryInfo Info { get; init; } = null!;
    public IReadOnlyList<CleanupItem> Items { get; init; } = Array.Empty<CleanupItem>();

    public int FileCount => Items.Count(i => !i.IsDirectory);
    public int DirCount => Items.Count(i => i.IsDirectory);
    public long Bytes => Items.Sum(i => i.Bytes);

    /// <summary>"12 个文件 · 3.4 MB" / "2 个空目录".</summary>
    public string AmountText
    {
        get
        {
            var parts = new List<string>();
            if (FileCount > 0) parts.Add($"{FileCount} 个文件");
            if (DirCount > 0) parts.Add($"{DirCount} 个空目录");
            if (parts.Count == 0) parts.Add("无");
            return string.Join(" + ", parts) + (Bytes > 0 ? $" · {JobProgressPolicy.FormatBytes(Bytes)}" : "");
        }
    }
}

public sealed class CleanupReport
{
    public IReadOnlyList<CleanupGroup> Groups { get; init; } = Array.Empty<CleanupGroup>();
    /// <summary>Partials owned by a RUNNING job — reported, never listed for deletion.</summary>
    public int ActivePartCount { get; init; }
    public string RootDirectory { get; init; } = "";

    public bool IsEmpty => Groups.Count == 0;

    public int TotalFiles => Groups.Sum(g => g.FileCount);
    public int TotalDirs => Groups.Sum(g => g.DirCount);
    public long TotalBytes => Groups.Sum(g => g.Bytes);

    public long BytesOf(IEnumerable<CleanupCategory> categories)
    {
        var set = categories.ToHashSet();
        return Groups.Where(g => set.Contains(g.Category)).Sum(g => g.Bytes);
    }

    public int CountOf(IEnumerable<CleanupCategory> categories)
    {
        var set = categories.ToHashSet();
        return Groups.Where(g => set.Contains(g.Category)).Sum(g => g.Items.Count);
    }
}

public sealed class CleanupResult
{
    public int DeletedFiles { get; init; }
    public int DeletedDirs { get; init; }
    public long FreedBytes { get; init; }
    public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();

    public bool AnythingDeleted => DeletedFiles + DeletedDirs > 0;

    /// <summary>"已释放 3.4 MB（12 个文件 + 2 个空目录）" — plus the honest bit.</summary>
    public string Describe()
    {
        var text = AnythingDeleted
            ? $"已释放 {JobProgressPolicy.FormatBytes(FreedBytes)}" +
              $"（{DeletedFiles} 个文件" + (DeletedDirs > 0 ? $" + {DeletedDirs} 个空目录" : "") + "）"
            : "没有删除任何文件";
        if (Failures.Count > 0)
            text += $"；{Failures.Count} 个文件被占用未删除（可能正在下载或被杀软扫描）";
        return text;
    }
}

/// <summary>
/// Round-88: scan the data root for junk and delete what the user selected.
///
/// Two rules that come from earlier rounds and are NOT negotiable here:
///  · a partial that a live job owns is not junk (round 87 / <see cref="ActiveDownloads"/>);
///  · a partial the journal can still resume is the user's progress, so it is
///    its own category, off by default (round 87 warned about exactly this).
///
/// Everything is an allow-list (<see cref="StorageCleanupPolicy.IsAllowedPath"/>),
/// re-checked immediately before each delete — a scan result can go stale while
/// the user reads the list.
/// </summary>
public sealed class StorageCleanupService
{
    private const string LogTag = "Cleanup";
    private readonly Logger _log = Logger.Instance;
    private readonly string _root;

    /// <summary>Defaults to the configured data root; tests pass a temp root.</summary>
    public StorageCleanupService(string? rootDirectory = null)
    {
        _root = rootDirectory ?? LauncherConfig.Instance.RootDirectory;
    }

    public string RootDirectory => _root;

    /// <summary>Runs on a worker thread: the walk touches every directory under
    /// the root (assets alone are tens of thousands of files).</summary>
    public Task<CleanupReport> ScanAsync(CancellationToken ct = default) => Task.Run(() => Scan(ct), ct);

    public Task<CleanupResult> ExecuteAsync(CleanupReport report,
        IReadOnlySet<CleanupCategory> selected, CancellationToken ct = default)
        => Task.Run(() => Execute(report, selected, ct), ct);

    // ------------------------------------------------------------------ scan

    public CleanupReport Scan(CancellationToken ct = default)
    {
        var groups = new List<CleanupGroup>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nowUtc = DateTime.UtcNow;

        void Collect(CleanupCategory category, IEnumerable<CleanupItem> items)
        {
            var list = items.Where(i => claimed.Add(i.Path)).ToList();
            if (list.Count == 0) return;
            groups.Add(new CleanupGroup
            {
                Category = category,
                Info = StorageCleanupPolicy.Describe(category),
                Items = list
            });
        }

        if (string.IsNullOrWhiteSpace(_root) || !Directory.Exists(_root))
            return new CleanupReport { RootDirectory = _root };

        var cache = Path.Combine(_root, "cache");
        var update = Path.Combine(_root, "update");
        var logs = Path.Combine(_root, "logs");
        var versions = Path.Combine(_root, "versions");

        Collect(CleanupCategory.CacheIcons, FilesUnder(Path.Combine(cache, "icons"), CleanupCategory.CacheIcons, ct));
        Collect(CleanupCategory.CacheOptiFine, FilesUnder(Path.Combine(cache, "optifine"), CleanupCategory.CacheOptiFine, ct));
        Collect(CleanupCategory.ManifestCache, SingleFile(ManifestCache.PathFor(_root), CleanupCategory.ManifestCache));

        Collect(CleanupCategory.UpdateLeftover, FilesUnder(update, CleanupCategory.UpdateLeftover, ct)
            .Where(i => !StorageCleanupPolicy.StagedUpdateFileNames.Any(
                n => string.Equals(i.FileName, n, StringComparison.OrdinalIgnoreCase)))
            .Where(i => !ActiveDownloads.IsActive(StripPartSuffix(i.Path))));

        Collect(CleanupCategory.OldLogs, FilesUnder(logs, CleanupCategory.OldLogs, ct)
            .Where(i => StorageCleanupPolicy.ShouldDeleteLog(i.Path, nowUtc)));

        Collect(CleanupCategory.EmptyVersionDirs, EmptyVersionShells(versions, ct));

        // Partials: reuse the round-87 classification so the two entry points
        // can never disagree about who owns a .part file.
        var residue = DownloadResidue.Scan(_root);
        var activeParts = residue.ActiveCount;
        Collect(CleanupCategory.OrphanParts, residue.Files
            .Where(f => f.Owner == ResidueOwner.Unowned)
            .Where(f => StorageCleanupPolicy.IsAllowedPath(_root, f.Path))
            .Select(f => new CleanupItem { Category = CleanupCategory.OrphanParts, Path = f.Path, Bytes = f.Bytes }));
        Collect(CleanupCategory.ResumableParts, residue.Files
            .Where(f => f.Owner == ResidueOwner.Journaled)
            .Where(f => StorageCleanupPolicy.IsAllowedPath(_root, f.Path))
            .Select(f => new CleanupItem { Category = CleanupCategory.ResumableParts, Path = f.Path, Bytes = f.Bytes }));

        Collect(CleanupCategory.InstallerMetaCache, FilesUnder(Path.Combine(cache, "installers"), CleanupCategory.InstallerMetaCache, ct));

        // Stable, human order: the table order in StorageCleanupPolicy.All.
        groups.Sort((a, b) => Array.FindIndex(StorageCleanupPolicy.All, i => i.Category == a.Category)
            .CompareTo(Array.FindIndex(StorageCleanupPolicy.All, i => i.Category == b.Category)));

        _log.Info(LogTag,
            $"扫描完成：{groups.Count} 类 / {groups.Sum(g => g.Items.Count)} 项 / " +
            $"{JobProgressPolicy.FormatBytes(groups.Sum(g => g.Bytes))}" +
            (activeParts > 0 ? $"；{activeParts} 个正在下载的残片已跳过" : ""));

        return new CleanupReport
        {
            Groups = groups,
            ActivePartCount = activeParts,
            RootDirectory = _root
        };
    }

    // --------------------------------------------------------------- execute

    public CleanupResult Execute(CleanupReport report, IReadOnlySet<CleanupCategory> selected,
        CancellationToken ct = default)
    {
        if (report == null || selected == null || selected.Count == 0)
            return new CleanupResult();

        var deletedFiles = 0;
        var deletedDirs = 0;
        long freed = 0;
        var failures = new List<string>();
        // Round-88 review: destinations whose .part we removed must also leave the
        // resume journal, otherwise the log keeps lines pointing at files that no
        // longer exist (filtered on replay, but only compacted then).
        var forgotten = new List<string>();

        foreach (var group in report.Groups)
        {
            if (!selected.Contains(group.Category)) continue;

            var groupFiles = 0;
            var groupDirs = 0;
            long groupBytes = 0;

            foreach (var item in group.Items)
            {
                ct.ThrowIfCancellationRequested();
                // Re-validate: the list the user clicked on may be minutes old.
                if (!StorageCleanupPolicy.IsAllowedPath(_root, item.Path))
                {
                    failures.Add($"{item.FileName}: 路径不在允许范围内");
                    continue;
                }

                try
                {
                    if (item.IsDirectory)
                    {
                        var id = Path.GetFileName(item.Path.TrimEnd('\\', '/'));
                        if (!StorageCleanupPolicy.IsEmptyVersionShell(item.Path, id))
                        {
                            failures.Add($"{item.FileName}: 目录里已有文件，跳过");
                            continue;
                        }
                        Directory.Delete(item.Path, recursive: true);
                        deletedDirs++;
                        groupDirs++;
                        continue;
                    }

                    // A job may have started on this very file while the user
                    // was reading the list — the report is a snapshot, the
                    // ownership map is live.
                    if (item.Path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                        && ActiveDownloads.IsActive(StripPartSuffix(item.Path)))
                    {
                        failures.Add($"{item.FileName}: 正在下载，跳过");
                        continue;
                    }

                    if (!File.Exists(item.Path)) continue; // already gone
                    var size = new FileInfo(item.Path).Length;
                    File.Delete(item.Path);
                    deletedFiles++;
                    groupFiles++;
                    freed += size;
                    groupBytes += size;
                    if (group.Category == CleanupCategory.ResumableParts)
                        forgotten.Add(StripPartSuffix(item.Path));
                }
                catch (Exception ex)
                {
                    failures.Add($"{item.FileName}: {ex.Message}");
                }
            }

            if (groupFiles + groupDirs > 0)
                _log.Info(LogTag,
                    $"清理 {group.Info.DisplayName}：{groupFiles} 个文件" +
                    (groupDirs > 0 ? $" + {groupDirs} 个空目录" : "") +
                    $" / {JobProgressPolicy.FormatBytes(groupBytes)}");
        }

        if (forgotten.Count > 0)
        {
            DownloadJournal.Forget(forgotten);
            _log.Info(LogTag, $"同时从断点续传台账里移除 {forgotten.Count} 条记录（对应文件已删除）");
        }

        var result = new CleanupResult
        {
            DeletedFiles = deletedFiles,
            DeletedDirs = deletedDirs,
            FreedBytes = freed,
            Failures = failures
        };
        _log.Info(LogTag, "清理结束：" + result.Describe());
        return result;
    }

    // --------------------------------------------------------------- helpers

    private IEnumerable<CleanupItem> FilesUnder(string directory, CleanupCategory category, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) yield break;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            });
        }
        catch { yield break; }

        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            // The allow-list is the authority, even for a directory walk we
            // started ourselves — it also keeps the staged update package out.
            if (!StorageCleanupPolicy.IsAllowedPath(_root, path)) continue;
            long size = 0;
            try { size = new FileInfo(path).Length; } catch { }
            yield return new CleanupItem { Category = category, Path = path, Bytes = size };
        }
    }

    private static IEnumerable<CleanupItem> SingleFile(string path, CleanupCategory category)
    {
        try
        {
            if (!File.Exists(path)) return Array.Empty<CleanupItem>();
            return new[]
            {
                new CleanupItem { Category = category, Path = path, Bytes = new FileInfo(path).Length }
            };
        }
        catch { return Array.Empty<CleanupItem>(); }
    }

    private IEnumerable<CleanupItem> EmptyVersionShells(string versionsRoot, CancellationToken ct)
    {
        if (!Directory.Exists(versionsRoot)) yield break;
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(versionsRoot); }
        catch { yield break; }

        foreach (var dir in dirs)
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileName(dir);
            if (!StorageCleanupPolicy.IsEmptyVersionShell(dir, id)) continue;
            if (!StorageCleanupPolicy.IsAllowedPath(_root, dir)) continue;
            yield return new CleanupItem
            {
                Category = CleanupCategory.EmptyVersionDirs,
                Path = dir,
                IsDirectory = true
            };
        }
    }

    private static string StripPartSuffix(string path)
        => path.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ? path[..^".part".Length] : path;
}
