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
/// Round-78 opt-in smoke diagnostic: renders the REAL crash card with the REAL
/// style dictionary and asserts that every new binding actually resolves.
///
/// Why this exists: WPF binding failures are silent — a typo in
/// <c>LastCrashReport.Solutions</c> or <c>CanApply</c> produces an empty row and
/// a green unit suite. Unit tests can only see the engine, so this harness
/// loads the view, walks the visual tree and reads back what was rendered.
/// Invoked ONLY with --crash-smoke (needs an STA thread and a desktop window).
/// </summary>
internal static class CrashCardSmoke
{
    public static int Run()
    {
        var code = 1;
        var thread = new Thread(() =>
        {
            try { code = RunCore(); }
            catch (Exception ex)
            {
                Console.WriteLine("[crash-smoke] EXCEPTION: " + ex);
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
        PresentationTraceSources.DataBindingSource.Listeners.Add(new ErrorCollector(bindingErrors));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;

        // Never touch the user's real data root: this harness APPLIES a repair.
        var root = Path.Combine(Path.GetTempPath(), "aurora-crash-smoke-" + Guid.NewGuid().ToString("N")[..6]);
        LauncherConfig.UseTestRoot(root); // round-84: isolate the config FILE too, not just the root
        LauncherConfig.Instance.EnsureDirectories();
        LauncherConfig.Instance.MaxMemoryMb = 2048;
        LauncherConfig.Instance.MinMemoryMb = 1024;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        LoadResources(app);

        var vm = new MainViewModel();

        // A realistic crash: OOM + a duplicated mod.
        var ctx = new CrashContext
        {
            InstanceName = "冒烟实例",
            ExitCode = -1,
            MaxMemoryMb = 2048,
            TotalPhysicalMb = 16384,
            Loader = ModLoaderType.Fabric,
            MinecraftVersion = "1.21.1",
            CurrentJavaMajor = 21,
            InstalledJavaMajors = new[] { 8, 21 }
        };
        var diagnosis = CrashResolutionEngine.Diagnose(
            "java.lang.OutOfMemoryError: Java heap space\nDuplicate mods: sodium.jar, sodium-1.jar", ctx);
        var report = new CrashReportVM(-1, "冒烟实例", diagnosis.Signature);
        var index = 1;
        foreach (var s in diagnosis.Solutions) report.Solutions.Add(new CrashSolutionVM(s, index++));

        var prop = typeof(MainViewModel).GetProperty(nameof(MainViewModel.LastCrashReport))!;
        prop.GetSetMethod(nonPublic: true)!.Invoke(vm, new object[] { report });

        var view = new LogsView { DataContext = vm };
        var win = new Window
        {
            Width = 1160, Height = 720, Content = view,
            ShowActivated = false, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            Left = -4000, Top = -4000
        };
        win.Show();
        Pump(win);

        var texts = new List<string>();
        var buttons = new List<Button>();
        Walk(view, o =>
        {
            if (o is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text)) texts.Add(tb.Text);
            if (o is Button b) buttons.Add(b);
        });

        var fails = new List<string>();
        void Require(bool ok, string why) { if (!ok) fails.Add(why); }

        Require(texts.Any(t => t == "检测到游戏崩溃"), "崩溃卡片标题没有渲染");
        Require(texts.Any(t => t.Contains("当前上限 2048 MB")), "诊断正文没有渲染（绑定路径可能写错）");
        Require(texts.Any(t => t.Contains("可一键修复")), "「可一键修复」标签没有渲染（KindLabel 绑定）");
        Require(texts.Any(t => t.Contains("检测到重复 Mod")), "第二条诊断没有渲染（Solutions 集合绑定）");
        Require(texts.Any(t => t.Contains("2 条诊断")), "诊断计数没有渲染（CountText 绑定）");

        Require(buttons.Any(b => (b.Content as string) == "发送给作者"), "「发送给作者」按钮缺失");
        Require(buttons.Any(b => (b.Content as string) == "打开设置"), "「打开设置」按钮缺失");

        var fixButtons = buttons.Where(b => (b.Content as string)?.Contains("内存") == true).ToList();
        Require(fixButtons.Count > 0, "诊断行的修复按钮没有渲染");
        Require(fixButtons.Any(b => b.IsEnabled), "修复按钮被禁用（CanApply / IsEnabled 绑定错误）");

        // Bindings that mention anything we added must resolve — a silent path
        // error on the card is exactly what this harness is for.
        var relevant = bindingErrors
            .Where(e => e.Contains("LastCrashReport") || e.Contains("Solution") || e.Contains("KindLabel")
                     || e.Contains("ResultText") || e.Contains("CanApply") || e.Contains("ActionText")
                     || e.Contains("Diagnosis") || e.Contains("CountText"))
            .ToList();
        Require(relevant.Count == 0, "绑定错误: " + string.Join(" | ", relevant.Take(3)));

        // Apply the memory repair through the real command (a click, minus the mouse).
        var memoryRow = report.Solutions.FirstOrDefault(s => s.Solution.Action == FixAction.RaiseMemory);
        Require(memoryRow != null, "OOM 诊断没有给出调内存的修复动作");
        if (memoryRow != null)
        {
            vm.ApplyCrashFixCommand.Execute(memoryRow);
            Pump(win);
            var expected = int.Parse(memoryRow.Solution.Payload!);
            Require(LauncherConfig.Instance.MaxMemoryMb == expected,
                $"点击修复后配置没变（期望 {expected}，实际 {LauncherConfig.Instance.MaxMemoryMb}）");
            Require(memoryRow.ResultText.Contains(expected.ToString()), "修复结果没有回显在卡片上");
            Require(!memoryRow.CanApply, "自动修复应用后按钮应变为不可用");
        }

        Console.WriteLine($"[crash-smoke] 渲染文本 {texts.Count} 条 / 按钮 {buttons.Count} 个 / 绑定错误 {bindingErrors.Count} 条");
        if (fails.Count == 0)
        {
            Console.WriteLine("[crash-smoke] SMOKE OK - 诊断卡渲染 + 一键修复生效");
            try { Directory.Delete(root, true); } catch { }
            return 0;
        }
        foreach (var f in fails) Console.WriteLine("[crash-smoke] FAIL: " + f);
        return 1;
    }

    /// <summary>Collects WPF binding trace output (the only place silent binding
    /// failures are reported).</summary>
    private sealed class ErrorCollector : TraceListener
    {
        private readonly List<string> _sink;
        public ErrorCollector(List<string> sink) { _sink = sink; }
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
