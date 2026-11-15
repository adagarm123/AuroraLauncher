using System.Windows;
using System.Windows.Threading;
using AuroraCore;
using AuroraCore.Services;
using AuroraLauncher.Desktop.Services;
using AuroraLauncher.Desktop.Views;

namespace AuroraLauncher.Desktop.ViewModels;

/// <summary>
/// Round-80: 液态玻璃开关. Two rules drive the whole feature:
///  1. <b>Off by default</b> — the original theme is the safe baseline.
///  2. <b>A red warning before anything non-default</b> — the user must accept
///     the risk explicitly, and cancelling must leave the combo exactly where it
///     was (no half-applied state).
///
/// The visual work happens in MainWindow (it owns the HWND and the named
/// elements); this file only decides WHAT to apply and persists the choice.
/// </summary>
public partial class MainViewModel
{
    /// <summary>OS build, for logs/diagnostics only — since round-81 the visual
    /// choice is the same on every Windows version (wallpaper skin).</summary>
    private static int WindowsBuild => Environment.OSVersion.Version.Build;

    public List<string> GlassModeOptions { get; } = GlassEffectPolicy.DisplayNames.ToList();

    private string _glassModeText = GlassEffectPolicy.DisplayName(LauncherConfig.Instance.GlassMode);
    private string _glassCommitted = GlassEffectPolicy.DisplayName(LauncherConfig.Instance.GlassMode);

    /// <summary>Bound to the combo box. The dialog runs on the next dispatcher
    /// turn: showing a modal window from inside a binding setter re-enters the
    /// ComboBox's selection logic and can wedge the popup.</summary>
    public string GlassModeText
    {
        get => _glassModeText;
        set
        {
            if (!SetProperty(ref _glassModeText, value)) return;
            Application.Current?.Dispatcher.BeginInvoke(new Action(CommitGlassMode),
                DispatcherPriority.Background);
        }
    }

    private string _glassNote = GlassEffectPolicy.StatusLine(
        LauncherConfig.Instance.GlassMode,
        GlassEffectPolicy.Resolve(LauncherConfig.Instance.GlassMode, WindowsBuild),
        WindowsBuild);

    /// <summary>Explains the current mode + any automatic downgrade.</summary>
    public string GlassNote
    {
        get => _glassNote;
        private set => SetProperty(ref _glassNote, value);
    }

    private bool _showGlassNoise;

    /// <summary>Whether the tiled grain layer is drawn (faux look only).</summary>
    public bool ShowGlassNoise
    {
        get => _showGlassNoise;
        private set => SetProperty(ref _showGlassNoise, value);
    }

    public GlassMode CurrentGlassMode => GlassEffectPolicy.Parse(_glassCommitted);

    /// <summary>Raised with the EFFECTIVE mode whenever the visuals must be
    /// (re)applied — including after a theme switch.</summary>
    public event EventHandler<GlassMode>? GlassModeChanged;

    private void InitGlass()
    {
        var requested = LauncherConfig.Instance.GlassMode;
        // Development hook: AURORA_GLASS=faux|local|off previews a mode without
        // touching the saved config (used by the screenshot smoke tests).
        var probe = Environment.GetEnvironmentVariable("AURORA_GLASS");
        if (!string.IsNullOrWhiteSpace(probe))
        {
            requested = probe.Trim().ToLowerInvariant() switch
            {
                "faux" or "on" or "glass" => GlassMode.Faux,
                "local" => GlassMode.Local,
                _ => GlassMode.Off
            };
            AddLog($"[Glass] 环境变量覆盖界面材质：{GlassEffectPolicy.DisplayName(requested)}");
        }
        var effective = GlassEffectPolicy.Resolve(requested, WindowsBuild);
        GlassNote = GlassEffectPolicy.StatusLine(requested, effective, WindowsBuild);
        ShowGlassNoise = effective == GlassMode.Faux;
        _glassModeText = GlassEffectPolicy.DisplayName(requested);
        _glassCommitted = _glassModeText;
        if (requested != GlassMode.Off)
            AddLog($"[Glass] 启动时应用界面材质：{GlassEffectPolicy.DisplayName(requested)}" +
                   (effective != requested ? $"（降级为 {GlassEffectPolicy.DisplayName(effective)}）" : ""));
    }

    /// <summary>Called by MainWindow once the HWND exists, and again after a
    /// theme switch (the theme dictionary replaces the brushes we overrode).</summary>
    public void RequestGlassApply() => GlassModeChanged?.Invoke(this, LauncherConfig.Instance.GlassMode);

    private void CommitGlassMode()
    {
        var requested = GlassEffectPolicy.Parse(_glassModeText);
        var committed = GlassEffectPolicy.Parse(_glassCommitted);
        if (requested == committed) return;

        var effective = GlassEffectPolicy.Resolve(requested, WindowsBuild);
        if (GlassEffectPolicy.NeedsWarning(requested))
        {
            var dialog = new GlassWarningWindow(
                GlassEffectPolicy.WarningTitle,
                GlassEffectPolicy.WarningBody(_glassModeText, effective, WindowsBuild))
            {
                Owner = Application.Current?.MainWindow
            };
            if (dialog.ShowDialog() != true)
            {
                // Revert the combo without touching the config.
                _glassModeText = _glassCommitted;
                OnPropertyChanged(nameof(GlassModeText));
                Status = "已取消：继续使用原主题";
                AddLog("[Glass] 用户在红色警告中取消，未开启玻璃效果。");
                return;
            }
        }

        _glassCommitted = _glassModeText;
        var cfg = LauncherConfig.Instance;
        cfg.GlassMode = requested;
        cfg.Save();

        GlassNote = GlassEffectPolicy.StatusLine(requested, effective, WindowsBuild);
        ShowGlassNoise = effective == GlassMode.Faux;
        AddLog($"[Glass] 界面材质 → {_glassModeText}（实际生效：{GlassEffectPolicy.DisplayName(effective)}）");
        GlassModeChanged?.Invoke(this, effective);
        Status = "界面材质：" + GlassEffectPolicy.DisplayName(effective);
        if (effective != requested)
            ShowToast("当前系统不支持 Mica，已自动改用零开销伪玻璃");
    }

    /// <summary>MainWindow reports what actually happened (used when the system
    /// material call fails and the caller falls back to the faux look).</summary>
    public void NoteGlassResult(string note)
    {
        if (string.IsNullOrWhiteSpace(note)) return;
        GlassNote = GlassEffectPolicy.StatusLine(CurrentGlassMode,
            GlassEffectPolicy.Resolve(CurrentGlassMode, WindowsBuild), WindowsBuild) +
            "\n" + note;
        AddLog("[Glass] " + note);
    }
}
