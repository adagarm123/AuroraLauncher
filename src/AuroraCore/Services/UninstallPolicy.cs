namespace AuroraCore.Services;

public sealed record UninstallTarget(string Path, bool IsDirectory, string Reason);

public sealed record UninstallRequest(
    string Root, string? ExePath, bool IsPortable,
    bool DeleteSaves, bool DeleteJava,
    IReadOnlyList<string> ExistingEntries);

public sealed record UninstallPlan(
    string Root, string? ExePath,
    IReadOnlyList<UninstallTarget> Targets,
    IReadOnlyList<string> Kept,
    bool DeleteSaves, bool DeleteJava)
{
    public long TotalBytesApprox => 0; // computed by the service, never guessed here
}

/// <summary>
/// Round-99: one-click uninstall. This policy is deliberately FAIL-CLOSED:
/// only explicitly allowlisted launcher-owned entries inside the data root
/// may be deleted. Anything unknown is KEPT, never guessed at. The user is
/// asked separately about saves and Java, both defaulting to "keep".
/// No wildcards over personal folders, no recursive deletion of a user dir.
/// </summary>
public static class UninstallPolicy
{
    public const string ConfirmationPhrase = "我明白此操作不可恢复";

    private static readonly string[] OwnedDirectories =
    {
        "versions", "libraries", "assets", "cache", "logs", "update", "playtime", "backups"
    };

    public static bool IsOwnedDirectory(string name, bool deleteSaves, bool deleteJava)
    {
        if (name.Equals("instances", StringComparison.OrdinalIgnoreCase)) return deleteSaves;
        if (name.Equals("backups", StringComparison.OrdinalIgnoreCase)) return deleteSaves;
        if (name.Equals("java", StringComparison.OrdinalIgnoreCase)) return deleteJava;
        return OwnedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsOwnedFile(string name)
    {
        if (name.Equals("config.json", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("config.json.bak-", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("accounts.json", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("instances.json", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("launcher_profiles.json", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("download-journal.log", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("forge-", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith("-installer.jar.log", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("portable.txt", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("latest.json", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("build-info.txt", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("patch-info.txt", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals(".last-round", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("AuroraLauncher.patch", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("AuroraLauncher.pdb", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("AuroraCore.pdb", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static UninstallPlan BuildPlan(UninstallRequest r)
    {
        if (!CacheFolderPolicy.IsSafeRoot(r.Root))
            return new UninstallPlan(r.Root, r.ExePath, Array.Empty<UninstallTarget>(),
                r.ExistingEntries.ToList(), r.DeleteSaves, r.DeleteJava);

        var targets = new List<UninstallTarget>();
        var kept = new List<string>();
        var exeFull = string.IsNullOrEmpty(r.ExePath) ? "" : Path.GetFullPath(r.ExePath);
        var rootFull = Path.GetFullPath(r.Root);

        foreach (var entry in r.ExistingEntries)
        {
            string full;
            try { full = Path.GetFullPath(entry); }
            catch { kept.Add(entry); continue; }

            // Never touch anything outside the data root (or, in portable
            // mode, outside the EXE folder, which IS the data root).
            var inside = full.StartsWith(rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
            var isRootItself = string.Equals(full, rootFull, StringComparison.OrdinalIgnoreCase);
            if (!inside || isRootItself) { kept.Add(entry); continue; }

            if (string.Equals(full, exeFull, StringComparison.OrdinalIgnoreCase)) continue; // handled separately

            var name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar));
            if (Directory.Exists(full) && IsOwnedDirectory(name, r.DeleteSaves, r.DeleteJava))
                targets.Add(new UninstallTarget(full, true, $"启动器数据目录 {name}"));
            else if (File.Exists(full) && IsOwnedFile(name))
                targets.Add(new UninstallTarget(full, false, $"启动器文件 {name}"));
            else
                kept.Add(entry);
        }

        return new UninstallPlan(rootFull, exeFull, targets, kept, r.DeleteSaves, r.DeleteJava);
    }

    /// <summary>Files that live beside the EXE only in portable mode.</summary>
    public static IReadOnlyList<string> PortableExtraFiles(string exeDir) =>
        new[] { "portable.txt", "config.json", "latest.json", "build-info.txt",
                "AuroraLauncher.pdb", "AuroraCore.pdb", ".last-round" }
            .Select(n => Path.Combine(exeDir, n)).ToArray();

    public static string Warning(UninstallPlan plan)
    {
        var keeps = new List<string>();
        if (!plan.DeleteSaves) keeps.Add("游戏存档与实例（instances/、backups/）");
        if (!plan.DeleteJava) keeps.Add("启动器管理的 Java（java/）");
        return $"即将移除 {plan.Targets.Count} 项启动器文件/目录。\n\n" +
               (keeps.Count > 0 ? "将保留：" + string.Join("、", keeps) + "\n\n" : "存档与 Java 也会一并删除。\n\n") +
               "文件会先尝试移入回收站；程序文件本身需要重启后才会消失。\n" +
               "任何不属于启动器的文件都不会被删除。";
    }
}
