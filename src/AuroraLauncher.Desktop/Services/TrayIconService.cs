using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using AuroraCore;
using AuroraCore.Services;

namespace AuroraLauncher.Desktop.Services;

/// <summary>
/// Round-74: the tray icon, implemented directly on <c>Shell_NotifyIcon</c>.
///
/// The obvious route — <c>System.Windows.Forms.NotifyIcon</c> — costs ~10 MB in
/// a self-contained single-file build (65.2 MB → 75.8 MB measured in this very
/// round). Shipping 10 MB of WinForms to every user, in a release whose headline
/// feature is "download less", is the wrong trade. A message-only window from
/// WPF's own <see cref="HwndSource"/> plus one P/Invoke does the same job with no
/// extra runtime.
///
/// Lifetime stays owned by MainWindow: this class only offers 显示 / 退出 and the
/// balloon that explains what happened when the window disappeared.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const int WM_TRAYMESSAGE = 0x8000 + 1; // WM_APP + 1
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;

    private const int NIM_ADD = 0x0;
    private const int NIM_MODIFY = 0x1;
    private const int NIM_DELETE = 0x2;
    private const int NIF_MESSAGE = 0x01;
    private const int NIF_ICON = 0x02;
    private const int NIF_TIP = 0x04;
    private const int NIF_INFO = 0x10;
    private const int NIIF_INFO = 0x01;

    private readonly Action _showWindow;
    private readonly Action _exitApplication;
    private readonly HwndSource _source;
    private readonly IntPtr _icon;
    private readonly uint _taskbarCreatedMessage;
    private ContextMenu? _menu;
    private string _status = "";
    private bool _added;
    private bool _disposed;

    public TrayIconService(Action showWindow, Action exitApplication)
    {
        _showWindow = showWindow;
        _exitApplication = exitApplication;
        _icon = LoadAppIcon();

        var parameters = new HwndSourceParameters("AuroraLauncherTray")
        {
            WindowStyle = 0,
            ParentWindow = HwndMessage // message-only: invisible, but receives shell callbacks
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        // explorer.exe restarts (crash, "restart explorer") broadcast this; without
        // re-adding, the icon silently disappears for the rest of the session.
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        var data = NewData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_TRAYMESSAGE;
        data.hIcon = _icon;
        data.szTip = BuildTooltip(0, 0);
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
        if (!_added)
            throw new InvalidOperationException("Shell_NotifyIcon(NIM_ADD) 失败，无法创建托盘图标。");
    }

    /// <summary>Truncated by Windows at 63 chars — keep it short.</summary>
    private static string BuildTooltip(int running, int queued)
    {
        var total = Math.Max(0, running) + Math.Max(0, queued);
        var suffix = total > 0 ? $" · {total} 个下载中" : "";
        var text = $"AuroraLauncher 第 {BuildInfo.CurrentRound} 轮{suffix}";
        return text.Length <= 120 ? text : text[..120];
    }

    public void UpdateStatus(int runningJobs, int queuedJobs)
    {
        if (_disposed || !_added) return;
        var text = BuildTooltip(runningJobs, queuedJobs);
        if (text == _status) return;
        _status = text;
        try
        {
            var data = NewData();
            data.uFlags = NIF_TIP;
            data.szTip = text;
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
        catch { /* the tray must never throw into a VM timer */ }
    }

    public void ShowBalloon(string title, string text)
    {
        if (_disposed || !_added) return;
        try
        {
            var data = NewData();
            data.uFlags = NIF_INFO;
            data.szInfoTitle = Clip(title, 63);
            data.szInfo = Clip(text, 255);
            data.dwInfoFlags = NIIF_INFO;
            data.uTimeoutOrVersion = 4000;
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
        catch { /* notifications can be disabled by policy */ }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYMESSAGE)
        {
            var evt = lParam.ToInt32() & 0xFFFF;
            if (evt == WM_LBUTTONDBLCLK || evt == WM_LBUTTONUP)
            {
                _showWindow();
                handled = true;
            }
            else if (evt == WM_RBUTTONUP || evt == WM_CONTEXTMENU)
            {
                ShowMenu();
                handled = true;
            }
            return IntPtr.Zero;
        }

        if (_taskbarCreatedMessage != 0 && (uint)msg == _taskbarCreatedMessage)
        {
            // Explorer came back: put the icon back where it belongs.
            _added = false;
            var data = NewData();
            data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            data.uCallbackMessage = WM_TRAYMESSAGE;
            data.hIcon = _icon;
            data.szTip = _status.Length > 0 ? _status : BuildTooltip(0, 0);
            _added = Shell_NotifyIcon(NIM_ADD, ref data);
            return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        // Built per click: a ContextMenu kept alive across hides can end up with
        // a stale placement and refuses to reopen.
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, StaysOpen = false };

        var open = new MenuItem { Header = "显示主窗口", FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) =>
        {
            menu.IsOpen = false;
            _source.Dispatcher.BeginInvoke(new Action(_showWindow), DispatcherPriority.Background);
        };
        menu.Items.Add(open);

        menu.Items.Add(new Separator());
        var status = new MenuItem { Header = _status.Length > 0 ? _status : BuildTooltip(0, 0), IsEnabled = false };
        menu.Items.Add(status);

        menu.Items.Add(new Separator());
        var exit = new MenuItem { Header = "退出启动器" };
        exit.Click += (_, _) =>
        {
            // Round-76: do NOT close the main window from inside the menu's nested
            // message loop — WPF's shutdown can be deferred that way, leaving a
            // windowless process that still owns the single-instance mutex (the
            // exact path that produced "双击打不开" after 最小化到托盘 → 退出).
            menu.IsOpen = false;
            _source.Dispatcher.BeginInvoke(new Action(_exitApplication), DispatcherPriority.Background);
        };
        menu.Items.Add(exit);

        _menu = menu;
        menu.IsOpen = true;
    }

    private NOTIFYICONDATA NewData()
    {
        return new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _source.Handle,
            uID = 1,
            szTip = "",
            szInfo = "",
            szInfoTitle = ""
        };
    }

    private static string Clip(string value, int max)
    {
        var clean = (value ?? "").Replace("\r", " ").Replace("\n", " ");
        return clean.Length <= max ? clean : clean[..max];
    }

    private static IntPtr LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) &&
                ExtractIconEx(exe, 0, out var large, out var small, 1) > 0)
            {
                if (large != IntPtr.Zero) DestroyIcon(large);
                if (small != IntPtr.Zero) return small;
            }
        }
        catch { /* fall back to the generic icon */ }
        return LoadIcon(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _menu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
            _menu = null;
            if (_added)
            {
                var data = NewData();
                Shell_NotifyIcon(NIM_DELETE, ref data);
                _added = false;
            }
            _source.RemoveHook(WndProc);
            _source.Dispose();
            if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        }
        catch { }
    }

    private static readonly IntPtr HwndMessage = new(-3); // HWND_MESSAGE

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, int count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);
}
