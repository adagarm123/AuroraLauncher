namespace AuroraCore.Services;

/// <summary>
/// Round-104: a borderless WindowChrome window keeps its 6 px resize border
/// when Windows maximises it, so the visible content is pushed a few pixels
/// off-screen on every edge (the user report: "全屏后界面超出显示范围").
/// Pure rules keep the decision testable without a real HWND.
/// </summary>
public static class WindowLayoutPolicy
{
    public const int NormalResizeBorder = 6;

    public static int ResizeBorder(bool maximized) => maximized ? 0 : NormalResizeBorder;

    /// <summary>Maximised windows must be pinned to the work area explicitly;
    /// letting WPF add the glass/resize border is what causes the overflow.</summary>
    public static bool ClampToWorkArea(bool maximized) => maximized;
}
