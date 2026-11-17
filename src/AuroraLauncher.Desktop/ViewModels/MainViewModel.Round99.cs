using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using AuroraCore;
using AuroraCore.Services;
using AuroraCore.Utils;
using AuroraLauncher.Desktop.Views;

namespace AuroraLauncher.Desktop.ViewModels;

public partial class MainViewModel
{
    // ==================== 缓存文件夹展示 ====================
    public ObservableCollection<CacheFolderVM> CacheFolders { get; } = new();

    private string _cacheFoldersTotalText = "尚未扫描";
    public string CacheFoldersTotalText
    {
        get => _cacheFoldersTotalText;
        private set => SetProperty(ref _cacheFoldersTotalText, value);
    }

    public void RefreshCacheFolders()
    {
        var root = LauncherConfig.Instance.RootDirectory;
        var items = CacheFolderService.Scan(root);
        CacheFolders.Clear();
        foreach (var i in items) CacheFolders.Add(new CacheFolderVM(i));
        CacheFoldersTotalText = $"共 {items.Count} 处 · 合计 {CacheFolderService.TotalText(items)}";
        Status = "缓存文件夹已刷新";
    }

    public void OpenCacheFolder(object? parameter)
    {
        var path = parameter as string;
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path)) path = Path.GetDirectoryName(path) ?? path;
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex) { Status = "无法打开缓存目录: " + ex.Message; }
    }

    // ==================== 首次启动引导：便携模式 ====================
    private bool _firstRunPortable;
    public bool FirstRunPortable
    {
        get => _firstRunPortable;
        set => SetProperty(ref _firstRunPortable, value);
    }

    private string _firstRunPortableNote = "";
    public string FirstRunPortableNote
    {
        get => _firstRunPortableNote;
        private set => SetProperty(ref _firstRunPortableNote, value);
    }

    public string FirstRunPortableHint =>
        "勾选后会在启动器 EXE 所在文件夹创建 portable.txt；重启后配置、实例、缓存、Java 全部存到该文件夹，适合 U 盘 / 移动硬盘。" +
        "已有数据不会自动搬移，只改变以后的写入位置。";

    /// <summary>Step 1 → 2. Applies the portable choice before moving on, and
    /// carries the result into step 2 so the user actually sees whether it
    /// worked (the wizard never silently drops a choice).</summary>
    public void OnFirstRunNext()
    {
        // Round-101: this command now belongs to the DEDICATED storage step
        // (step 2/4), which is before the first download. The Java step uses
        // FirstRunJavaNextCommand and no longer applies anything.
        var choice = FirstRunStoragePolicy.FromPortable(FirstRunPortable);
        if (FirstRunStoragePolicy.ShouldCreateMarker(choice, IsPortableMode))
        {
            var (ok, message) = EnablePortableModeCore("首次启动引导");
            FirstRunPortableNote = (ok ? "✓ " : "✗ ") + message;
            if (ok) ShowToast("已启用便携模式，重启启动器后生效");
        }
        else
        {
            FirstRunPortableNote = FirstRunStoragePolicy.Note(false, IsPortableMode,
                Environment.ProcessPath ?? "");
        }
        FirstRunStep = 2;
    }

    /// <summary>Silent, reusable core. The settings-page button keeps its own
    /// confirmation dialog; the wizard applies an explicit checkbox instead.</summary>
    public (bool Ok, string Message) EnablePortableModeCore(string source)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return (false, "无法定位程序路径。");
        try
        {
            var path = PortableModePolicy.CreateMarker(exe, "由" + source + "创建");
            Logger.Instance.Info("Config", "已创建便携模式标记: " + path);
            OnPropertyChanged(nameof(PortableModeText));
            return (true, "已创建 portable.txt，重启启动器后所有文件将存到程序所在目录（已有数据不会自动搬移）。");
        }
        catch (Exception ex)
        {
            return (false, "创建 portable.txt 失败：" + ex.Message +
                    "。如果启动器放在 Program Files 等受保护目录，请换到普通文件夹。");
        }
    }

    // ==================== 一键卸载 ====================
    /// <summary>Shows the full path list, asks about saves and Java, requires
    /// an explicit acknowledgement AND a second confirmation, then deletes
    /// only allowlisted launcher-owned entries. Never touches anything else.</summary>
    public void Uninstall()
    {
        var cfg = LauncherConfig.Instance;
        var root = cfg.RootDirectory;
        var exe = Environment.ProcessPath;
        var portable = cfg.IsPortableMode;

        if (!CacheFolderPolicy.IsSafeRoot(root))
        {
            System.Windows.MessageBox.Show(
                Application.Current.MainWindow,
                $"当前存储目录不安全，已取消卸载：\n{root}\n\n请把存储目录改到启动器自己的文件夹后再试。",
                "卸载已取消", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var entries = UninstallService.EnumerateEntries(root, exe, portable);
        var dlg = new UninstallWindow(root, exe, portable, entries)
        {
            Owner = Application.Current.MainWindow
        };
        if (dlg.ShowDialog() != true) { Status = "已取消卸载"; return; }

        var plan = dlg.ResultPlan;
        var answer = System.Windows.MessageBox.Show(
            Application.Current.MainWindow,
            "再次确认：即将移除 " + plan.Targets.Count + " 项启动器文件。\n\n" +
            UninstallPolicy.Warning(plan) + "\n\n确定继续吗？",
            "最后确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) { Status = "已取消卸载"; return; }

        var result = UninstallService.Execute(plan);
        var msg = $"已移除 {result.Removed} 项，失败 {result.Failed} 项。" +
                  (result.ExeDeferred ? "\n启动器 EXE 会在下次重启后自动删除。" : "") +
                  (result.Failures.Count > 0 ? "\n\n失败明细：\n" + string.Join("\n", result.Failures.Take(5)) : "") +
                  "\n\n被移入回收站的文件可以恢复。启动器即将退出。";
        System.Windows.MessageBox.Show(Application.Current.MainWindow, msg,
            "卸载完成", MessageBoxButton.OK, MessageBoxImage.Information);
        Logger.Instance.Info("Uninstall", msg.Replace("\n", " | "));
        Application.Current.Shutdown();
    }
}
