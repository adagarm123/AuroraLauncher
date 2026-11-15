using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AuroraCore;
using AuroraCore.Models;
using AuroraCore.Services;
using AuroraLauncher.Desktop.Converters;
using AuroraLauncher.Desktop.ViewModels;
using AuroraLauncher.Desktop.Views;

namespace AuroraSelfTest;

/// <summary>
/// Round-79 opt-in smoke diagnostic: renders the two pages that gained new cards
/// (首页性能概览 / 实例页存档备份) with the real styles and a real view model,
/// then drives the Ctrl+K palette through its view-model surface.
///
/// Why: WPF binding errors are silent. A typo in <c>SystemCpuText</c> or
/// <c>BackupInfoText</c> would ship an empty card with a green unit suite.
/// Invoked ONLY with --round79-smoke.
/// </summary>
internal static class Round79Smoke
{
    public static int Run()
    {
        var code = 1;
        var thread = new Thread(() =>
        {
            try { code = RunCore(); }
            catch (Exception ex)
            {
                Console.WriteLine("[r79-smoke] EXCEPTION: " + ex);
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

        var root = Path.Combine(Path.GetTempPath(), "aurora-r79-" + Guid.NewGuid().ToString("N")[..6]);
        LauncherConfig.UseTestRoot(root); // round-84: isolate the config FILE too, not just the root
        LauncherConfig.Instance.EnsureDirectories();

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        LoadResources(app);

        var vm = new MainViewModel();

        var fails = new List<string>();
        void Require(bool ok, string why) { if (!ok) fails.Add(why); }

        // ---- Round-82: 全新用户（没有任何实例）点启动游戏不能是死按钮 ----
        Require(!vm.HasInstances, "新建的 VM 应该没有实例");
        Require(vm.LaunchButtonText == "去下载游戏",
            $"没有实例时按钮应改为「去下载游戏」，实际「{vm.LaunchButtonText}」");
        Require(vm.InstanceCardHint.Contains("还没有实例"), "实例卡片提示没有切到「还没有实例」文案");
        vm.LaunchCommand.Execute(null);
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
            System.Windows.Threading.DispatcherPriority.Background);
        Require(vm.CurrentPageKey == "Download",
            $"没有实例时点启动应跳到游戏下载页，实际停在 {vm.CurrentPageKey}");

        // A real instance directory with a real world, so backup + perf cards have data.
        var inst = new GameInstance
        {
            Id = "smoke79",
            Name = "冒烟实例 79",
            MinecraftVersion = "1.21.1",
            LoaderType = ModLoaderType.Fabric,
            LastPeakMemoryMb = 3900,
            LastAverageFps = 61,
            LastFpsSamples = 8,
            LastRunSeconds = 42 * 60,
            LastPerfAt = DateTime.Now
        };
        Directory.CreateDirectory(Path.Combine(inst.Directory, "saves", "World1"));
        File.WriteAllText(Path.Combine(inst.Directory, "saves", "World1", "level.dat"), "LEVEL");
        vm.Instances.Add(inst);
        vm.SelectedInstance = inst;

        // Round-82: adding the first instance must flip the button back.
        Require(vm.HasInstances && vm.LaunchButtonText == "启动游戏",
            "有实例后按钮应恢复「启动游戏」");

        var win = new Window
        {
            Width = 1160, Height = 760, ShowActivated = false, WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, Left = -4000, Top = -4000
        };
        var host = new Grid();
        win.Content = host;
        win.Show();

        // ---- 首页：性能概览 ----
        var home = new HomeView { DataContext = vm };
        host.Children.Add(home);
        Pump(win);
        var homeTexts = Texts(home);
        Require(homeTexts.Any(t => t.Contains("性能概览")), "首页性能卡标题没有渲染");
        Require(homeTexts.Any(t => t.Contains("上次运行")), "「上次运行」标签没有渲染");
        Require(homeTexts.Any(t => t.Contains("峰值内存 3900 MB")), "上次运行明细没有渲染（PerfSummaryText 绑定）");
        Require(homeTexts.Any(t => t.Contains("GB 可用")), "系统内存读数没有渲染");
        Require(homeTexts.Any(t => t == "—" || t.EndsWith('%')), "CPU 读数没有渲染");

        // ---- Round-91：窗口矮的时候，底部性能卡必须还能被滚到 ----
        // 1280×720 全屏 + 缩放时，卡片区比窗口高，底部那张「性能概览」以前会被直接
        // 裁掉且没有滚动条（用户截图）。这里把窗口压到 720 / 600 逐档验证：
        // 滚到底以后，性能卡最底那行文字必须落在视口内。
        {
            var scroller = FindScrollViewer(home);
            Require(scroller != null, "首页没有可滚动容器：矮窗口下底部内容会被裁掉");
            Console.WriteLine($"[r79-smoke] 找到滚动容器: name=「{scroller?.Name}」 类型={scroller?.GetType().Name} " +
                              $"父级={(scroller != null ? VisualTreeHelper.GetParent(scroller)?.GetType().Name : "?")}");
            var anchor = FindText(home, "整机占用（3 秒刷新）");
            Require(anchor != null, "性能卡底部的说明文字没有渲染（找不到锚点）");

            foreach (var height in new[] { 760.0, 600.0, 480.0 })
            {
                win.Height = height;
                Pump(win);
                Pump(win);
                if (scroller == null || anchor == null) break;

                var needsScroll = scroller.ExtentHeight > scroller.ViewportHeight + 1;
                if (needsScroll)
                    Require(scroller.ScrollableHeight > 0,
                        $"窗口高 {height} 时内容超出视口却没有可滚动距离");

                scroller.ScrollToEnd();
                Pump(win);
                Pump(win);
                var bottomVisible = IsInsideViewport(anchor, scroller);
                Console.WriteLine($"[r79-smoke] 首页 {height:0}px: 内容 {scroller.ExtentHeight:0.#} / 视口 " +
                                  $"{scroller.ViewportHeight:0.#} / 可滚 {scroller.ScrollableHeight:0.#} / 锚点可见={bottomVisible}");
                Require(bottomVisible,
                    $"窗口高 {height} 时滚到底仍看不到性能卡底部（内容被裁掉）");
                scroller.ScrollToTop();
                Pump(win);
            }

            // 第 68 轮的坑：不受限的 ScrollViewer 里的 ListBox 会全量生成容器。
            var instanceList = FindListBox(home);
            Require(instanceList != null && instanceList.MaxHeight > 0 && instanceList.MaxHeight <= 320,
                "实例列表必须有 MaxHeight：无限制的 ListBox 放进 ScrollViewer 会全量生成容器");

            // Hero（欢迎语 + 启动按钮）必须固定，不能跟着内容滚走——它是这个页面最主要
            // 的操作入口。页面上有两个「启动游戏」按钮（hero 一个、实例卡里一个），
            // 必须挑不在滚动容器里的那个，否则测的是实例卡（它本来就该跟着滚）。
            var launchButtons = Buttons(home)
                .Where(b => (b.Content as string) is "启动游戏" or "去下载游戏")
                .ToList();
            var heroLaunch = launchButtons.FirstOrDefault(b => scroller == null || !IsDescendantOf(b, scroller));
            Require(heroLaunch != null, "首页找不到 hero 区的启动按钮（只有实例卡里那个）");
            if (heroLaunch != null && scroller != null)
            {
                win.Height = 600;
                Pump(win);
                var beforeScroll = heroLaunch.TranslatePoint(new Point(0, 0), home).Y;
                scroller.ScrollToEnd();
                Pump(win);
                Pump(win);
                var afterScroll = heroLaunch.TranslatePoint(new Point(0, 0), home).Y;
                Require(Math.Abs(beforeScroll - afterScroll) < 1.0,
                    $"hero 启动按钮跟着内容滚走了（{beforeScroll:0.#} → {afterScroll:0.#}）：它必须固定");
                scroller.ScrollToTop();
                Pump(win);
            }

            // 截图留证：600px 高、滚到底时的首页（性能卡应当完整可见）
            try
            {
                win.Height = 600;
                Pump(win);
                scroller?.ScrollToEnd();
                Pump(win);
                Pump(win);
                var shot = Path.Combine(Path.GetTempPath(), "aurora-home-600.png");
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)Math.Max(home.ActualWidth, 100), (int)Math.Max(home.ActualHeight, 100), 96, 96,
                    PixelFormats.Pbgra32);
                rtb.Render(home);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using var fs = File.Create(shot);
                encoder.Save(fs);
                Console.WriteLine($"[r79-smoke] 首页 600px 截图: {shot}");
            }
            catch (Exception ex) { Console.WriteLine("[r79-smoke] 首页截图失败: " + ex.Message); }

            win.Height = 760;
            Pump(win);
        }

        // ---- Round-82 实时刷新：删光实例后按钮必须当场改字（不用重启页面） ----
        vm.Instances.Clear();
        Pump(win);
        var heroButton = Buttons(home).FirstOrDefault(b =>
            (b.Content as string) is "启动游戏" or "去下载游戏");
        Require(heroButton != null, "首页找不到启动按钮");
        Require((heroButton?.Content as string) == "去下载游戏",
            $"清空实例后按钮应立刻变成「去下载游戏」，实际「{heroButton?.Content}」");
        // 恢复数据，后面的检查仍然在"有实例"状态下进行
        vm.Instances.Add(inst);
        vm.SelectedInstance = inst;
        Pump(win);
        Require((Buttons(home).FirstOrDefault(b => (b.Content as string) is "启动游戏") != null),
            "加回实例后按钮应恢复「启动游戏」");

        host.Children.Remove(home);

        // ---- 实例页：存档备份 ----
        var instancesView = new InstancesView { DataContext = vm };
        host.Children.Add(instancesView);
        Pump(win);
        var instTexts = Texts(instancesView);
        var instButtons = Buttons(instancesView);
        Require(instTexts.Any(t => t.Contains("存档备份")), "实例页存档备份卡没有渲染");
        Require(instButtons.Any(b => (b.Content as string) == "立即备份"), "「立即备份」按钮缺失");
        Require(instButtons.Any(b => (b.Content as string) == "打开备份文件夹"), "「打开备份文件夹」按钮缺失");

        // 真的点一次「立即备份」（直接调用命令，与点击同一条路径）
        if (instButtons.FirstOrDefault(b => (b.Content as string) == "立即备份") is { } backupButton)
        {
            backupButton.Command.Execute(null);
            var backupDir = SaveBackupService.BackupRootOf(root, inst.Id);
            var ok = PumpUntil(win, () => Directory.Exists(backupDir) &&
                                           Directory.EnumerateFiles(backupDir, "saves-*.zip").Any(), 8000);
            Require(ok, "点「立即备份」后没有生成 zip");
            Require(File.Exists(Path.Combine(inst.Directory, "saves", "World1", "level.dat")),
                "备份不该动原始存档");
        }

        // Round-84: dump the page so the button row can be eyeballed (the new
        // 「扫描未注册实例」must not wrap into an ugly second line).
        try
        {
            var shot = Path.Combine(Path.GetTempPath(), "aurora-instances.png");
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)instancesView.ActualWidth, (int)instancesView.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(instancesView);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(shot);
            enc.Save(fs);
            Console.WriteLine("[r79-smoke] 实例页截图: " + shot);
        }
        catch (Exception ex) { Console.WriteLine("[r79-smoke] 截图失败: " + ex.Message); }

        host.Children.Remove(instancesView);

        // ---- Ctrl+K 语义（逻辑层；窗口覆盖层由绑定断言 + 命令守卫覆盖） ----
        vm.TogglePalette();
        Require(vm.IsPaletteOpen, "TogglePalette 没有打开面板");
        vm.PaletteQuery = "备份";
        Require(vm.PaletteResults.Count > 0, "搜索「备份」没有结果");
        Require(vm.PaletteSelected != null, "打开后应默认选中第一行");
        var before = vm.PaletteResults.Count;
        vm.MovePaletteSelection(1);
        Require(vm.PaletteResults.Count == before, "方向键不该改变结果集");
        vm.PaletteQuery = "存档自动备份";
        var setting = vm.PaletteResults.FirstOrDefault(i => i.Kind == PaletteKind.Setting);
        Require(setting != null, "「存档自动备份」应能命中设置项");
        vm.RunPalette(setting);
        Require(!vm.IsPaletteOpen, "执行后应关闭面板");
        Require(vm.CurrentPageKey == "Settings", $"设置项应跳到设置页，实际 {vm.CurrentPageKey}");

        vm.TogglePalette();
        vm.PaletteQuery = "冒烟实例";
        Require(vm.PaletteResults.Any(i => i.Kind == PaletteKind.Instance), "实例应可被搜索到");
        vm.ClosePalette();

        // 只关心本轮新增的绑定，忽略页面里其它历史绑定噪声
        var relevant = bindingErrors
            .Where(e => e.Contains("SystemCpuText") || e.Contains("SystemMemoryText") || e.Contains("SystemLoadNote")
                     || e.Contains("RunPerfText") || e.Contains("RunVerdict") || e.Contains("BackupInfoText")
                     || e.Contains("SaveBackup") || e.Contains("Palette"))
            .ToList();
        Require(relevant.Count == 0, "绑定错误: " + string.Join(" | ", relevant.Take(3)));

        Console.WriteLine($"[r79-smoke] 首页文本 {homeTexts.Count} 条 / 实例页文本 {instTexts.Count} 条 / " +
                          $"按钮 {instButtons.Count} 个 / 绑定错误 {bindingErrors.Count} 条");
        if (fails.Count == 0)
        {
            Console.WriteLine("[r79-smoke] SMOKE OK - 性能卡 / 存档备份 / Ctrl+K 语义全部生效");
            try { Directory.Delete(root, true); } catch { }
            return 0;
        }
        foreach (var f in fails) Console.WriteLine("[r79-smoke] FAIL: " + f);
        return 1;
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

    /// <summary>Pumps the dispatcher until <paramref name="done"/> or timeout —
    /// the async backup completes on this thread, so blocking would deadlock.</summary>
    private static bool PumpUntil(Window win, Func<bool> done, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            Pump(win);
            if (done()) return true;
            Thread.Sleep(50);
        }
        return done();
    }

    private static List<string> Texts(DependencyObject root)
    {
        var list = new List<string>();
        Walk(root, o => { if (o is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text)) list.Add(tb.Text); });
        return list;
    }

    private static List<Button> Buttons(DependencyObject root)    {
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

    // ---- round-91 helpers: is the bottom of the page reachable? ----

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        ScrollViewer? found = null;
        Walk(root, o => { if (found == null && o is ScrollViewer sv) found = sv; });
        return found;
    }

    private static TextBlock? FindText(DependencyObject root, string exact)
    {
        TextBlock? found = null;
        Walk(root, o =>
        {
            if (found == null && o is TextBlock tb && tb.Text == exact) found = tb;
        });
        return found;
    }

    private static ListBox? FindListBox(DependencyObject root)
    {
        ListBox? found = null;
        Walk(root, o => { if (found == null && o is ListBox lb) found = lb; });
        return found;
    }

    private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        var node = child;
        while (node != null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    /// <summary>Is the element's box inside the scroller's visible area? Uses the
    /// element's own bounds (not the scroller's), so "clipped by the container"
    /// is detected rather than "somewhere in the logical tree".</summary>
    private static bool IsInsideViewport(FrameworkElement element, ScrollViewer scroller)
    {
        if (!element.IsVisible || element.ActualHeight <= 0) return false;
        try
        {
            var topLeft = element.TranslatePoint(new Point(0, 0), scroller);
            var bottom = topLeft.Y + element.ActualHeight;
            return topLeft.Y >= -1 && bottom <= scroller.ViewportHeight + 1;
        }
        catch { return false; }
    }
}
