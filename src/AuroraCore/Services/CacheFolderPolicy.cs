namespace AuroraCore.Services;

/// <summary>One cache location the launcher owns. Relative paths only — the
/// data root is supplied at scan time, so this stays pure and testable.</summary>
public sealed record CacheFolderSpec(
    string Key, string RelativePath, string DisplayName, string Description, bool IsDirectory);

/// <summary>
/// Round-99: the settings page now SHOWS every cache folder instead of only
/// offering a "clean" button. This is a read-only inventory: it never deletes
/// anything. Keeping the list here (rather than scattered in XAML) means the
/// cleanup categories, the display and the tests all agree on one source.
/// </summary>
public static class CacheFolderPolicy
{
    public static readonly IReadOnlyList<CacheFolderSpec> Folders = new[]
    {
        new CacheFolderSpec("icons", "cache/icons", "项目图标缓存",
            "内容中心浏览过的模组 / 整合包图标，删了下次会重新下载。", true),
        new CacheFolderSpec("installers", "cache/installers", "加载器安装器缓存",
            "Forge / Fabric 安装器与版本清单，清理后断网时可能无法离线安装。", true),
        new CacheFolderSpec("manifest", "cache/version_manifest_v2.json", "版本清单离线缓存",
            "上次成功获取的 Mojang 版本列表（约 270 KB）。", false),
        new CacheFolderSpec("journal", "cache/download-journal.log", "断点续传台账",
            "记录每个 .part 对应的下载，清理残片时必须同步更新。", false),
        new CacheFolderSpec("update", "update", "更新包暂存",
            "已下载、等待下次启动换装的更新 EXE 与补丁。", true),
        new CacheFolderSpec("logs", "logs", "运行日志",
            "启动器自己的日志；导出诊断时也会用到。", true),
    };

    public static string FullPath(string root, CacheFolderSpec spec)
        => Path.Combine(root, spec.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{(long)v} B" : $"{v:0.0} {units[i]}";
    }

    /// <summary>Shared by the cache viewer and the uninstaller: a data root
    /// must never be a drive root, a system folder or a personal folder root.
    /// The uninstaller uses this to fail closed before deleting anything.</summary>
    public static bool IsSafeRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        string full;
        try { full = Path.GetFullPath(root); }
        catch { return false; }
        if (full.Length < 4 || Path.GetPathRoot(full)?.TrimEnd('\\', '/') == full.TrimEnd('\\', '/')) return false;

        var segments = full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return false;
        var lower = segments.Select(s => s.ToLowerInvariant()).ToArray();
        string[] forbidden =
        {
            "windows", "program files", "program files (x86)", "system32",
            "users", "desktop", "documents", "downloads", "appdata", "$recycle.bin"
        };
        // A user's own Desktop/Documents/Downloads are never acceptable ROOTS.
        if (lower.Any(s => forbidden.Contains(s)) &&
            !lower.Any(s => s.Contains("aurora", StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
    }
}
