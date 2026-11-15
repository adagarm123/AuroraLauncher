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
/// Round-83 opt-in smoke: renders the REAL settings page with the REAL styles and
/// view model, then drives the automatic-memory switch and the manual clean.
///
/// Why this exists: the settings block is pure data binding — a wrong path
/// (`MemoryAutoHint` vs `MemoryHint`) fails silently and ships an empty line
/// under the memory box with a green unit suite. Invoked ONLY with --memory-smoke.
/// </summary>
internal static class MemorySmoke
{
    public static int Run()
    {
        var code = 1;
        var thread = new Thread(() =>
        {
            try { code = RunCore(); }
            catch (Exception ex)
            {
                Console.WriteLine("[mem-smoke] EXCEPTION: " + ex);
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

        var root = Path.Combine(Path.GetTempPath(), "aurora-mem-smoke-" + Guid.NewGuid().ToString("N")[..6]);
        LauncherConfig.UseTestRoot(root); // round-84: isolate the config FILE too, not just the root
        LauncherConfig.Instance.EnsureDirectories();
        LauncherConfig.Instance.AutoMemory = false;
        LauncherConfig.Instance.MaxMemoryMb = 4096;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Application.Run() is never called here, so install the dispatcher sync
        // context by hand: without it `await ... ConfigureAwait(true)` inside the
        // VM resumes on a pool thread, which is NOT what a real click does.
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        LoadResources(app);

        var vm = new MainViewModel();
        var fails = new List<string>();
        void Require(bool ok, string why) { if (!ok) fails.Add(why); }

        // A modded instance with a shader pack so the recommendation is non-trivial.
        var inst = new GameInstance
        {
            Id = "memsmoke",
            Name = "内存冒烟",
            MinecraftVersion = "1.21.1",
            LoaderType = ModLoaderType.Fabric
        };
        Directory.CreateDirectory(Path.Combine(inst.Directory, "mods"));
        for (var i = 0; i < 12; i++)
            File.WriteAllText(Path.Combine(inst.Directory, "mods", $"m{i}.jar"), "");
        Directory.CreateDirectory(Path.Combine(inst.Directory, "shaderpacks"));
        File.WriteAllText(Path.Combine(inst.Directory, "shaderpacks", "bsl.zip"), "");
        vm.Instances.Add(inst);
        vm.SelectedInstance = inst;

        var win = new Window
        {
            Width = 1160, Height = 760, ShowActivated = false, WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, Left = -4000, Top = -4000
        };
        var host = new Grid();
        win.Content = host;
        win.Show();

        var settings = new SettingsView { DataContext = vm };
        host.Children.Add(settings);
        Pump(win);

        var texts = Texts(settings);
        var buttons = Buttons(settings);
        var checkBoxes = new List<CheckBox>();
        Walk(settings, o => { if (o is CheckBox cb) checkBoxes.Add(cb); });

        // ---- (a) the block actually rendered ----
        Require(texts.Any(t => t.Contains("自动（推荐）")) || checkBoxes.Any(c => (c.Content as string ?? "").Contains("自动（推荐）")),
            "「自动（推荐）」选项没有渲染");
        Require(texts.Any(t => t.Contains("推荐") && (t.Contains("MB"))),
            "推荐说明（MemoryAutoHint）没有渲染");
        Require(buttons.Any(b => (b.Content as string) == "释放启动器占用内存"),
            "「释放启动器占用内存」按钮缺失");

        // ---- (a2) round-85: three-state download policy + live explanation ----
        var policyCombo = FindComboBox(settings, "DownloadSourceOptions");
        Require(policyCombo != null, "下载线路下拉框没有绑定到 DownloadSourceOptions");
        if (policyCombo != null)
        {
            Require(policyCombo.Items.Count == 3, $"下载线路应有 3 个选项，实际 {policyCombo.Items.Count}");
            var labels = policyCombo.Items.Cast<object>().Select(o => o?.ToString() ?? "").ToList();
            Require(labels.Any(l => l.Contains("自动")) && labels.Any(l => l.Contains("官方")) && labels.Any(l => l.Contains("镜像")),
                "三个选项必须分别是自动 / 仅官方 / 仅镜像：" + string.Join(" | ", labels));
        }
        Require(texts.Any(t => t.Contains("下载线路")), "「下载线路」标签没有渲染");
        Require(texts.Any(t => t.Contains("策略：")), "策略说明（当前优先哪条）没有渲染");
        Require(texts.Any(t => t.Contains("当前网络")), "测速结果那一行没有渲染");

        // ---- (b) manual mode: the box is editable, and an oversized value warns ----
        Require(!vm.AutoMemory && vm.MemoryManualEnabled, "默认应是手动模式且输入框可用");
        vm.MemoryMb = 99999;
        Pump(win);
        Require(vm.HasMemoryWarning && vm.MemoryWarningText.Contains("55"),
            "手填超大内存必须给出红色警告");
        Require(texts.Concat(Texts(settings)).Any(t => t.Contains("超过物理内存")),
            "警告文案没有渲染出来");
        vm.MemoryMb = 4096;
        Pump(win);
        Require(!vm.HasMemoryWarning, "4096 MB 不该报警");

        // ---- (c) auto mode: one click computes the number and locks the box ----
        var beforeMb = vm.MemoryMb;
        vm.AutoMemory = true;
        Pump(win);
        var expected = MemoryAllocationPolicy.Recommend(
            MemoryAllocationPolicy.DetectPhysicalMb(), 12, true, true).RecommendedMb;
        Require(vm.MemoryMb == expected,
            $"自动模式应显示推荐值 {expected}，实际 {vm.MemoryMb}");
        Require(vm.MemoryMb != beforeMb || expected == beforeMb,
            "自动模式没有改写输入框里的数字");
        Require(!vm.MemoryManualEnabled, "自动模式下输入框应禁用");
        var box = FindTextBox(settings);
        Require(box != null && !box.IsEnabled, "设置页的输入框没有跟着变灰（IsEnabled 绑定失效）");

        // ---- (d) the manual clean really runs, off the UI thread, and reports ----
        // Direct timing probe first: if the trim itself blocks, the button test
        // below would look like a binding bug.
        {
            var sw = Stopwatch.StartNew();
            var probe = AuroraLauncher.Desktop.Services.MemoryHygiene.Trim(aggressive: true);
            Console.WriteLine($"[mem-smoke] direct trim {sw.ElapsedMilliseconds} ms " +
                              $"{probe.BeforeMb} -> {probe.AfterMb} MB");
        }
        if (buttons.FirstOrDefault(b => (b.Content as string) == "释放启动器占用内存") is { } clean)
        {
            // Exactly what a click does: go through the bound command.
            var beforeMsg = vm.MemoryCleanStatus;
            clean.Command.Execute(null);
            // Wait for COMPLETION (IsCleaningMemory flips back), not merely for a
            // changed message — the "正在释放…" status is set synchronously.
            var clicked = PumpUntil(win, () => !vm.IsCleaningMemory && vm.MemoryCleanStatus != beforeMsg, 20000);
            Console.WriteLine($"[mem-smoke] 按钮路径: 完成={clicked} cleaning={vm.IsCleaningMemory} " +
                              $"msg=「{vm.MemoryCleanStatus}」");
            Require(clicked, "点「释放启动器占用内存」后没有任何结果反馈");
            Require(!vm.MemoryCleanStatus.Contains("失败"), "清理报告了失败: " + vm.MemoryCleanStatus);
            Require(vm.MemoryCleanStatus.Contains("MB") || vm.MemoryCleanStatus.Contains("缓存"),
                "清理结果没有给出可读信息: " + vm.MemoryCleanStatus);
        }

        var relevant = bindingErrors
            .Where(e => e.Contains("Memory") || e.Contains("AutoMemory") || e.Contains("ReleaseIdle"))
            .ToList();
        Require(relevant.Count == 0, "绑定错误: " + string.Join(" | ", relevant.Take(3)));
        Require(bindingErrors.Count == 0, "设置页出现绑定错误: " + string.Join(" | ", bindingErrors.Take(3)));

        Console.WriteLine($"[mem-smoke] 设置页文本 {texts.Count} 条 / 按钮 {buttons.Count} 个 / " +
                          $"复选框 {checkBoxes.Count} 个 / 绑定错误 {bindingErrors.Count} 条 / 清理结果「{vm.MemoryCleanStatus}」");

        // Visual artefact: the smoke can prove the bindings work, but only a
        // picture shows whether the new block is actually readable.
        try
        {
            // The settings page scrolls; bring the memory block into view first,
            // otherwise the picture just shows the storage card.
            FindTextBox(settings)?.BringIntoView();
            var scroller = FindScrollViewer(FindTextBox(settings));
            scroller?.ScrollToVerticalOffset(scroller.VerticalOffset + 285);
            Pump(win);
            Pump(win);
            var shot = Path.Combine(Path.GetTempPath(), "aurora-mem-settings.png");
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)settings.ActualWidth, (int)settings.ActualHeight, 96, 96,
                PixelFormats.Pbgra32);
            rtb.Render(settings);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(shot);
            encoder.Save(fs);
            Console.WriteLine($"[mem-smoke] 截图: {shot}");
        }
        catch (Exception ex) { Console.WriteLine("[mem-smoke] 截图失败: " + ex.Message); }
        if (fails.Count == 0)
        {
            Console.WriteLine("[mem-smoke] SMOKE OK - 自动分配 / 超限警告 / 手动清理全部生效");
            try { Directory.Delete(root, true); } catch { }
            return 0;
        }
        foreach (var f in fails) Console.WriteLine("[mem-smoke] FAIL: " + f);
        return 1;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? node)
    {
        while (node != null && node is not ScrollViewer) node = VisualTreeHelper.GetParent(node);
        return node as ScrollViewer;
    }

    /// <summary>ComboBox whose ItemsSource binds to <paramref name="path"/>.</summary>
    private static ComboBox? FindComboBox(DependencyObject root, string path)
    {
        ComboBox? found = null;
        Walk(root, o =>
        {
            if (found != null || o is not ComboBox cb) return;
            if (cb.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.ParentBinding.Path.Path == path)
                found = cb;
        });
        return found;
    }

    private static TextBox? FindTextBox(DependencyObject root)
    {
        TextBox? found = null;
        Walk(root, o =>
        {
            if (found != null || o is not TextBox t) return;
            // Pick THE memory box by its binding path — the page has other inputs.
            if (t.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "MemoryMb")
                found = t;
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

    /// <summary>Runs queued dispatcher work for real. <c>Dispatcher.Invoke</c> from
    /// this same thread executes inline and does NOT drain the queue — so an
    /// async continuation posted to the sync context would never run and the
    /// smoke would report a false "no feedback".</summary>
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
