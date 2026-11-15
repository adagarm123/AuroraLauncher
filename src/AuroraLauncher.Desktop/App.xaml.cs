using System.Windows;
using AuroraCore;
using AuroraCore.Services;
using AuroraLauncher.Desktop.Services;
using AuroraLauncher.Desktop.Views;

namespace AuroraLauncher.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>Round-61: one binary, two modes. Started with
    /// <c>--apply-update|...</c> it is the updater HOST — it must never build
    /// MainWindow (that would re-trigger the swap) and instead shows a small
    /// borderless progress window until the new launcher is running. All
    /// other launches show the normal main window. StartupUri was removed so
    /// this decision happens before WPF auto-creates a window.</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (UpdateApplyArgs.TryParse(e.Args, out var plan) && plan != null)
        {
            var host = new UpdateApplyingWindow(plan);
            MainWindow = host;
            host.Show();
            return;
        }

        // Round-74: one launcher per data root. Two copies would fight over
        // config.json and instances/ (the second instance's Save() can drop the
        // first one's edits). The second process asks the first to come forward
        // and exits — silently, so double-clicking the EXE never "does nothing".
        var dataRoot = LauncherConfig.Instance.RootDirectory;
        // Log file first: the single-instance path below exits before any service
        // is constructed, and that decision deserves a line in the file log too.
        try { AuroraCore.Utils.Logger.Instance.Configure(LauncherConfig.Instance.LogsDir); } catch { }
        // Round-84: the heal happened while Load() ran (before the log file was
        // known), so report it here — the user needs to know why their instance
        // list came back / why the path changed.
        if (LauncherConfig.Instance.RootHealedFrom is { Length: > 0 } healedFrom)
            AuroraCore.Utils.Logger.Instance.Warn("Config",
                $"存储目录曾指向临时文件夹（{healedFrom}），已自动恢复到 {dataRoot}；原有实例按新目录重新读取");
        if (LauncherConfig.Instance.IsPortableMode)
            AuroraCore.Utils.Logger.Instance.Info("Config", "便携模式：配置与数据都放在程序目录 " + dataRoot);
        if (!SingleInstanceGuard.TryAcquire(dataRoot, ActivateExistingWindow))
        {
            // Round-75: only exit when the existing instance PROVES it is alive
            // (ack). A process that owns the mutex but has no window to show would
            // otherwise swallow every double-click — reported by the user as
            // "文件夹内双击无法打开应用程序".
            if (SingleInstanceGuard.SignalExisting(dataRoot, TimeSpan.FromSeconds(2)))
            {
                Shutdown();
                return;
            }
            AuroraCore.Utils.Logger.Instance.Warn("App", "已有实例未响应，继续启动新窗口（单实例保护降级）。");
        }

        UpdateService.CleanupUpdaterHost();
        // The updater host that launched us exits a moment AFTER we start,
        // so its own temporary copy is still locked right now. Sweep again
        // shortly after startup instead of leaving a 63 MB file behind.
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000).ConfigureAwait(false);
            UpdateService.CleanupUpdaterHost();
        });
        var main = new MainWindow();
        _shell = main;
        MainWindow = main;
        main.Show();
    }

    /// <summary>The only window this app owns, captured when it is created.
    /// Round-76: the activation listener runs on a BACKGROUND thread, and
    /// <c>Application.MainWindow</c> verifies thread affinity — reading it there
    /// threw "调用线程无法访问此对象", so no window was ever restored while the
    /// second instance happily exited. A plain field has no affinity.</summary>
    private AuroraLauncher.Desktop.MainWindow? _shell;

    /// <summary>Round-74/76: a second EXE launch (or a tray double-click) restores
    /// the window — including from a hidden-to-tray state. Returns true only when a
    /// window is actually being brought up; that boolean becomes the ack.</summary>
    private bool ActivateExistingWindow()
    {
        var shell = _shell;
        if (shell == null) return false;
        return shell.RestoreFromTray();
    }

    /// <summary>Round-69: file logging is asynchronous (batched on a background
    /// thread), so the last lines must be flushed before the process dies.</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        try { AuroraCore.Download.DownloadJournal.Flush(800); } catch { }
        try { AuroraCore.Utils.Logger.Instance.Flush(1500); } catch { }
        // Round-76: release the single-instance lock here instead of relying on
        // process teardown — one lingering second is one blocked double-click.
        SingleInstanceGuard.Release();
        base.OnExit(e);
    }
}
