using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using AuroraCore;
using AuroraCore.Services;
using AuroraCore.Utils;
using AuroraLauncher.Desktop.Services;
using AuroraLauncher.Desktop.ViewModels;
using AuroraLauncher.Desktop.Views;

namespace AuroraLauncher.Desktop;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    // Round-105: WM_GETMINMAXINFO is the only reliable way to stop a borderless
    // WindowChrome window from covering the taskbar when maximised. The monitor
    // work rect is already in physical pixels — exactly what the message wants.
    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public WinRect Monitor;
        public WinRect Work;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public WinPoint Reserved;
        public WinPoint MaxSize;
        public WinPoint MaxPosition;
        public WinPoint MinTrackSize;
        public WinPoint MaxTrackSize;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private const uint MonitorDefaultToNearest = 2;

    /// <summary>Every visited page, created once and NEVER detached. Keeping
    /// the visual tree alive is the whole point — see PageHostPolicy.</summary>
    private readonly Dictionary<string, UIElement> _pages = new();

    /// <summary>Round-68: navigation cost, measured instead of argued about.
    /// Logged only when a switch exceeds 50 ms, so a user report can carry a
    /// number ("切换页面 Mods 用时 128ms（首次构建）").</summary>
    private readonly UiLatencyTrace _navTrace = new();

    private DispatcherTimer? _warmTimer;
    private MainViewModel? _vm;
    private bool _shuttingDown;

    /// <summary>Round-70: watchdog that attributes UI freezes to GC vs. app work.
    /// Round-69 measured 48–218 ms of "waiting for the UI thread" while the page
    /// itself took 5 ms; without this, every next fix is a guess.</summary>
    private readonly UiStallTrace _stallTrace = new();
    private DispatcherTimer? _stallTimer;
    private long _lastStallLogTick;

    /// <summary>Round-74: tray icon. Null when the user disabled it (or the shell
    /// refused to create one) — every use is null-checked so close falls back to
    /// the round-73 behaviour instead of trapping the window.</summary>
    private TrayIconService? _tray;

    /// <summary>Round-83: while the window sits in the tray, drop the launcher's
    /// own icon cache once (5 min in). Deliberately a timer without any
    /// reclamation of the game process: the JVM is never touched.</summary>
    private DispatcherTimer? _idleTrimTimer;
    private DateTime _hiddenAt;
    private bool _idleTrimmed;

    /// <summary>Round-75: the window is really gone (Closed fired). Restoring it is
    /// impossible, and the process must not keep the single-instance mutex after
    /// this point — see the Closed handler.</summary>
    private bool _closed;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyMaximizedLayout();
        DataContext = new MainViewModel();
        // Sync the actual WPF theme resources with the persisted preference.
        if (DataContext is MainViewModel vm)
        {
            _vm = vm;
            vm.ApplyTheme();
            vm.PropertyChanged += OnVmPropertyChanged;
            // Round-80: glass is applied by the window (it owns the HWND), on the
            // request of the view model. SourceInitialized guarantees a handle.
            vm.GlassModeChanged += (_, mode) => ApplyGlass(mode);
            SourceInitialized += (_, _) =>
            {
                ApplyGlass(vm.CurrentGlassMode);
                // Round-105: answer Windows with the monitor work area so the
                // maximised window never grows over the taskbar.
                (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(ClampMaximizedToWorkArea);
            };
            // Round-104: "跟随系统" must actually follow the OS at runtime.
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.VisualStyle) vm.ApplySystemThemeIfNeeded();
            };
            // The VM navigates to Home in its constructor, before we could
            // subscribe — activate the initial page explicitly.
            ActivatePage(vm.CurrentPageKey, initial: true);
        }
        // Round-67: no BitmapCache exists any more (all DropShadow effects
        // removed), so the DpiChanged cache-rescale walk is gone as well.

        // Round-68: idle pre-warm. A warm page flip measured 0.3 ms; the first
        // BUILD of a page measures 5–60 ms (BAML + templates + first layout).
        // Building the remaining pages one per idle tick moves that cost off the
        // first click of each tab. Hidden (not Collapsed) during the layout pass
        // so templates really are applied.
        _warmTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            // 240 ms apart: a real machine measured 20–120 ms per page, so the
            // warm-up itself must stay well under "one hitch per tick".
            Interval = TimeSpan.FromMilliseconds(240)
        };
        _warmTimer.Tick += (_, _) => WarmOnePage();
        _warmTimer.Start();

        // Round-70: 250 ms watchdog. The tick itself is O(1); a tick that arrives
        // late proves the dispatcher was blocked, and the GC-pause delta inside
        // that gap says whether it was a collection or our own code.
        var stallTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(UiStallTrace.DefaultIntervalMs)
        };
        var watchdogFrom = Environment.TickCount64 + 4000; // startup layout is not a click
        stallTimer.Tick += (_, _) =>
        {
            var now = Environment.TickCount64;
            if (now < watchdogFrom) { _stallTrace.Reset(); return; }
            var sample = _stallTrace.Sample();
            if (sample == null) return;
            if (now - _lastStallLogTick < 1500) return; // one line per stall burst
            _lastStallLogTick = now;
            Logger.Instance.Warn("ui", UiStallTrace.Describe(sample.Value));
        };
        stallTimer.Start();
        _stallTimer = stallTimer;

        if (LauncherConfig.Instance.MinimizeToTrayEnabled)
        {
            try
            {
                _tray = new TrayIconService(() => RestoreFromTray(), ExitFromTray);
                Logger.Instance.Info("App", "托盘图标已启用（关闭窗口行为：" +
                    CloseBehaviorPolicy.DisplayText(LauncherConfig.Instance.CloseAction) + "）");
            }
            catch (Exception ex)
            {
                // No tray (locked-down shell, missing shell32, ...) must never be
                // fatal: the window simply closes like before.
                Logger.Instance.Warn("App", "托盘图标创建失败，关闭窗口将按「询问/退出」处理: " + ex.Message);
                _tray = null;
            }
        }

        Closed += (_, _) =>
        {
            _closed = true;
            _shuttingDown = true;
            _warmTimer?.Stop();
            _stallTimer?.Stop();
            _idleTrimTimer?.Stop();
            _idleTrimTimer = null;
            _tray?.Dispose();
            _tray = null;
            // Round-106 (L3): release the playtime heartbeat timer on shutdown.
            try { _vm?.DisposeResources(); } catch { }

            // Round-75 invariant: this app must never outlive its only window.
            // Round 74 could leave a windowless process alive; it still owned the
            // single-instance mutex, so every later double-click was answered with
            // "switching to the existing window" — and nothing appeared.
            Logger.Instance.Info("App", "主窗口已关闭，退出程序。");
            try { Application.Current?.Shutdown(); } catch { }

            // Round-76: WPF's shutdown can be deferred when the close was started
            // from inside a menu's nested message loop (tray → 退出 did exactly
            // that and left a windowless process behind). Enforce the invariant
            // instead of trusting it: if the process is still here in 4 s, leave.
            var watchdog = new Thread(() =>
            {
                Thread.Sleep(4000);
                try
                {
                    Logger.Instance.Warn("App", "退出超时，强制结束进程（避免残留进程占住单实例锁）。");
                    Logger.Instance.Flush(600);
                }
                catch { }
                Environment.Exit(0);
            })
            { IsBackground = true, Name = "aurora-exit-watchdog" };
            watchdog.Start();
        };

        StateChanged += (_, _) =>
        {
            // Round-74: while hidden/minimized, stop the idle page warm-up and ask
            // the VM to throttle UI refreshes — a background download should cost
            // disk + network, not layout passes nobody can see.
            var visible = IsVisible && WindowState != WindowState.Minimized;
            _vm?.SetWindowVisible(visible);
            if (visible)
            {
                if (_warmTimer is { IsEnabled: false } && PageWarmupPolicy.NextToWarm(_pages.Keys) != null)
                    _warmTimer.Start();
            }
            else
            {
                _warmTimer?.Stop();
            }
        };
    }

    /// <summary>Round-73: never kill three half-finished installs silently.
    /// Asks once; "否" keeps the window and the downloads alive.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        // Already decided (tray → 退出, updater restart, shutdown): just close.
        if (_shuttingDown)
        {
            base.OnClosing(e);
            return;
        }

        // Round-90: closing is another way to lose unsaved settings — ask here
        // too, before the download guard (saving is cheap, and a user who picks
        // 「取消」 here should keep the window without also being asked about
        // downloads).
        if (_vm != null && !_vm.ConfirmCloseWithUnsavedSettings())
        {
            e.Cancel = true;
            base.OnClosing(e);
            return;
        }

        var running = _vm?.DownloadActiveCount ?? 0;
        var queued = _vm?.DownloadQueuedCount ?? 0;
        var trayAvailable = _tray != null && LauncherConfig.Instance.MinimizeToTrayEnabled;
        var decision = CloseBehaviorPolicy.Decide(LauncherConfig.Instance.CloseAction, trayAvailable, running, queued);

        switch (decision.Kind)
        {
            case CloseDecisionKind.HideToTray:
                e.Cancel = true;
                HideToTray();
                break;

            case CloseDecisionKind.Prompt:
                var dialog = new ClosePromptWindow(decision) { Owner = this };
                dialog.ShowDialog();
                if (dialog.Remember && dialog.Choice != CloseChoice.Cancel)
                {
                    var stored = dialog.Choice == CloseChoice.HideToTray ? CloseAction.MinimizeToTray : CloseAction.Exit;
                    LauncherConfig.Instance.CloseAction = stored;
                    LauncherConfig.Instance.Save();
                    Logger.Instance.Info("App", "已记住关闭窗口选择：" + CloseBehaviorPolicy.DisplayText(stored));
                }
                if (dialog.Choice == CloseChoice.HideToTray)
                {
                    e.Cancel = true;
                    HideToTray();
                }
                else if (dialog.Choice == CloseChoice.Exit)
                {
                    _shuttingDown = true;
                    // Cancel first so the engine's .part cleanup can run before exit.
                    _vm?.CancelAllDownloads();
                }
                else
                {
                    e.Cancel = true;
                }
                break;

            default: // Exit
                _shuttingDown = true;
                _vm?.CancelAllDownloads();
                break;
        }
        base.OnClosing(e);
    }

    /// <summary>Hides to the tray, keeping downloads alive.</summary>
    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
        _warmTimer?.Stop();
        _vm?.SetWindowVisible(false);
        StartIdleTrimWatch();
        var hint = CloseBehaviorPolicy.BuildTrayHint(_vm?.DownloadActiveCount ?? 0, _vm?.DownloadQueuedCount ?? 0);
        _tray?.ShowBalloon("AuroraLauncher 仍在运行", hint);
        Logger.Instance.Info("App", "窗口已最小化到托盘：" + hint);
    }

    /// <summary>Round-83: 5 minutes after hiding, clear the launcher's own caches
    /// exactly once. No GC and no other processes — returning to the window must
    /// never wait on anything.</summary>
    private void StartIdleTrimWatch()
    {
        if (!LauncherConfig.Instance.ReleaseIdleMemory) return;
        _hiddenAt = DateTime.UtcNow;
        _idleTrimmed = false;
        _idleTrimTimer ??= CreateIdleTrimTimer();
        _idleTrimTimer.Start();
    }

    private DispatcherTimer CreateIdleTrimTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
        timer.Tick += async (_, _) =>
        {
            if (_closed || _vm == null) { timer.Stop(); return; }
            if (IsVisible) { timer.Stop(); return; }
            if (_idleTrimmed) return;
            if (DateTime.UtcNow - _hiddenAt < TimeSpan.FromMinutes(5)) return;
            _idleTrimmed = true;
            timer.Stop();
            // Round-104: async void has no caller to catch a throw — one
            // unhandled exception here would kill the whole launcher.
            try { await _vm.TrimIdleMemoryAsync(); }
            catch (Exception ex) { Logger.Instance.Warn("App", "空闲内存清理失败: " + ex.Message); }
        };
        return timer;
    }

    /// <summary>Brings the window back (tray double-click, or a second EXE launch).
    /// Returns true only when a window is being shown — the caller turns that into
    /// the activation ack, and false makes the second instance open its own window
    /// instead of exiting into nothing.</summary>
    public bool RestoreFromTray()
    {
        try
        {
            if (!Dispatcher.CheckAccess())
            {
                // Marshalling is the ONLY safe way to touch a WPF window from the
                // listener thread (round 76: touching it directly threw
                // "调用线程无法访问此对象" and swallowed the activation entirely).
                Dispatcher.BeginInvoke(new Action(() => RestoreFromTray()));
                return !_closed;
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn("App", "无法调度窗口恢复: " + ex.Message);
            return false;
        }

        if (_closed)
        {
            // Nothing to restore. The caller (a second EXE launch) gets no ack and
            // starts its own window; shutting down here also releases the mutex
            // immediately instead of on process teardown.
            Logger.Instance.Warn("App", "收到唤醒请求，但主窗口已关闭，无法恢复。");
            try { Application.Current?.Shutdown(); } catch { }
            return false;
        }

        try
        {
            _idleTrimTimer?.Stop();
            _idleTrimmed = false;
            ShowInTaskbar = true;
            Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            // Windows may refuse SetForegroundWindow from a background process; the
            // topmost toggle is the standard workaround and is reverted immediately.
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                SingleInstanceGuard.ShowWindow(handle, SingleInstanceGuard.SwRestore);
                if (!SingleInstanceGuard.SetForegroundWindow(handle))
                {
                    SingleInstanceGuard.SetWindowPos(handle, SingleInstanceGuard.HwndTopmost, 0, 0, 0, 0,
                        SingleInstanceGuard.SwpNoMove | SingleInstanceGuard.SwpNoSize | SingleInstanceGuard.SwpNoActivate);
                    SingleInstanceGuard.SetWindowPos(handle, SingleInstanceGuard.HwndNotTopmost, 0, 0, 0, 0,
                        SingleInstanceGuard.SwpNoMove | SingleInstanceGuard.SwpNoSize | SingleInstanceGuard.SwpNoActivate);
                }
            }
            _vm?.SetWindowVisible(true);
            if (_warmTimer is { IsEnabled: false } && PageWarmupPolicy.NextToWarm(_pages.Keys) != null)
                _warmTimer.Start();
            return true;
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn("App", "恢复窗口失败，将打开新窗口: " + ex.Message);
            return false;
        }
    }

    /// <summary>Tray menu → 退出: cancel downloads (so .part cleanup runs), then close.</summary>
    private void ExitFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ExitFromTray));
            return;
        }

        // Round-90: the tray menu is a close path too. Before this, a user who
        // chose "直接退出" got a silent kill of every running download from here —
        // exactly what the setting's own label promises will not happen.
        if (!ConfirmExitWithActiveJobs("托盘菜单退出")) return;
        if (_vm != null && !_vm.ConfirmCloseWithUnsavedSettings()) return;

        _shuttingDown = true;
        _vm?.CancelAllDownloads();
        if (_closed)
        {
            try { Application.Current?.Shutdown(); } catch { }
            return;
        }
        Close();
    }

    /// <summary>Round-90: one gate for every "really exit" path. True = the user
    /// agreed to interrupt the downloads (or there was nothing to interrupt).</summary>
    private bool ConfirmExitWithActiveJobs(string trigger)
    {
        var running = _vm?.DownloadActiveCount ?? 0;
        var queued = _vm?.DownloadQueuedCount ?? 0;
        if (!CloseGuardPolicy.ShouldPrompt(running, queued, alreadyConfirmed: false)) return true;

        var answer = MessageBox.Show(this, CloseGuardPolicy.BuildMessage(running, queued),
            "还有下载没完成", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        var exit = answer == MessageBoxResult.Yes;
        Logger.Instance.Info("App",
            $"{trigger}：{running + queued} 个任务未完成，用户选择{(exit ? "退出" : "继续下载")}");
        return exit;
    }

    /// <summary>Round-77: a .mrpack dragged onto the window is installed — the
    /// "send your friend a pack" flow has to end in something that works without
    /// reading instructions.</summary>
    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasMrpack(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (!HasMrpack(e.Data)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        var pack = files.FirstOrDefault(f => f.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase));
        if (pack == null) return;
        e.Handled = true;
        Logger.Instance.Info("App", "拖入整合包: " + pack);
        if (_vm != null)
        {
            _ = ImportMrpackSafelyAsync(_vm, pack);
        }
    }

    private static async Task ImportMrpackSafelyAsync(MainViewModel vm, string pack)
    {
        try { await vm.ImportMrpackAsync(pack).ConfigureAwait(true); }
        catch (Exception ex) { Logger.Instance.Warn("App", "拖入整合包失败: " + ex.Message); }
    }

    private static bool HasMrpack(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)) return false;
        var files = data.GetData(DataFormats.FileDrop) as string[];
        return files != null && files.Any(f => f.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase));
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentPageKey) && _vm != null)
            ActivatePage(_vm.CurrentPageKey);
        else if (e.PropertyName is nameof(MainViewModel.DownloadActiveCount) or nameof(MainViewModel.DownloadQueuedCount))
            _tray?.UpdateStatus(_vm?.DownloadActiveCount ?? 0, _vm?.DownloadQueuedCount ?? 0);
        else if (e.PropertyName == nameof(MainViewModel.IsPaletteOpen) && _vm?.IsPaletteOpen == true)
        {
            // Focus must wait for the overlay to become visible, otherwise the
            // TextBox is still Collapsed and Focus() silently does nothing.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                PaletteInput.Focus();
                PaletteInput.SelectAll();
            }), DispatcherPriority.Input);
            SetGlassBlur(true);
        }
        else if (e.PropertyName == nameof(MainViewModel.IsPaletteOpen))
        {
            SetGlassBlur(false);
        }
        else if (e.PropertyName == nameof(MainViewModel.DarkTheme))
        {
            // SwitchTheme() swaps the theme dictionary synchronously right after
            // raising this; re-applying at Background priority runs after it.
            Dispatcher.BeginInvoke(new Action(() => ApplyGlass(_vm?.CurrentGlassMode ?? GlassMode.Off)),
                DispatcherPriority.Background);
        }
    }

    // ==================== Round-80: 液态玻璃 ====================

    /// <summary>
    /// The one place that touches the window for glass. Round-81 redesign: the
    /// glass look is a wallpaper + dark translucent cards drawn INSIDE the
    /// window (fig-1), so there is no DWM material any more — a backdrop behind
    /// an opaque wallpaper would be invisible anyway.
    /// </summary>
    private void ApplyGlass(GlassMode requested)
    {
        var dark = _vm?.DarkTheme ?? true;
        var effective = GlassEffectPolicy.Resolve(requested, 0);

        GlassVisuals.Restore();
        GlassWallpaper.Visibility = Visibility.Collapsed;
        GlassNoiseOverlay.Visibility = Visibility.Collapsed;
        SetGlassBlur(false);

        switch (effective)
        {
            case GlassMode.Faux:
                GlassWallpaper.Fill = GlassVisuals.BuildWallpaperBrush(dark);
                GlassWallpaper.Visibility = Visibility.Visible;
                GlassNoiseOverlay.Fill = GlassVisuals.Noise;
                GlassNoiseOverlay.Visibility = Visibility.Visible;
                GlassVisuals.ApplyTranslucentSurfaces(dark);
                break;

            case GlassMode.Local:
                GlassVisuals.ApplyTranslucentSurfaces(dark);
                break;

            default: // Off
                break;
        }

        UpdateWindowClip();
        Logger.Instance.Info("Glass", $"界面材质：请求 {GlassEffectPolicy.DisplayName(requested)}，" +
                                     $"生效 {GlassEffectPolicy.DisplayName(effective)}");
    }

    // ---- Round-81: 无边框窗口的自绘边框 ----

    /// <summary>Rounded corners are ours to draw now: clip the root grid to the
    /// same radius as the edge highlight. Maximised windows go square (a rounded
    /// corner cut into the screen edge looks broken).</summary>
    private void UpdateWindowClip()
    {
        var radius = WindowState == WindowState.Maximized ? 0 : 12;
        WindowRoot.Clip = new RectangleGeometry(
            new Rect(0, 0, Math.Max(0, WindowRoot.ActualWidth), Math.Max(0, WindowRoot.ActualHeight)),
            radius, radius);
        WindowEdge.CornerRadius = new CornerRadius(radius);
        WindowEdge.BorderThickness = new Thickness(radius == 0 ? 0 : 1);
    }

    private void OnWindowFrameSizeChanged(object sender, SizeChangedEventArgs e) => UpdateWindowClip();

    private void OnWindowStateChanged(object sender, EventArgs e)
    {
        if (MaximizeGlyph != null)
            MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        ApplyMaximizedLayout();
    }

    /// <summary>Round-105: answer Windows' maximise query with the monitor WORK
    /// area (not the whole screen). Assigning Left/Top/Width/Height in the
    /// Maximized state is a no-op — WPF hands that geometry to the OS — so the
    /// taskbar clamp lives in this hook. It must not depend on WindowState:
    /// Windows sends WM_GETMINMAXINFO *before* the state flips to Maximized.
    /// </summary>
    private IntPtr ClampMaximizedToWorkArea(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WindowLayoutPolicy.WmGetMinMaxInfo) return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
        if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        var mmi = (MinMaxInfo)Marshal.PtrToStructure(lParam, typeof(MinMaxInfo))!;
        mmi.MaxPosition.X = info.Work.Left;
        mmi.MaxPosition.Y = info.Work.Top;
        mmi.MaxSize.X = info.Work.Right - info.Work.Left;
        mmi.MaxSize.Y = info.Work.Bottom - info.Work.Top;
        Marshal.StructureToPtr(mmi, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>Round-104: borderless + WindowChrome leaves the 6 px resize
    /// border in Maximized state, which pushes content past the screen edge.
    /// Drop the border only; the work-area clamp itself is done by the
    /// WM_GETMINMAXINFO hook above (see round-105).</summary>
    private void ApplyMaximizedLayout()
    {
        var maximized = WindowState == WindowState.Maximized;
        var chrome = WindowChrome.GetWindowChrome(this);
        if (chrome != null)
            chrome.ResizeBorderThickness = new Thickness(WindowLayoutPolicy.ResizeBorder(maximized));
        UpdateWindowClip();
    }

    /// <summary>Drag anywhere on the title strip / sidebar brand. Buttons and
    /// text boxes mark the event handled, so they never start a drag.</summary>
    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }
        try { DragMove(); } catch { /* released too early — nothing to do */ }
    }

    private void OnWindowMinimize(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnWindowMaximizeToggle(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnWindowClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>Blur is allowed ONLY behind the transient palette and only in
    /// "Local" mode. PageHost is static while the palette is open, so the blur
    /// renders once instead of per frame — see GlassGuardPolicy.</summary>
    private void SetGlassBlur(bool on)
    {
        var mode = _vm?.CurrentGlassMode ?? GlassMode.Off;
        var wanted = on && GlassEffectPolicy.UsesTransientBlur(mode);
        var target = wanted ? GlassBlurEffect : null;
        if (ReferenceEquals(PageHost.Effect, target)) return;
        PageHost.Effect = target;
    }

    /// <summary>One frozen effect instance reused forever — allocating a fresh
    /// one per toggle would rebuild the shader every time.</summary>
    private static readonly BlurEffect GlassBlurEffect = CreateGlassBlur();

    private static BlurEffect CreateGlassBlur()
    {
        var glassBlur = new BlurEffect
        {
            Radius = 14,
            KernelType = KernelType.Gaussian,
            RenderingBias = RenderingBias.Performance
        };
        glassBlur.Freeze();
        return glassBlur;
    }

    // ---- Round-79: Ctrl+K 全局搜索的键盘入口 ----

    /// <summary>Tunnelling handler on the window: Ctrl+K opens the palette from
    /// ANY page (including while a TextBox has focus), and while it is open it
    /// owns ↑ ↓ Enter Esc. A single handler keeps the shortcuts in one place.
    /// </summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (ctrl && e.Key == Key.K)
        {
            _vm.TogglePalette();
            e.Handled = true;
            return;
        }

        if (!_vm.IsPaletteOpen) return;
        switch (e.Key)
        {
            case Key.Escape:
                _vm.ClosePalette();
                e.Handled = true;
                break;
            case Key.Down:
                _vm.MovePaletteSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                _vm.MovePaletteSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                _vm.RunPalette(_vm.PaletteSelected);
                e.Handled = true;
                break;
        }
    }

    private void OnPaletteItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm?.PaletteSelected == null) return;
        _vm.RunPalette(_vm.PaletteSelected);
        e.Handled = true;
    }

    private void OnPaletteBackdropClick(object sender, MouseButtonEventArgs e)
    {
        _vm?.ClosePalette();
        e.Handled = true;
    }

    /// <summary>Round-65: O(1) navigation. A page is built only on its first
    /// visit, then stays in PageHost forever; switching flips Visibility
    /// instead of rebuilding the visual tree.
    /// Side effect: Loaded fires once per page (no more Unloaded storms), and
    /// hidden pages keep their subscriptions — see the IsVisible guards in
    /// LogsView / ModsView.
    /// Round-68: the cost is measured from click to the first dispatcher turn
    /// AFTER layout, i.e. the moment the new page is actually on screen.</summary>
    private void ActivatePage(string key, bool initial = false)
    {
        var target = NavigationPolicy.Normalize(key);
        var clock = Stopwatch.StartNew();
        var built = false;

        if (PageHostPolicy.ShouldCreate(_pages.Keys, target))
        {
            var page = CreatePage(target);
            page.Visibility = Visibility.Collapsed;
            _pages[target] = page;
            PageHost.Children.Add(page);
            built = true;
        }
        foreach (var (pageKey, page) in _pages)
            page.Visibility = PageHostPolicy.IsVisible(target, pageKey)
                ? Visibility.Visible
                : Visibility.Collapsed;

        var flipMs = clock.Elapsed.TotalMilliseconds;
        ReportNavigation(target, built, clock, flipMs, initial);

        // A click resets the idle window: pre-warming while the user is
        // clicking through tabs would BE the jank we are removing.
        var warm = _warmTimer;
        if (warm is { IsEnabled: true } && PageWarmupPolicy.NextToWarm(_pages.Keys) != null)
        {
            warm.Stop();
            warm.Start();
        }
    }

    /// <summary>Round-69: three-phase measurement. The flip is timed
    /// synchronously; the Render-priority turn marks "layout done" (the layout
    /// manager queues its pass at Render priority before we queue ours); the
    /// Background turn marks "the UI thread is free again". The gap between the
    /// last two is OTHER work — a page that flips in 0.3 ms must never be
    /// reported as slow (round-68 did exactly that in the user's log).</summary>
    private void ReportNavigation(string target, bool built, Stopwatch clock, double flipMs, bool initial)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var toPaint = clock.Elapsed.TotalMilliseconds - flipMs;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var toIdle = clock.Elapsed.TotalMilliseconds - flipMs;
                var timing = NavigationLatencyPolicy.FromPhases(flipMs, toPaint, toIdle);
                var slow = _navTrace.Record("nav:" + target, timing.TotalMs);
                var kind = NavigationLatencyPolicy.Classify(timing);
                var message = NavigationLatencyPolicy.Describe(target, timing, built, initial);

                if (kind != NavigationSlowKind.None && !initial && slow)
                    Logger.Instance.Warn("ui", message);
                else
                    Logger.Instance.Trace("ui", message);
            }), DispatcherPriority.Background);
        }), DispatcherPriority.Render);
    }

    private UIElement CreatePage(string target) => target switch
    {
        "Download" => new DownloadView(),
        "Instances" => new InstancesView(),
        "Mods" => new ModsView(),
        "Downloads" => new DownloadsView(),
        "Settings" => new SettingsView(),
        "Logs" => new LogsView(),
        "Changelog" => new ChangelogView(),
        "Help" => new HelpView(),
        _ => new HomeView()
    };

    /// <summary>Builds and pre-lays-out ONE not-yet-created page per idle tick.
    /// Never runs while installing/downloading (that progress work owns the UI
    /// thread) and never while shutting down.</summary>
    private void WarmOnePage()
    {
        if (_vm == null) return;
        if (!PageWarmupPolicy.ShouldWarmNow(_vm.IsBusy, _vm.Download.Active, _shuttingDown)) return;

        var next = PageWarmupPolicy.NextToWarm(_pages.Keys);
        if (next == null)
        {
            _warmTimer?.Stop();
            return;
        }

        var clock = Stopwatch.StartNew();
        var page = CreatePage(next);
        page.Visibility = Visibility.Hidden; // Hidden still measures/arranges
        _pages[next] = page;
        PageHost.Children.Add(page);
        PageHost.UpdateLayout();
        page.Visibility = Visibility.Collapsed; // Collapsed is skipped by layout
        clock.Stop();

        Logger.Instance.Trace("ui", $"空闲预热页面 {next} 完成，用时 {clock.Elapsed.TotalMilliseconds:F0}ms");
    }
}
