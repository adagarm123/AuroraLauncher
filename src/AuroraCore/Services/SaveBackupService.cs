using System.IO;
using System.IO.Compression;

namespace AuroraCore.Services;

/// <summary>When to snapshot <c>saves/</c>. Round-79.</summary>
public enum SaveBackupMode
{
    Off,
    BeforeLaunch,
    AfterExit,
    Both
}

/// <summary>Why a snapshot was taken — also encoded in the file name so a user
/// browsing the backup folder can tell them apart.</summary>
public enum BackupTrigger { BeforeLaunch, AfterExit, Manual }

/// <summary>
/// Round-79: the "hand slipped" insurance policy. Two hard rules drive every
/// decision here:
///  1. <b>Never touch the saves folder.</b> We only READ it and write zips
///     somewhere else. A backup feature that can itself lose saves is worse
///     than none.
///  2. <b>Never surprise the disk.</b> Keep the newest N, and refuse to snapshot
///     an absurdly large folder instead of filling the drive.
/// Pure logic (no IO) so every rule is unit-testable.
/// </summary>
public static class SaveBackupPolicy
{
    public const int DefaultKeep = 5;
    public const int MaxKeep = 20;

    /// <summary>Above this the automatic snapshot is skipped (a 10 GB world
    /// zipped before every launch is a bug, not a feature). Manual backup is
    /// still allowed — the user asked for it.</summary>
    public const long MaxAutoSourceBytes = 3L * 1024 * 1024 * 1024;

    public static bool ShouldRun(SaveBackupMode mode, BackupTrigger trigger) => trigger switch
    {
        BackupTrigger.BeforeLaunch => mode is SaveBackupMode.BeforeLaunch or SaveBackupMode.Both,
        BackupTrigger.AfterExit => mode is SaveBackupMode.AfterExit or SaveBackupMode.Both,
        BackupTrigger.Manual => true,
        _ => false
    };

    public static int ClampKeep(int keep) => Math.Clamp(keep <= 0 ? DefaultKeep : keep, 1, MaxKeep);

    /// <summary>"saves-&lt;instance&gt;-&lt;time&gt;-&lt;trigger&gt;.zip" — sortable by
    /// name and greppable by instance, which matters when a user opens the
    /// folder six months later.</summary>
    public static string BuildFileName(DateTime now, BackupTrigger trigger, string instanceName)
    {
        var tag = trigger switch
        {
            BackupTrigger.BeforeLaunch => "before",
            BackupTrigger.AfterExit => "after",
            _ => "manual"
        };
        return $"saves-{SafeName(instanceName)}-{now:yyyyMMdd-HHmmss}-{tag}.zip";
    }

    /// <summary>Instance names are user text: strip path separators and
    /// characters Windows rejects, cap the length, and never return empty.</summary>
    public static string SafeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "instance";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray();
        var text = new string(chars).Trim('_', '.');
        if (text.Length == 0) return "instance";
        return text.Length <= 24 ? text : text[..24];
    }

    /// <summary>Which backups to delete: everything older than the newest
    /// <paramref name="keep"/>. Only files this feature created qualify —
    /// a user's own zip dropped in the folder must survive.</summary>
    public static IReadOnlyList<string> PlanPrune(IEnumerable<(string Path, DateTime CreatedUtc)> backups, int keep)
    {
        var n = ClampKeep(keep);
        return backups
            .Where(b => LooksLikeBackup(b.Path))
            .OrderByDescending(b => b.CreatedUtc)
            .ThenByDescending(b => b.Path, StringComparer.OrdinalIgnoreCase)
            .Skip(n)
            .Select(b => b.Path)
            .ToList();
    }

    public static bool LooksLikeBackup(string path)
    {
        var file = Path.GetFileName(path);
        return file.StartsWith("saves-", StringComparison.OrdinalIgnoreCase) &&
               file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }

    public static string DescribeMode(SaveBackupMode mode) => mode switch
    {
        SaveBackupMode.BeforeLaunch => "启动前备份",
        SaveBackupMode.AfterExit => "退出后备份",
        SaveBackupMode.Both => "启动前 + 退出后",
        _ => "关闭"
    };

    public static SaveBackupMode ParseMode(string? display) => display switch
    {
        "启动前备份" => SaveBackupMode.BeforeLaunch,
        "退出后备份" => SaveBackupMode.AfterExit,
        "启动前 + 退出后" => SaveBackupMode.Both,
        _ => SaveBackupMode.Off
    };

    /// <summary>Why the automatic snapshot was skipped, in user words. Returns
    /// null when everything looks fine.</summary>
    public static string? SkipReason(long fileCount, long totalBytes, long maxBytes = MaxAutoSourceBytes)
    {
        if (fileCount <= 0) return "存档文件夹是空的，先玩一次再备份";
        if (maxBytes > 0 && totalBytes > maxBytes)
            return $"存档占了 {totalBytes / 1024 / 1024} MB，超过自动备份上限（{maxBytes / 1024 / 1024} MB），已跳过";
        return null;
    }
}

/// <summary>Result of one snapshot attempt. Never throws to the caller: a
/// backup failure must not block launching the game or closing the launcher.</summary>
public sealed record SaveBackupResult(bool Ok, string Message, string? Path = null, long Bytes = 0, int Pruned = 0)
{
    public static SaveBackupResult Fail(string why) => new(false, why);
}

/// <summary>
/// Round-79: zips <c>instances/&lt;id&gt;/saves</c> into
/// <c>backups/&lt;id&gt;/</c> with a bounded retention. Reads the saves folder,
/// never writes into it; writes to a .tmp file first so a killed process can
/// never leave a half zip that looks like a good backup.
/// </summary>
public static class SaveBackupService
{
    public static string BackupRootOf(string rootDir, string instanceId)
        => Path.Combine(rootDir, "backups", instanceId);

    public static string SavesOf(string instanceDirectory) => Path.Combine(instanceDirectory, "saves");

    public static async Task<SaveBackupResult> CreateAsync(
        string savesDir, string backupDir, string instanceName, BackupTrigger trigger, int keep,
        CancellationToken ct = default)
    {
        string? tmp = null;
        try
        {
            if (!Directory.Exists(savesDir))
                return SaveBackupResult.Fail("还没有存档文件夹（进游戏创建世界后才会出现）");

            var (count, bytes) = await Task.Run(() => Measure(savesDir), ct).ConfigureAwait(false);
            var skip = SaveBackupPolicy.SkipReason(count, bytes,
                trigger == BackupTrigger.Manual ? 0 : SaveBackupPolicy.MaxAutoSourceBytes);
            if (skip != null) return SaveBackupResult.Fail(skip);

            Directory.CreateDirectory(backupDir);
            var name = SaveBackupPolicy.BuildFileName(DateTime.Now, trigger, instanceName);
            var dest = Path.Combine(backupDir, name);
            tmp = dest + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);

            await Task.Run(() =>
            {
                ZipFile.CreateFromDirectory(savesDir, tmp, CompressionLevel.Fastest,
                    includeBaseDirectory: false);
            }, ct).ConfigureAwait(false);

            File.Move(tmp, dest, overwrite: true);
            tmp = null;

            var created = new FileInfo(dest);
            var pruned = await Task.Run(() => Prune(backupDir, keep), ct).ConfigureAwait(false);
            var message = $"已备份存档（{created.Length / 1024} KB）" +
                          (pruned > 0 ? $"，并清理了 {pruned} 个更早的备份" : "");
            return new SaveBackupResult(true, message, dest, created.Length, pruned);
        }
        catch (OperationCanceledException)
        {
            return SaveBackupResult.Fail("备份已取消");
        }
        catch (Exception ex)
        {
            // Locked file / no permission / disk full: report, never crash.
            return SaveBackupResult.Fail("备份失败：" + ex.Message);
        }
        finally
        {
            try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>Newest first; the instance page shows the top one.</summary>
    public static IReadOnlyList<(string Path, DateTime CreatedUtc, long Bytes)> List(string backupDir)
    {
        try
        {
            if (!Directory.Exists(backupDir)) return Array.Empty<(string, DateTime, long)>();
            return Directory.EnumerateFiles(backupDir, "saves-*.zip", SearchOption.TopDirectoryOnly)
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => (f.FullName, f.LastWriteTimeUtc, f.Length))
                .ToList();
        }
        catch
        {
            return Array.Empty<(string, DateTime, long)>();
        }
    }

    public static string? Latest(string backupDir) => List(backupDir).FirstOrDefault().Path;

    private static (long Count, long Bytes) Measure(string dir)
    {
        long count = 0, bytes = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            count++;
            try { bytes += new FileInfo(f).Length; } catch { }
        }
        return (count, bytes);
    }

    private static int Prune(string backupDir, int keep)
    {
        var all = Directory.EnumerateFiles(backupDir, "*.zip", SearchOption.TopDirectoryOnly)
            .Select(p => new FileInfo(p))
            .Select(f => (f.FullName, f.LastWriteTimeUtc))
            .ToList();
        var doomed = SaveBackupPolicy.PlanPrune(all, keep);
        var removed = 0;
        foreach (var path in doomed)
        {
            try { File.Delete(path); removed++; } catch { }
        }
        return removed;
    }
}
