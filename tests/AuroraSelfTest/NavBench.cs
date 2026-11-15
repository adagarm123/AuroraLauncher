using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AuroraLauncher.Desktop.Converters;
using AuroraLauncher.Desktop.ViewModels;
using AuroraLauncher.Desktop.Views;

namespace AuroraSelfTest;

/// <summary>Opt-in layout benchmark (round-68). Round-65 made pages resident and
/// round-67 removed every effect/animation, so navigation no longer crashes —
/// but a residual hitch remains. Instead of guessing again (round-66 proved how
/// expensive a wrong guess is), this harness measures the REAL cost of a
/// Collapsed → Visible flip for every page in the real window size, using the
/// real style dictionary and a real MainViewModel.
///
/// Invoked ONLY with --nav-bench; never in unit / all modes (it needs an STA
/// thread, a desktop window and a writable profile).</summary>
internal static class NavBench
{
    private static readonly (string Key, Func<UserControl> Create)[] Pages =
    {
        ("Home", () => new HomeView()),
        ("Download", () => new DownloadView()),
        ("Instances", () => new InstancesView()),
        ("Mods", () => new ModsView()),
        ("Downloads", () => new DownloadsView()),
        ("Settings", () => new SettingsView()),
        ("Logs", () => new LogsView()),
        ("Changelog", () => new ChangelogView()),
        ("Help", () => new HelpView())
    };

    public static int Run()
    {
        var code = -1;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                LoadResources(app);

                MainViewModel? vm = null;
                try
                {
                    vm = new MainViewModel();
                    SeedRealisticData(vm);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[bench] WARN: MainViewModel ctor failed, benching empty pages: " + ex.Message);
                }

                var host = new Grid();
                var win = new Window
                {
                    Width = 1160,
                    Height = 720,
                    Content = host,
                    ShowActivated = false,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    Left = -4000,
                    Top = -4000
                };
                win.Show();
                Pump(win);

                // Throwaway pass: the very first page in a process pays JIT +
                // BAML costs that would otherwise be attributed to "Home".
                var throwaway = new HomeView();
                host.Children.Add(throwaway);
                win.UpdateLayout();
                host.Children.Remove(throwaway);

                var pages = new Dictionary<string, UserControl>(StringComparer.Ordinal);
                var coldCreate = new Dictionary<string, double>(StringComparer.Ordinal);
                var coldShow = new Dictionary<string, double>(StringComparer.Ordinal);
                var warmShow = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var (key, create) in Pages)
                {
                    // A) the OLD behaviour: build, then first show.
                    var swBuild = Stopwatch.StartNew();
                    var cold = create();
                    cold.DataContext = vm;
                    cold.Visibility = Visibility.Collapsed;
                    host.Children.Add(cold);
                    win.UpdateLayout();
                    swBuild.Stop();
                    coldCreate[key] = swBuild.Elapsed.TotalMilliseconds;

                    var swColdShow = Stopwatch.StartNew();
                    cold.Visibility = Visibility.Visible;
                    win.UpdateLayout();
                    swColdShow.Stop();
                    coldShow[key] = swColdShow.Elapsed.TotalMilliseconds;
                    host.Children.Remove(cold);

                    // B) the NEW behaviour: idle pre-warm (Hidden -> layout ->
                    // Collapsed), then the first real show.
                    var warm = create();
                    warm.DataContext = vm;
                    warm.Visibility = Visibility.Hidden;
                    host.Children.Add(warm);
                    win.UpdateLayout();
                    warm.Visibility = Visibility.Collapsed;

                    var swWarmShow = Stopwatch.StartNew();
                    warm.Visibility = Visibility.Visible;
                    win.UpdateLayout();
                    swWarmShow.Stop();
                    warmShow[key] = swWarmShow.Elapsed.TotalMilliseconds;

                    pages[key] = warm;
                }
                Pump(win);
                Console.WriteLine("[bench] pages created: " + pages.Count +
                                  " / window " + (int)win.ActualWidth + "x" + (int)win.ActualHeight);

                Console.WriteLine("[bench] key           visuals  items  cold-build  cold-1st-show  warm-1st-show  steady-median  steady-max  (ms)");
                foreach (var (key, _) in Pages)
                {
                    var samples = new List<double>();
                    for (var i = 0; i < 9; i++)
                    {
                        SetOnly(pages, "Home" == key ? "Help" : "Home");
                        Pump(win);
                        var sw = Stopwatch.StartNew();
                        SetOnly(pages, key);
                        win.UpdateLayout();
                        sw.Stop();
                        samples.Add(sw.Elapsed.TotalMilliseconds);
                    }
                    samples.Sort();
                    var visualCount = CountVisuals(pages[key]);
                    var itemCount = CountRealizedItems(pages[key]);
                    Console.WriteLine($"[bench] {key,-12} {visualCount,7} {itemCount,6} {coldCreate[key],11:F1} {coldShow[key],14:F1} {warmShow[key],14:F1} {samples[samples.Count / 2],14:F1} {samples[^1],11:F1}");
                }

                BenchFrameLatency(win, pages);

                // Round-69 verification: the Logs feed must stay frozen while
                // the page is hidden and publish the whole backlog in ONE Reset
                // when it opens. Seeded rows are cleared first so the counts are
                // unambiguous.
                if (vm != null)
                {
                    vm.Logs.Replace(Array.Empty<string>());
                    for (var i = 0; i < 50; i++)
                        AuroraCore.Utils.Logger.Instance.Info("bench", $"log-feed-{i:00}");
                    Pump(win);
                    Console.WriteLine($"[bench] Logs feed while hidden: {vm.Logs.Count} bound " +
                                      $"(page={vm.CurrentPageKey}) — expected 0");

                    var stopwatch = Stopwatch.StartNew();
                    vm.Navigate("Logs");
                    Pump(win);
                    stopwatch.Stop();
                    var ok = vm.Logs.Count == 50 ? "OK" : "UNEXPECTED";
                    Console.WriteLine($"[bench] Logs feed after opening: {vm.Logs.Count} bound, " +
                                      $"published in {stopwatch.Elapsed.TotalMilliseconds:F1}ms — expected 50 [{ok}]");
                }

                // Round-71 verification: the Download page's version list must
                // never collapse into "two dead mirror routes". Prints what the
                // user would see in the status line (including the offline-cache
                // wording) after a real manifest fetch attempt.
                if (vm != null)
                {
                    vm.Navigate("Download");
                    _ = vm.LoadVersionsAsync();
                    for (var i = 0; i < 60; i++)
                    {
                        Pump(win);
                        Thread.Sleep(100);
                    }
                    Console.WriteLine($"[bench] versions after load: {vm.Versions.Count} — status: {vm.Status}");
                }

                Console.WriteLine("[bench] BENCH OK");
                code = 0;
                win.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[bench] EXCEPTION: " + ex);
                code = 1;
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(120)))
        {
            Console.WriteLine("[bench] TIMEOUT - UI thread blocked");
            return 2;
        }
        return code;
    }

    /// <summary>Measures how long it takes until the next composed frame after a
    /// flip. Layout says nothing about the render pass: if a flip costs 0.3 ms of
    /// layout but 40 ms until the frame lands, the hitch is render-side.</summary>
    private static void BenchFrameLatency(Window win, Dictionary<string, UserControl> pages)
    {
        var clock = Stopwatch.StartNew();
        var pending = false;
        var flipAt = 0.0;
        var last = -1.0;
        EventHandler onRendering = (_, _) =>
        {
            if (!pending) return;
            pending = false;
            last = clock.Elapsed.TotalMilliseconds - flipAt;
        };
        System.Windows.Media.CompositionTarget.Rendering += onRendering;

        Console.WriteLine("[bench] key           frame-latency median / max (ms, flip -> next composed frame)");
        foreach (var (key, _) in Pages)
        {
            var samples = new List<double>();
            for (var i = 0; i < 6; i++)
            {
                SetOnly(pages, "Home" == key ? "Help" : "Home");
                Pump(win);
                last = -1;
                flipAt = clock.Elapsed.TotalMilliseconds;
                pending = true;
                SetOnly(pages, key);
                // Give the media context a chance to produce the frame.
                var deadline = clock.Elapsed.TotalMilliseconds + 500;
                while (last < 0 && clock.Elapsed.TotalMilliseconds < deadline)
                    win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                if (last >= 0) samples.Add(last);
            }
            if (samples.Count == 0)
            {
                Console.WriteLine($"[bench] {key,-12} (no frame observed)");
                continue;
            }
            samples.Sort();
            Console.WriteLine($"[bench] {key,-12} {samples[samples.Count / 2],10:F1} / {samples[^1],6:F1}");
        }
        System.Windows.Media.CompositionTarget.Rendering -= onRendering;
    }

    /// <summary>Real-world volumes: a full Mojang manifest (~100 versions), a log
    /// buffer capped at 600 lines and a dozen instances. The empty-page bench
    /// measured 0.1 ms and told us nothing; these are the lists a user actually
    /// sees when they click a tab.</summary>
    private static void SeedRealisticData(MainViewModel vm)
    {
        for (var i = 0; i < 103; i++)
        {
            vm.Versions.Add(new AuroraCore.Models.VersionManifestEntry
            {
                Id = "1." + (21 - i % 20) + "." + i,
                Type = i % 6 == 0 ? "snapshot" : "release",
                ReleaseTime = "2026-01-01T00:00:00Z"
            });
        }
        for (var i = 0; i < 600; i++)
            vm.Logs.Add($"[Info] [12:00:{i % 60:00}] line {i} — 正在准备资源文件，共 {i * 7} 个");
        for (var i = 0; i < 12; i++)
        {
            vm.Instances.Add(new AuroraCore.Models.GameInstance
            {
                Id = "bench" + i,
                Name = "1.20.1 Fabric 整合包 " + i,
                MinecraftVersion = "1.20.1",
                Notes = "用于基准测试的实例，带有一些较长的描述文本以触发换行测量。"
            });
        }
    }

    /// <summary>Mirrors App.xaml: theme brushes + control styles + the converters
    /// every page references. Without the brushes every DynamicResource lookup
    /// falls back to nothing and the measured tree would be unrealistically cheap.</summary>
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

    private static void SetOnly(Dictionary<string, UserControl> pages, string key)
    {
        foreach (var (pageKey, page) in pages)
            page.Visibility = pageKey == key ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Runs pending dispatcher work down to Loaded so a visibility flip
    /// has fully settled before the next sample.</summary>
    private static void Pump(Window win)
    {
        win.UpdateLayout();
        win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static int CountVisuals(DependencyObject root)
    {
        var n = 0;
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            n++;
            var count = VisualTreeHelper.GetChildrenCount(d);
            for (var i = 0; i < count; i++) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
        return n;
    }

    private static int CountRealizedItems(DependencyObject root)
    {
        var n = 0;
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is ListBoxItem) n++;
            var count = VisualTreeHelper.GetChildrenCount(d);
            for (var i = 0; i < count; i++) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
        return n;
    }
}
