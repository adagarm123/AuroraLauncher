using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AuroraCore;
using AuroraCore.Services;
using AuroraLauncher.Desktop.Converters;
using AuroraLauncher.Desktop.Services;
using AuroraLauncher.Desktop.ViewModels;
using AuroraLauncher.Desktop.Views;

namespace AuroraSelfTest;

/// <summary>
/// Round-80 opt-in smoke: proves the glass layer really does something.
/// Verifies (a) surface brushes actually change and are fully restored,
/// (b) the noise/gradient brushes are frozen (no re-rasterisation per frame),
/// (c) the red warning dialog renders its title/body/buttons, and
/// (d) the settings page shows the new "界面材质" control with real bindings.
///
/// Invoked ONLY with --glass-smoke (needs an STA thread + desktop).
/// </summary>
internal static class GlassSmoke
{
    public static int Run()
    {
        var code = 1;
        var thread = new Thread(() =>
        {
            try { code = RunCore(); }
            catch (Exception ex)
            {
                Console.WriteLine("[glass-smoke] EXCEPTION: " + ex);
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

        var root = Path.Combine(Path.GetTempPath(), "aurora-glass-" + Guid.NewGuid().ToString("N")[..6]);
        LauncherConfig.UseTestRoot(root); // round-84: isolate the config FILE too, not just the root
        LauncherConfig.Instance.EnsureDirectories();
        LauncherConfig.Instance.GlassMode = GlassMode.Off;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        LoadResources(app);

        var fails = new List<string>();
        void Require(bool ok, string why) { if (!ok) fails.Add(why); }

        // ---- (a) surfaces really change, then really come back ----
        var before = (Application.Current.Resources["PanelBrush"] as SolidColorBrush)?.Color;
        Require(before is { A: 255 }, "the default panel brush should be opaque");

        GlassVisuals.ApplyTranslucentSurfaces(dark: true);
        var during = (Application.Current.Resources["PanelBrush"] as SolidColorBrush)?.Color;
        Require(during is { } d && d.A < 255, "glass mode must make cards translucent");
        Require(Application.Current.Resources["BorderBrush"] is SolidColorBrush { Color.A: < 255 },
            "glass mode must soften the hairline border");

        GlassVisuals.Restore();
        var after = (Application.Current.Resources["PanelBrush"] as SolidColorBrush)?.Color;
        Require(after == before, "Restore() must put the original brush back exactly");
        Require(!GlassVisuals.IsApplied, "Restore() must remove the override dictionary (theme switch would break)");

        // ---- (b) brushes are frozen & the noise actually tiles ----
        Require(GlassVisuals.Noise.IsFrozen, "the noise brush must be frozen (shared, no re-raster)");
        Require(GlassVisuals.Noise is ImageBrush { TileMode: TileMode.Tile },
            "the noise must tile; a stretched 64×64 image would look like a smear");
        var gradient = GlassVisuals.BuildWindowGradient(true);
        Require(gradient.IsFrozen, "the window gradient must be frozen");
        Require(((LinearGradientBrush)gradient).GradientStops.Count >= 2, "the gradient needs real stops");

        // ---- (c) the red gate renders ----
        var warn = new GlassWarningWindow(
            GlassEffectPolicy.WarningTitle,
            GlassEffectPolicy.WarningBody(GlassEffectPolicy.DisplayName(GlassMode.Faux), GlassMode.Faux, 19045))
        {
            Left = -4000, Top = -4000, ShowActivated = false, ShowInTaskbar = false
        };
        warn.Show();
        Pump(warn);
        var warnTexts = Texts(warn);
        var warnButtons = Buttons(warn);
        Require(warnTexts.Any(t => t.Contains("卡顿") || t.Contains("崩溃")), "警告标题/正文没有渲染");
        Require(warnTexts.Any(t => t.Contains("我已知晓")), "确认按钮文案缺失");
        Require(warnButtons.Count >= 2, "警告窗口必须有两个按钮（取消 / 仍然开启）");
        Require(warnButtons.Any(b => (b.Content as string)?.Contains("取消") == true), "缺少取消按钮");
        warn.Close();

        // ---- (d) the settings control is wired ----
        var vm = new MainViewModel();
        var settings = new SettingsView { DataContext = vm };
        var host = new Window
        {
            Width = 1160, Height = 760, ShowActivated = false, WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, Left = -4000, Top = -4000, Content = settings
        };
        host.Show();
        Pump(host);
        var texts = Texts(settings);
        var combos = new List<ComboBox>();
        Walk(settings, o => { if (o is ComboBox c) combos.Add(c); });
        Require(texts.Any(t => t.Contains("界面材质")), "设置页没有「界面材质」标签");
        Require(combos.Any(c => c.ItemsSource == vm.GlassModeOptions), "材质下拉框没有绑定 GlassModeOptions");
        Require(vm.GlassModeText == GlassEffectPolicy.DisplayName(GlassMode.Off),
            "默认必须是「关闭」");
        Require(vm.GlassNote.Contains("原") || vm.GlassNote.Contains("关闭"), "缺少当前材质说明文案");
        host.Close();

        var relevant = bindingErrors
            .Where(e => e.Contains("Glass") || e.Contains("界面材质"))
            .ToList();
        Require(relevant.Count == 0, "绑定错误: " + string.Join(" | ", relevant.Take(3)));

        Console.WriteLine($"[glass-smoke] 警告文本 {warnTexts.Count} 条 / 设置页文本 {texts.Count} 条 / " +
                          $"下拉框 {combos.Count} 个 / 绑定错误 {bindingErrors.Count} 条");
        if (fails.Count == 0)
        {
            Console.WriteLine("[glass-smoke] SMOKE OK - 表面重着色可逆 / 噪点与渐变已冻结 / 红色警告可渲染 / 设置项已接线");
            try { Directory.Delete(root, true); } catch { }
            return 0;
        }
        foreach (var f in fails) Console.WriteLine("[glass-smoke] FAIL: " + f);
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
