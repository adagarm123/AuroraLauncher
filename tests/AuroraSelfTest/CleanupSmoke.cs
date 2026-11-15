using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AuroraCore;
using AuroraCore.Services;
using AuroraLauncher.Desktop.Converters;
using AuroraLauncher.Desktop.ViewModels;
using AuroraLauncher.Desktop.Views;

namespace AuroraSelfTest;

/// <summary>
/// Round-88 opt-in smoke: the storage cleaner on the REAL settings page.
///
/// Scan → list → default check states → clean through the bound command → files
/// really gone, user data really intact. The unit test proves the service; this
/// proves the wiring (a wrong property name renders nothing and ships green
/// unit tests — the round-40 lesson).
/// </summary>
internal static class CleanupSmoke
{
    public static int Run()
    {
        var code = 1;
        var thread = new Thread(() =>
        {
            try { code = RunCore(); }
            catch (Exception ex)
            {
                Console.WriteLine("[cleanup-smoke] EXCEPTION: " + ex);
                code = 2;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return code;
    }

    private static int RunCore()
    {
        var bindingErrors = new List<string>();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new Collector(bindingErrors));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;

        var root = Path.Combine(Path.GetTempPath(), "aurora-cleanup-smoke-" + Guid.NewGuid().ToString("N")[..6]);
        LauncherConfig.UseTestRoot(root);
        LauncherConfig.Instance.EnsureDirectories();

        void W(string rel, int bytes)
        {
            var path = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[bytes]);
        }

        // Safe junk + one CostsOffline category + things that must survive.
        W(Path.Combine("cache", "icons", "a.img"), 4000);
        W(Path.Combine("cache", "icons", "b.img"), 6000);
        W(Path.Combine("logs", "aurora-2020-01-01.log"), 3000);
        W(Path.Combine("cache", "installers", "meta.json"), 2500);          // CostsOffline
        W(Path.Combine("instances", "i1", "saves", "world.dat"), 1234);     // never
        Directory.CreateDirectory(Path.Combine(root, "versions", "shell"));

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        LoadResources(app);

        var vm = new MainViewModel();
        vm.SuppressCleanupConfirmForTests = true;
        // The staged package must exist BEFORE the scan but AFTER the VM starts:
        // a staged exe at construction time makes the launcher try to install it
        // (which is correct in production, and not what this smoke is testing).
        W(Path.Combine("update", "AuroraLauncher.new.exe"), 8000);
        var fails = new List<string>();
        void Require(bool ok, string why) { if (!ok) fails.Add(why); }

        var win = new Window
        {
            Width = 1200, Height = 820, ShowActivated = false, WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, Left = -4000, Top = -4000
        };
        var host = new Grid();
        win.Content = host;
        win.Show();

        var settings = new SettingsView { DataContext = vm };
        host.Children.Add(settings);
        Pump(win);

        var buttons = Buttons(settings);
        var scanButton = buttons.FirstOrDefault(b => (b.Content as string) == "扫描垃圾文件");
        var cleanButton = buttons.FirstOrDefault(b => (b.Content as string) == "清理选中项");
        Require(scanButton != null, "设置页缺少「扫描垃圾文件」按钮");
        Require(cleanButton != null, "设置页缺少「清理选中项」按钮");
        Require(cleanButton != null && !cleanButton.IsEnabled, "还没扫描时「清理选中项」应当是禁用的");

        // ---- (a) scan through the bound command ---------------------------
        scanButton?.Command?.Execute(null);
        // Round-93: while the scan runs the button must be disabled, otherwise
        // an impatient second click starts a second scan (the whole point).
        if (vm.IsScanningStorage && scanButton != null)
            Require(!scanButton.IsEnabled, "扫描进行中按钮必须置灰（防重复点击）");
        var scanned = PumpUntil(win, () => !vm.IsScanningStorage && vm.HasStorageCleanupReport, 30000);
        Console.WriteLine($"[cleanup-smoke] 扫描：{vm.StorageCleanupSummary}");
        Require(scanned, "点「扫描垃圾文件」后没有出现报告（HasStorageCleanupReport 一直为 false）");
        Require(scanButton == null || scanButton.IsEnabled, "扫描结束后按钮必须恢复可用");
        Require(vm.StorageCleanupGroups.Count > 0, "报告里没有任何分类");

        // ---- (b) rows render as checkboxes with policy-driven defaults ----
        var checkBoxes = new List<CheckBox>();
        Walk(settings, o => { if (o is CheckBox cb && cb.Content is string s && s.Contains("·")) checkBoxes.Add(cb); });
        Console.WriteLine($"[cleanup-smoke] 分类行 {checkBoxes.Count} 个 / 文本 " +
                          string.Join(" | ", checkBoxes.Select(c => (string)c.Content).Take(3)));
        Require(checkBoxes.Count == vm.StorageCleanupGroups.Count,
            $"应有 {vm.StorageCleanupGroups.Count} 个分类复选项，实际渲染 {checkBoxes.Count} 个");

        var offline = vm.StorageCleanupGroups.FirstOrDefault(g => g.CostsOffline);
        Require(offline != null, "应当有一个会牺牲离线能力的分类（加载器缓存）");
        Require(offline != null && !offline.Selected, "CostsOffline 分类绝不能默认勾选");
        Require(vm.StorageCleanupGroups.Where(g => !g.CostsOffline).All(g => g.Selected),
            "安全分类应当默认勾选");

        var offlineBox = checkBoxes.FirstOrDefault(c => ((string)c.Content).Contains("离线能力"));
        Require(offlineBox != null, "离线代价应当在行文案里写明");
        Require(offlineBox != null && offlineBox.IsChecked == false, "界面上的离线分类复选框必须是未勾状态");

        Require(!string.IsNullOrWhiteSpace(vm.StorageCleanupSelectionText), "选中汇总文案没有生成");
        Require(cleanButton!.IsEnabled, "有勾选项时「清理选中项」应当可用");

        // ---- (c) clean for real ------------------------------------------
        cleanButton.Command.Execute(null);
        var cleaned = PumpUntil(win, () => !vm.IsCleaningStorage && vm.StorageCleanupResult.Contains("释放"), 40000);
        Console.WriteLine($"[cleanup-smoke] 结果：{vm.StorageCleanupResult}");
        Require(cleaned, "点「清理选中项」后没有拿到「已释放…」的结果");
        Require(!File.Exists(Path.Combine(root, "cache", "icons", "a.img")), "图标缓存没有被真正删除");
        Require(!File.Exists(Path.Combine(root, "logs", "aurora-2020-01-01.log")), "旧日志没有被真正删除");
        Require(!Directory.Exists(Path.Combine(root, "versions", "shell")), "空壳版本目录没有被删除");
        Require(File.Exists(Path.Combine(root, "cache", "installers", "meta.json")), "未勾选的分类被误删！");
        Require(File.Exists(Path.Combine(root, "instances", "i1", "saves", "world.dat")), "存档被误删！");
        Require(File.Exists(Path.Combine(root, "update", "AuroraLauncher.new.exe")), "待安装的更新包被误删！");

        Require(bindingErrors.Count == 0, "设置页出现绑定错误: " + string.Join(" | ", bindingErrors.Take(3)));

        // Visual artefact: rescan so the picture shows the real list.
        try
        {
            W(Path.Combine("cache", "icons", "c.img"), 5000);
            scanButton!.Command!.Execute(null);
            PumpUntil(win, () => !vm.IsScanningStorage && vm.HasStorageCleanupReport, 20000);
            var box = FindCleanupBox(settings);
            box?.BringIntoView();
            Pump(win);
            var shot = Path.Combine(Path.GetTempPath(), "aurora-cleanup-settings.png");
            // Render the whole page (it is taller than the window) so the block is included.
            var height = Math.Max(settings.ActualHeight, 1400);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)settings.ActualWidth, (int)height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(settings);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(shot);
            encoder.Save(fs);
            Console.WriteLine($"[cleanup-smoke] 截图: {shot}");
        }
        catch (Exception ex) { Console.WriteLine("[cleanup-smoke] 截图失败: " + ex.Message); }

        if (fails.Count == 0)
        {
            Console.WriteLine("[cleanup-smoke] SMOKE OK - 扫描 / 分类勾选 / 清理 / 保护项全部生效");
            try { Directory.Delete(root, true); } catch { }
            return 0;
        }
        foreach (var f in fails) Console.WriteLine("[cleanup-smoke] FAIL: " + f);
        return 1;
    }

    /// <summary>Read-only diagnostic: run the real scanner against the configured
    /// data root and print what it found. Deletes nothing — this is how the timing
    /// and the classification get checked on a real 1.5 GB folder.</summary>
    public static int ScanRealRootForDiagnostics()
    {
        var root = LauncherConfig.Instance.RootDirectory;
        Console.WriteLine($"[cleanup-scan] root: {root}");
        var clock = Stopwatch.StartNew();
        var report = new StorageCleanupService(root).Scan();
        clock.Stop();
        Console.WriteLine($"[cleanup-scan] 用时 {clock.ElapsedMilliseconds} ms · {report.Groups.Count} 类 · " +
                          $"{report.TotalFiles} 个文件 + {report.TotalDirs} 个空目录 · " +
                          $"{JobProgressPolicy.FormatBytes(report.TotalBytes)}");
        foreach (var group in report.Groups)
            Console.WriteLine($"[cleanup-scan]   {group.Info.DisplayName,-16} {group.AmountText,-28} " +
                              (group.Info.DefaultSelected ? "默认勾选" : "默认不勾"));
        if (report.ActivePartCount > 0)
            Console.WriteLine($"[cleanup-scan]   跳过 {report.ActivePartCount} 个正在下载的残片");
        return 0;
    }

    private static CheckBox? FindCleanupBox(DependencyObject root)    {
        CheckBox? found = null;
        Walk(root, o =>
        {
            if (found != null || o is not CheckBox cb) return;
            if (cb.Content is string s && s.Contains("·")) found = cb;
        });
        return found;
    }

    private sealed class Collector : TraceListener
    {
        private readonly List<string> _sink;
        public Collector(List<string> sink) { _sink = sink; }
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            if (message.Contains("BindingExpression") || message.Contains("path error"))
                _sink.Add(message.Replace("\r", " ").Replace("\n", " ").Trim());
        }
    }

    private static void LoadResources(Application app)
    {
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/AuroraLauncher;component/Themes/AuroraBrushes.Dark.xaml", UriKind.Absolute)
        });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/AuroraLauncher;component/Styles/AuroraControls.xaml", UriKind.Absolute)
        });
        app.Resources["PageActive"] = new PageActiveConverter();
        app.Resources["TextMatch"] = new TextMatchConverter();
        app.Resources["BoolToText"] = new BoolToTextConverter();
        app.Resources["BoolToOpacity"] = new BoolToOpacityConverter();
        app.Resources["StatusToBrush"] = new StatusToBrushConverter();
        app.Resources["NullToCollapsed"] = new NullToCollapsedConverter();
        app.Resources["BoolToVis"] = new BooleanToVisibilityConverter();
    }

    private static void Pump(Window win)
    {
        win.UpdateLayout();
        win.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        win.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        win.UpdateLayout();
    }

    private static void PumpFrame()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static bool PumpUntil(Window win, Func<bool> done, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            Pump(win);
            PumpFrame();
            if (done()) return true;
            Thread.Sleep(40);
        }
        return done();
    }

    private static List<Button> Buttons(DependencyObject root)
    {
        var list = new List<Button>();
        Walk(root, o => { if (o is Button b) list.Add(b); });
        return list;
    }

    private static void Walk(DependencyObject root, Action<DependencyObject> visit)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            visit(node);
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++) stack.Push(VisualTreeHelper.GetChild(node, i));
        }
    }
}
