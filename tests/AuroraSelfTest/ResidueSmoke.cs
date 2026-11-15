using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AuroraCore;
using AuroraLauncher.Desktop.Converters;
using AuroraLauncher.Desktop.ViewModels;
using AuroraLauncher.Desktop.Views;

namespace AuroraSelfTest;

/// <summary>
/// Round-87 opt-in smoke: the residue pipeline end to end — real scan, real
/// threshold, real banner on the download page, real cleanup through the bound
/// command, real files gone from disk.
///
/// Why a smoke and not just unit tests: the whole feature is bindings plus a
/// background scan. A wrong property name ships an invisible banner and a button
/// that does nothing, with every unit test green. Invoked with --residue-smoke.
/// </summary>
internal static class ResidueSmoke
{
    public static int Run()
    {
        var code = 1;
        var thread = new Thread(() =>
        {
            try { code = RunCore(); }
            catch (Exception ex)
            {
                Console.WriteLine("[residue-smoke] EXCEPTION: " + ex);
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

        var root = Path.Combine(Path.GetTempPath(), "aurora-residue-smoke-" + Guid.NewGuid().ToString("N")[..6]);
        LauncherConfig.UseTestRoot(root);
        LauncherConfig.Instance.EnsureDirectories();
        // Threshold so low that two small files trip it: the point is the wiring,
        // not the 200 MB default (which must not be allocated in a test).
        LauncherConfig.Instance.ResidueWarnBytesMb = 1;
        LauncherConfig.Instance.ResidueWarnCount = 0;

        // Junk that no job owns: an interrupted update package + an untracked
        // partial under libraries/. Nothing journaled, so the cleanup runs without
        // a confirmation dialog (a MessageBox would block a headless run).
        var updatePart = Path.Combine(root, "update", "AuroraLauncher.patch.part");
        Directory.CreateDirectory(Path.GetDirectoryName(updatePart)!);
        File.WriteAllBytes(updatePart, new byte[2 * 1024 * 1024]);
        var libPart = Path.Combine(root, "libraries", "net", "example", "thing.jar.part");
        Directory.CreateDirectory(Path.GetDirectoryName(libPart)!);
        File.WriteAllBytes(libPart, new byte[4096]);
        // A completed file next door must survive untouched.
        var done = Path.Combine(root, "libraries", "net", "example", "thing.jar");
        File.WriteAllBytes(done, new byte[64]);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        LoadResources(app);

        var vm = new MainViewModel();
        var fails = new List<string>();
        void Require(bool ok, string why) { if (!ok) fails.Add(why); }

        var win = new Window
        {
            Width = 1200, Height = 780, ShowActivated = false, WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, Left = -4000, Top = -4000
        };
        var host = new Grid();
        win.Content = host;
        win.Show();

        var downloads = new DownloadsView { DataContext = vm };
        host.Children.Add(downloads);
        Pump(win);

        // ---- (a) the startup scan reaches the banner ----
        var appeared = PumpUntil(win, () => vm.HasResidueNotice, 20000);
        Console.WriteLine($"[residue-smoke] banner={vm.HasResidueNotice} text=「{vm.ResidueNoticeText}」");
        Require(appeared, "启动扫描后下载页没有出现残留提示（阈值 1 MB）");
        Require(vm.ResidueNoticeText.Contains("残留") && vm.ResidueNoticeText.Contains("清理"),
            "提示文案必须说清楚是什么、去哪清理：" + vm.ResidueNoticeText);

        var texts = Texts(downloads);
        Require(texts.Any(t => t.Contains("未完成的下载残留")), "提示文字没有渲染到下载页");
        var buttons = Buttons(downloads);
        var goSettings = buttons.FirstOrDefault(b => (b.Content as string) == "去设置清理");
        Require(goSettings != null, "「去设置清理」按钮缺失");
        Require(buttons.Any(b => (b.Content as string) == "知道了"), "「知道了」按钮缺失");

        // Visual artefact, taken while the banner is on screen (before cleanup).
        try
        {
            var shot = Path.Combine(Path.GetTempPath(), "aurora-residue-downloads.png");
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)downloads.ActualWidth, (int)downloads.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(downloads);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(shot);
            encoder.Save(fs);
            Console.WriteLine($"[residue-smoke] 截图: {shot}");
        }
        catch (Exception ex) { Console.WriteLine("[residue-smoke] 截图失败: " + ex.Message); }

        // ---- (b) 去设置清理 really navigates ----
        if (goSettings?.Command != null)
        {
            goSettings.Command.Execute(null);
            Pump(win);
            Require(vm.CurrentPageKey == "Settings",
                $"「去设置清理」应跳到设置页，当前是 {vm.CurrentPageKey}");
        }

        // ---- (c) the settings button cleans for real ----
        var settings = new SettingsView { DataContext = vm };
        host.Children.Clear();
        host.Children.Add(settings);
        Pump(win);
        var cleanButton = Buttons(settings).FirstOrDefault(b => (b.Content as string) == "清理残留下载文件");
        Require(cleanButton != null, "设置页缺少「清理残留下载文件」按钮");
        if (cleanButton?.Command != null)
        {
            cleanButton.Command.Execute(null);
            var finished = PumpUntil(win, () => !vm.IsCleaningResidue && !string.IsNullOrEmpty(vm.ResidueStatusText), 30000);
            Console.WriteLine($"[residue-smoke] 清理结果=「{vm.ResidueStatusText}」");
            Require(finished, "点「清理残留下载文件」后没有任何结果");
            Require(vm.ResidueStatusText.Contains("释放"), "结果必须说明释放了多少空间：" + vm.ResidueStatusText);
            Require(!File.Exists(updatePart), "update/ 里的 .part 没有被删掉");
            Require(!File.Exists(libPart), "libraries/ 里的 .part 没有被删掉");
            Require(File.Exists(done), "清理误删了已完成的文件！");
            Require(!vm.HasResidueNotice, "清理后提示应消失");
        }

        var relevant = bindingErrors
            .Where(e => e.Contains("Residue")).ToList();
        Require(relevant.Count == 0, "残留相关的绑定错误: " + string.Join(" | ", relevant.Take(3)));
        Require(bindingErrors.Count == 0, "下载页/设置页出现绑定错误: " + string.Join(" | ", bindingErrors.Take(3)));

        // Visual artefact stays where it is: the banner has to be photographed
        // while it is still on screen (the cleanup below removes the files).

        if (fails.Count == 0)
        {
            Console.WriteLine("[residue-smoke] SMOKE OK - 扫描 / 提示 / 跳转 / 清理全部生效");
            try { Directory.Delete(root, true); } catch { }
            return 0;
        }
        foreach (var f in fails) Console.WriteLine("[residue-smoke] FAIL: " + f);
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

    private static List<string> Texts(DependencyObject root)
    {
        var list = new List<string>();
        Walk(root, o => { if (o is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text)) list.Add(tb.Text); });
        return list;
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
