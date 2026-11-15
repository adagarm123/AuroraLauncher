namespace AuroraCore.Services;

/// <summary>
/// Round-80: how much "liquid glass" the user asked for. Off is the default and
/// the only mode the launcher ships by default — every other mode is opt-in
/// behind a red performance warning.
/// </summary>
public enum GlassMode
{
    Off,
    /// <summary>DWM composited material (Mica on Win11 22H2+, Acrylic fallback).
    /// GPU cost ≈ 0, but the look follows the system and Win10 support varies.</summary>
    System,
    /// <summary>Faux glass: translucent gradient + noise + hairline highlight.
    /// Zero per-frame cost (plain fills, no Effect), works on every OS.</summary>
    Faux,
    /// <summary>Faux glass only on transient surfaces (Ctrl+K palette, toast,
    /// dialog scrims) with a real blur behind the palette. Resident pages stay
    /// exactly as they were.</summary>
    Local
}

/// <summary>
/// Round-80 decision layer. Everything here is pure so the wording, the warning
/// gate and the OS fallback are testable without a window or a GPU.
///
/// Design note (why "no blur everywhere"): WPF's <c>BlurEffect</c> blurs the
/// element's OWN subtree — applying it to a card blurs its text. Real
/// "frosted glass over what's behind" needs a VisualBrush snapshot, which is
/// precisely the per-frame bitmap regeneration that froze the UI in round 66/67.
/// So the default glass look is built from plain fills (no Effect), and a real
/// blur is allowed only behind the transient palette, where the page is static.
/// </summary>
public static class GlassEffectPolicy
{
    /// <summary>Windows 11 22H2 — the first build with DWMWA_SYSTEMBACKDROP_TYPE.</summary>
    public const int MicaMinBuild = 22621;

    /// <summary>Windows 11 21H2 — acrylic via SetWindowCompositionAttribute.</summary>
    public const int AcrylicMinBuild = 22000;

    public static IReadOnlyList<string> DisplayNames { get; } = new[]
    {
        "关闭（原主题，最省资源）",
        "液态玻璃（光影壁纸 + 深色玻璃卡片）",
        "仅浮层（搜索面板 / 提示 / 弹窗）"
    };

    public static string DisplayName(GlassMode mode) => mode switch
    {
        // Round-81: the wallpaper design replaced the native Mica path (a Mica
        // backdrop is invisible under an in-window wallpaper), so a config that
        // still says "System" is reported — and applied — as the glass skin.
        GlassMode.System => DisplayNames[1],
        GlassMode.Faux => DisplayNames[1],
        GlassMode.Local => DisplayNames[2],
        _ => DisplayNames[0]
    };

    public static GlassMode Parse(string? display) => display switch
    {
        "液态玻璃（光影壁纸 + 深色玻璃卡片）" => GlassMode.Faux,
        "仅浮层（搜索面板 / 提示 / 弹窗）" => GlassMode.Local,
        _ => GlassMode.Off
    };

    /// <summary>Only "off" is silent. Everything else costs SOMETHING — GPU
    /// composition, extra layered fills, or a transient blur.</summary>
    public static bool NeedsWarning(GlassMode mode) => mode != GlassMode.Off;

    /// <summary>Requested mode reduced to what this build actually renders.
    /// Since round-81 the glass look is drawn inside the window (wallpaper), so
    /// the native "System" material has no visible surface any more and is
    /// folded into the same skin.</summary>
    public static GlassMode Resolve(GlassMode requested, int windowsBuild)
        => requested == GlassMode.System ? GlassMode.Faux : requested;

    /// <summary>True when the mode paints its own translucent surfaces (cards,
    /// sidebar) rather than relying on the DWM backdrop.</summary>
    public static bool UsesTranslucentSurfaces(GlassMode mode)
        => mode is GlassMode.Faux or GlassMode.System or GlassMode.Local;

    /// <summary>True when the mode is allowed to put a real BlurEffect behind a
    /// TRANSIENT surface (never on a resident page).</summary>
    public static bool UsesTransientBlur(GlassMode mode) => mode == GlassMode.Local;

    public static string Describe(GlassMode mode) => mode switch
    {
        GlassMode.System or GlassMode.Faux =>
            "底层一张矢量光影壁纸，卡片是半透明黑玻璃 + 1px 白色高光边，文字转为白色 / 灰白。\n" +
            "全部是纯色块叠加：没有实时模糊、没有阴影位图、没有动画，滚动列表的成本和原来的实心卡片一样。",
        GlassMode.Local =>
            "玻璃只出现在临时界面：Ctrl+K 搜索面板（打开时对页面做一次模糊）、右下角提示、弹窗遮罩。\n" +
            "常驻页面保持原主题完全不变，这是最保守的选择。",
        _ => "使用原有主题背景（深色 / 浅色），不做任何半透明或模糊处理。"
    };

    /// <summary>Shown in the red gate before anything is enabled. Deliberately
    /// blunt: the user must be able to say "I was warned" afterwards.</summary>
    public static string WarningTitle => "⚠ 开启玻璃效果可能导致严重卡顿甚至崩溃";

    public static string WarningBody(string requested, GlassMode effective, int windowsBuild)
    {
        var fallback = effective != Parse(requested)
            ? $"\n\n注意：当前系统版本（build {windowsBuild}）不支持 Mica，将自动降级为「{DisplayName(effective)}」。"
            : "";
        return "你即将开启：" + requested + "\n\n" +
               "这是实验性的视觉特效，最坏情况下会导致启动器卡顿、崩溃或无法响应：\n" +
               "· 在半透明窗口上滚动长列表（实例、模组、日志）时出现掉帧；\n" +
               "· 显卡驱动较旧或使用远程桌面 / 虚拟机时，画面撕裂、闪烁，甚至启动器无响应 / 崩溃；\n" +
               "· 浅色主题下部分文字对比度下降。\n\n" +
               "开启后随时可以在「设置 → 外观」里改回「关闭」，不会影响任何存档或游戏文件。" +
               fallback;
    }

    /// <summary>One-line status under the combo box.</summary>
    public static string StatusLine(GlassMode mode, GlassMode effective, int windowsBuild)
        => mode == GlassMode.Off ? Describe(GlassMode.Off) : Describe(effective);
}

/// <summary>
/// Round-80 guard: round-67 banned every software effect because a shadow on a
/// card made each navigation regenerate bitmaps. Glass needs ONE narrow
/// exception, and "narrow" has to be machine-checkable or it will rot:
///
///  · <c>&lt;BlurEffect&gt;</c> may only appear in MainWindow.xaml, on an element
///    whose <c>x:Name</c> starts with <c>Glass</c>, never inside a DataTemplate
///    (list rows are the thing that must never be blurred).
///  · In code, a BlurEffect may only be assigned from a field whose name starts
///    with <c>GlassBlur</c> in MainWindow.xaml.cs.
///
/// Pure string analysis — unit-testable with synthetic XAML.
/// </summary>
public static class GlassGuardPolicy
{
    public const string AllowedXamlFile = "MainWindow.xaml";
    public const string AllowedCodeFile = "MainWindow.xaml.cs";
    public const string AllowedElementPrefix = "Glass";
    public const string AllowedCodeMarker = "GlassBlur";

    /// <summary>Every violation found in one XAML file (empty = clean).</summary>
    public static IReadOnlyList<string> ScanXaml(string xaml, string fileName)
    {
        var violations = new List<string>();
        if (string.IsNullOrEmpty(xaml) || !xaml.Contains("<BlurEffect", StringComparison.Ordinal))
            return violations;

        var templates = DataTemplateRanges(xaml);
        var index = -1;
        while ((index = xaml.IndexOf("<BlurEffect", index + 1, StringComparison.Ordinal)) >= 0)
        {
            if (templates.Any(r => index > r.Start && index < r.End))
                violations.Add($"{fileName}: BlurEffect inside a DataTemplate（列表行绝不能模糊）");
            if (!string.Equals(fileName, AllowedXamlFile, StringComparison.OrdinalIgnoreCase))
                violations.Add($"{fileName}: BlurEffect 只允许出现在 {AllowedXamlFile} 的 Glass* 元素内");
            else
            {
                var window = xaml[Math.Max(0, index - 600)..index];
                if (!window.Contains($"x:Name=\"{AllowedElementPrefix}", StringComparison.Ordinal))
                    violations.Add($"{fileName}: BlurEffect 挂在非 Glass* 元素上");
            }
        }
        return violations;
    }

    /// <summary>Code-side rule: a BlurEffect assignment is only allowed in
    /// MainWindow.xaml.cs and only from a GlassBlur-named member.</summary>
    public static bool IsCodeBlurAllowed(string fileName, string line)
        => string.Equals(fileName, AllowedCodeFile, StringComparison.OrdinalIgnoreCase)
           && line.Contains(AllowedCodeMarker, StringComparison.OrdinalIgnoreCase);

    private static List<(int Start, int End)> DataTemplateRanges(string xaml)
    {
        var ranges = new List<(int, int)>();
        var index = -1;
        while ((index = xaml.IndexOf("<DataTemplate", index + 1, StringComparison.Ordinal)) >= 0)
        {
            var end = xaml.IndexOf("</DataTemplate>", index, StringComparison.Ordinal);
            if (end < 0) end = xaml.Length;
            ranges.Add((index, end));
        }
        return ranges;
    }
}
