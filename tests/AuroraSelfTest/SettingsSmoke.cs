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
/// Round-90 opt-in smoke: unsaved-settings interception on the REAL view model.
///
/// The unit tests prove the snapshot comparison; this proves the wiring — that
/// Navigate actually stops, that "放弃" restores the saved value, that "保存"
/// persists, and that the hint renders. All three are silent failures when a
/// binding or a call site is wrong (the round-40 lesson).
/// </summary>
internal static class SettingsSmoke
{
    public static int Run()
    {
        var code = 1;
        var thread = new Thread(() =>
        {
            try { code = RunCore(); }
            catch (Exception ex)
            {
                Console.WriteLine("[settings-smoke] EXCEPTION: " + ex);
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

        var root = Path.Combine(Path.GetTempPath(), "aurora-settings-smoke-" + Guid.NewGuid().ToString("N")[..6]);
        LauncherConfig.UseTestRoot(root);
        LauncherConfig.Instance.EnsureDirectories();
        LauncherConfig.Instance.ProxyUrl = null;
        LauncherConfig.Instance.JavaPath = null;
        LauncherConfig.Instance.Save();

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        LoadResources(app);

        var vm = new MainViewModel();
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

        // ---- (a) a fresh page is not dirty -------------------------------
        vm.Navigate("Settings");
        Pump(win);
        Require(vm.CurrentPageKey == "Settings", "应当能进入设置页");
        Require(!vm.HasUnsavedSettings, "刚进入设置页时不应算「未保存」");
        Require(string.IsNullOrEmpty(vm.SettingsDirtyHint), "刚进入设置页时不该显示未保存提示");

        // ---- (b) one edit makes it dirty, and the hint appears -----------
        var labelsSeen = new List<string>();
        vm.JavaPath = @"C:\temp\fake-java\bin\javaw.exe";
        Pump(win);
        Require(vm.HasUnsavedSettings, "改了 Java 路径之后必须算未保存");
        // The hint is refreshed by a 500 ms ticker (most fields are plain
        // auto-properties, see InitRound90SettingsGuard) — give it one tick.
        var hintAppeared = PumpUntil(win, () => vm.HasSettingsDirtyHint, 4000);
        Require(hintAppeared && vm.SettingsDirtyHint.Contains("尚未保存"),
            "未保存提示没有生成：" + vm.SettingsDirtyHint);
        Require(Texts(settings).Any(t => t.Contains("尚未保存")), "未保存提示没有渲染到页面");

        // Visual artefacts: the close-behaviour label (does the longer text fit?)
        // and the save row with the dirty hint. Both are read by a human here
        // because "text clipped in a combo box" is invisible to assertions.
        try
        {
            var scroller = FindDescendantScrollViewer(settings);
            // Show the LONG label in the combo for the picture — "text clipped in a
            // combo box" is exactly what assertions cannot see.
            var exitLabel = CloseBehaviorPolicy.DisplayText(CloseAction.Exit);
            Require(vm.CloseActionOptions.Contains(exitLabel),
                "下拉框选项里必须包含新的「直接退出」文案：" + string.Join(" | ", vm.CloseActionOptions));
            vm.CloseActionText = exitLabel;
            FindComboBox(settings, "CloseActionOptions")?.BringIntoView();
            Pump(win);
            Pump(win);
            Shot(settings, Path.Combine(Path.GetTempPath(), "aurora-settings-close.png"), 900);
            scroller?.ScrollToEnd();
            Pump(win);
            Pump(win);
            Shot(settings, Path.Combine(Path.GetTempPath(), "aurora-settings-save.png"), 1200);
            scroller?.ScrollToHome();
            Pump(win);
        }
        catch (Exception ex) { Console.WriteLine("[settings-smoke] 截图失败: " + ex.Message); }

        // ---- (c) 「取消」 keeps the user on the page ---------------------
        vm.UnsavedSettingsPromptForTests = labels => { labelsSeen.AddRange(labels); return UnsavedSettingsDecision.Cancel; };
        vm.Navigate("Home");
        Pump(win);
        Require(vm.CurrentPageKey == "Settings", "选「取消」后必须留在设置页，实际跳到了 " + vm.CurrentPageKey);
        Require(labelsSeen.Any(l => l.Contains("Java")), "提示里应当点名改了「Java 路径」：" + string.Join(",", labelsSeen));

        // ---- (d) 「放弃」 restores the saved value ----------------------
        vm.UnsavedSettingsPromptForTests = _ => UnsavedSettingsDecision.Discard;
        vm.Navigate("Home");
        Pump(win);
        Require(vm.CurrentPageKey == "Home", "选「放弃」后应当离开设置页");
        Require(string.IsNullOrEmpty(vm.JavaPath), "放弃修改后 Java 路径应当恢复成保存的值，实际是「" + vm.JavaPath + "」");
        Require(!vm.HasUnsavedSettings, "放弃之后不该再算未保存");
        Require(string.IsNullOrEmpty(LauncherConfig.Instance.JavaPath ?? ""), "放弃修改不应写进配置");

        // ---- (e) 「保存」 persists and clears the flag -------------------
        vm.Navigate("Settings");
        Pump(win);
        vm.ProxyUrl = "http://127.0.0.1:7890";
        Pump(win);
        Require(vm.HasUnsavedSettings, "改了代理地址之后必须算未保存");
        vm.UnsavedSettingsPromptForTests = _ => UnsavedSettingsDecision.Save;
        vm.Navigate("Home");
        Pump(win);
        Require(vm.CurrentPageKey == "Home", "选「保存」后应当离开设置页");
        Require(LauncherConfig.Instance.ProxyUrl == "http://127.0.0.1:7890",
            "选「保存」必须真的写进配置，实际是「" + (LauncherConfig.Instance.ProxyUrl ?? "null") + "」");
        Require(!vm.HasUnsavedSettings, "保存之后不该再算未保存");

        // ---- (f) closing the window asks the same question --------------
        vm.Navigate("Settings");
        Pump(win);
        vm.JavaPath = @"C:\temp\other\bin\javaw.exe";
        Pump(win);
        Require(vm.HasUnsavedSettings, "前置条件：应当有未保存的修改");
        vm.UnsavedSettingsPromptForTests = _ => UnsavedSettingsDecision.Cancel;
        Require(!vm.ConfirmCloseWithUnsavedSettings(), "选「取消」时窗口必须留住（返回 false）");
        vm.UnsavedSettingsPromptForTests = _ => UnsavedSettingsDecision.Save;
        Require(vm.ConfirmCloseWithUnsavedSettings(), "选「保存」后应当允许关闭");
        Require(LauncherConfig.Instance.JavaPath == @"C:\temp\other\bin\javaw.exe",
            "关窗时选「保存」也要真的写进配置");

        Require(bindingErrors.Count == 0, "设置页出现绑定错误: " + string.Join(" | ", bindingErrors.Take(3)));

        if (fails.Count == 0)
        {
            Console.WriteLine("[settings-smoke] SMOKE OK - 脏检查 / 取消拦截 / 放弃回滚 / 保存离开 / 关窗询问 全部生效");
            try { Directory.Delete(root, true); } catch { }
            return 0;
        }
        foreach (var f in fails) Console.WriteLine("[settings-smoke] FAIL: " + f);
        return 1;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? node)
    {
        while (node != null && node is not ScrollViewer) node = VisualTreeHelper.GetParent(node);
        return node as ScrollViewer;
    }

    /// <summary>The page's own scroller is a CHILD of the UserControl — walking
    /// parents (as the memory smoke does from a TextBox) returns null here.</summary>
    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        ScrollViewer? found = null;
        Walk(root, o => { if (found == null && o is ScrollViewer sv) found = sv; });
        return found;
    }

    private static ComboBox? FindComboBox(DependencyObject root, string itemsSourcePath)
    {
        ComboBox? found = null;
        Walk(root, o =>
        {
            if (found != null || o is not ComboBox cb) return;
            if (cb.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.ParentBinding.Path.Path == itemsSourcePath)
                found = cb;
        });
        return found;
    }

    private static void Shot(UIElement element, string path, int height)
    {
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Max(element.RenderSize.Width, 100), Math.Max(height, 100), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(element);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
        Console.WriteLine($"[settings-smoke] 截图: {path}");
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

    /// <summary>Pumps message loops until <paramref name="done"/> — needed for the
    /// 500 ms dirty-hint ticker, which <see cref="Pump"/> alone never reaches.</summary>
    private static bool PumpUntil(Window win, Func<bool> done, int timeoutMs)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            Pump(win);
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            if (done()) return true;
            Thread.Sleep(40);
        }
        return done();
    }

    private static List<string> Texts(DependencyObject root)
    {
        var list = new List<string>();
        Walk(root, o => { if (o is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text)) list.Add(tb.Text); });
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
