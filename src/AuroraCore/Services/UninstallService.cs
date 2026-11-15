using System.Runtime.InteropServices;
using AuroraCore.Utils;
using Microsoft.VisualBasic.FileIO;

namespace AuroraCore.Services;

public sealed record UninstallResult(
    int Removed, int Failed, IReadOnlyList<string> Failures, bool ExeDeferred);

/// <summary>Executes a vetted <see cref="UninstallPlan"/>. Files go to the
/// Recycle Bin (recoverable) rather than being obliterated; only the running
/// EXE needs the OS-level delayed delete because Windows keeps it mapped.
/// Nothing outside the plan is ever touched.</summary>
public static class UninstallService
{
    private const int MovefileDelayUntilReboot = 0x00000004;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string existing, string? replacement, int flags);

    public static IReadOnlyList<string> EnumerateEntries(string root, string? exePath, bool isPortable)
    {
        var list = new List<string>();
        if (CacheFolderPolicy.IsSafeRoot(root) && Directory.Exists(root))
        {
            try { list.AddRange(Directory.EnumerateFileSystemEntries(root)); }
            catch (Exception ex) { Logger.Instance.Warn("Uninstall", "枚举数据目录失败: " + ex.Message); }
        }
        if (isPortable && !string.IsNullOrEmpty(exePath))
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(exePath));
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    foreach (var p in UninstallPolicy.PortableExtraFiles(dir))
                        if (File.Exists(p) && !list.Contains(p, StringComparer.OrdinalIgnoreCase))
                            list.Add(p);
            }
            catch { /* best effort */ }
        }
        return list;
    }

    public static UninstallResult Execute(UninstallPlan plan)
    {
        var removed = 0;
        var failed = 0;
        var failures = new List<string>();

        foreach (var t in plan.Targets)
        {
            try
            {
                if (t.IsDirectory && Directory.Exists(t.Path))
                    FileSystem.DeleteDirectory(t.Path, UIOption.OnlyErrorDialogs,
                        RecycleOption.SendToRecycleBin, UICancelOption.DoNothing);
                else if (!t.IsDirectory && File.Exists(t.Path))
                    FileSystem.DeleteFile(t.Path, UIOption.OnlyErrorDialogs,
                        RecycleOption.SendToRecycleBin, UICancelOption.DoNothing);
                else continue;

                Logger.Instance.Info("Uninstall", "已移入回收站: " + t.Path + " (" + t.Reason + ")");
                removed++;
            }
            catch (Exception ex)
            {
                failed++;
                failures.Add(t.Path + "：" + ex.Message);
                Logger.Instance.Warn("Uninstall", "删除失败: " + t.Path + " -> " + ex.Message);
            }
        }

        // Remove the now-empty data root itself, never a non-empty one.
        try
        {
            if (Directory.Exists(plan.Root) && !Directory.EnumerateFileSystemEntries(plan.Root).Any())
                Directory.Delete(plan.Root, recursive: false);
        }
        catch { /* keep the empty folder, harmless */ }

        var deferred = false;
        if (!string.IsNullOrEmpty(plan.ExePath) && File.Exists(plan.ExePath))
        {
            try { File.Delete(plan.ExePath); }
            catch { /* running EXE is locked — expected */ }
            if (File.Exists(plan.ExePath))
                deferred = MoveFileEx(plan.ExePath, null, MovefileDelayUntilReboot);
        }

        return new UninstallResult(removed, failed, failures, deferred);
    }
}
