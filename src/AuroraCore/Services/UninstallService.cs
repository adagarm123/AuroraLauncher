using System.Diagnostics;
using System.Runtime.InteropServices;
using AuroraCore.Utils;
using Microsoft.VisualBasic.FileIO;

namespace AuroraCore.Services;

public sealed record UninstallResult(
    int Removed, int Failed, IReadOnlyList<string> Failures, bool ExeDeferred,
    int RegistryRemoved = 0, int RegistryFailed = 0,
    bool CleanupHelperStarted = false, string HelperError = "");

/// <summary>Executes a vetted <see cref="UninstallPlan"/>. Files go to the
/// Recycle Bin (recoverable). Since round-102 the running EXE is removed by a
/// short-lived helper copy as soon as the launcher exits — no more "restart
/// the PC to finish uninstalling". Registry entries are removed only from the
/// strict allowlist in <see cref="UninstallRegistryPolicy"/>.</summary>
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

    public static UninstallResult Execute(UninstallPlan plan, bool removeRegistry = false,
        bool tryDeferExe = true)
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

        var registryRemoved = 0;
        var registryFailed = 0;
        if (removeRegistry)
        {
            var targets = UninstallRegistryService.Scan(plan.ExePath ?? "");
            var reg = UninstallRegistryService.Remove(targets);
            registryRemoved = reg.Removed;
            registryFailed = reg.Failed;
            failures.AddRange(reg.Failures);
        }

        // Fallback only: the normal path is PrepareAndStartHelper below. This
        // keeps older callers safe if they forget to start the helper.
        var deferred = false;
        if (tryDeferExe) deferred = DeferExeUntilReboot(plan.ExePath);

        return new UninstallResult(removed, failed, failures, deferred,
            registryRemoved, registryFailed);
    }

    /// <summary>Copies the launcher to a temporary helper and hands it a
    /// manifest of what to finish after we exit. NEVER touches files that are
    /// not in the already-approved uninstall plan.</summary>
    public static (bool Started, string Error) PrepareAndStartHelper(UninstallPlan plan)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return (false, "无法定位启动器 EXE");

            var temp = Path.Combine(Path.GetTempPath(), "aurora-uninstall-" + Guid.NewGuid().ToString("N"));
            var helper = temp + ".exe";
            File.Copy(exe, helper, overwrite: true);

            var manifest = new UninstallCleanupManifest
            {
                ParentPid = Environment.ProcessId,
                ExePath = string.IsNullOrEmpty(plan.ExePath) ? exe : plan.ExePath!,
                Root = plan.Root
            };
            foreach (var t in plan.Targets)
                if (File.Exists(t.Path) || Directory.Exists(t.Path))
                    manifest.Paths.Add(t.Path);
            manifest.HelperPath = helper;
            var manifestPath = temp + ".json";
            manifest.Save(manifestPath);

            var psi = new ProcessStartInfo(helper, UninstallCleanupArgs.Build(manifestPath))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath()
            };
            using var p = Process.Start(psi);
            if (p == null) return (false, "清理进程未能启动");
            Logger.Instance.Info("Uninstall", "已启动临时清理进程，等待启动器退出后删除本体: " + helper);
            return (true, "");
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn("Uninstall", "启动清理进程失败: " + ex.Message);
            return (false, ex.Message);
        }
    }

    /// <summary>Headless mode of the helper copy. Re-validates every path, so
    /// a modified manifest cannot delete anything outside the allowlist.</summary>
    public static async Task RunCleanupHelperAsync(string manifestPath)
    {
        var manifest = UninstallCleanupManifest.Load(manifestPath);
        if (manifest == null || !UninstallCleanupArgs.IsPlausibleExePath(manifest.ExePath) ||
            !UninstallCleanupArgs.IsPlausibleHelperPath(manifest.HelperPath) ||
            !CacheFolderPolicy.IsSafeRoot(manifest.Root))
        {
            // A malformed / tampered manifest must never turn the helper into
            // an arbitrary file deleter. Only drop the trusted copy itself.
            Logger.Instance.Warn("Uninstall", "清理清单未通过校验，拒绝删除任何文件: " + manifestPath);
            TryScheduleSelfDelete(Environment.ProcessPath);
            return;
        }

        try
        {
            using (var parent = await WaitForParentAsync(manifest.ParentPid).ConfigureAwait(false)) { }
        }
        catch { /* parent already gone */ }

        foreach (var path in manifest.Paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!IsCleanupPathAllowed(path, manifest.Root)) continue;
            for (var i = 0; i < 5; i++)
            {
                try
                {
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    else if (File.Exists(path)) File.Delete(path);
                    else break;
                    if (!File.Exists(path) && !Directory.Exists(path)) break;
                }
                catch { await Task.Delay(200).ConfigureAwait(false); }
            }
        }

        for (var i = 0; i < 10 && File.Exists(manifest.ExePath); i++)
        {
            try { File.Delete(manifest.ExePath); }
            catch { await Task.Delay(200).ConfigureAwait(false); }
        }
        Logger.Instance.Flush(1000);
        try { if (File.Exists(manifestPath)) File.Delete(manifestPath); } catch { }
        TryScheduleSelfDelete(manifest.HelperPath);
    }

    private static async Task<Process?> WaitForParentAsync(int pid)
    {
        if (pid <= 0) return null;
        Process? parent = null;
        try { parent = Process.GetProcessById(pid); }
        catch { return null; }
        for (var i = 0; i < 100; i++)
        {
            if (parent.HasExited) return parent;
            await Task.Delay(150).ConfigureAwait(false);
        }
        return parent;
    }

    /// <summary>Last-resort fallback when the helper could not be started.</summary>
    public static bool DeferExeUntilReboot(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return false;
        try { File.Delete(exePath); } catch { /* still mapped */ }
        return File.Exists(exePath) && MoveFileEx(exePath, null, MovefileDelayUntilReboot);
    }

    public static bool IsCleanupPathAllowed(string path, string root)
    {
        try
        {
            if (!CacheFolderPolicy.IsSafeRoot(root)) return false;
            var full = Path.GetFullPath(path);
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return false;
            var name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar));
            return Directory.Exists(full)
                ? UninstallPolicy.IsOwnedDirectory(name, true, true)
                : UninstallPolicy.IsOwnedFile(name);
        }
        catch { return false; }
    }

    private static void TryScheduleSelfDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { File.Delete(path); } catch { }
        if (File.Exists(path)) MoveFileEx(path, null, MovefileDelayUntilReboot);
    }
}
