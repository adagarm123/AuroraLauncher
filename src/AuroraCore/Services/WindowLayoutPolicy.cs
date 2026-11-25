namespace AuroraCore.Services;

/// <summary>
/// Round-104: a borderless WindowChrome window keeps its 6 px resize border
/// when Windows maximises it, so the visible content is pushed a few pixels
/// off-screen on every edge (the user report: "全屏后界面超出显示范围").
/// Round-105: assigning Left/Top/Width/Height to a MAXIMISED window is a no-op
/// — WPF hands the geometry to the OS. The only reliable way to keep the
/// taskbar visible is to answer WM_GETMINMAXINFO with the monitor work area.
/// Pure rules keep the decision testable without a real HWND.
/// </summary>
public static class WindowLayoutPolicy
{
    public const int NormalResizeBorder = 6;

    /// <summary>WM_GETMINMAXINFO: Windows asks for the maximised rectangle while
    /// the window is still Normal, so the answer must ALWAYS be the work area.</summary>
    public const int WmGetMinMaxInfo = 0x0024;

    public static int ResizeBorder(bool maximized) => maximized ? 0 : NormalResizeBorder;

    /// <summary>Maximised windows must be pinned to the work area explicitly;
    /// letting WPF add the glass/resize border is what causes the overflow.</summary>
    public static bool ClampToWorkArea(bool maximized) => maximized;
}
