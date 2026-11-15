using System.Windows;
using AuroraCore;
using AuroraCore.Services;
using AuroraCore.Utils;
using AuroraLauncher.ViewModels;

namespace AuroraLauncher.Desktop.ViewModels;

/// <summary>What to do about unsaved settings.</summary>
public enum UnsavedSettingsDecision
{
    Save,
    Discard,
    Cancel
}

/// <summary>
/// Round-90: "you changed settings and walked away" — the classic desktop
/// silent-data-loss bug. The page saves only on 「保存设置」, so switching to
/// another page used to throw the edits away with no trace.
///
/// Design: one snapshot string, taken when the page is entered and after every
/// successful save. No per-setter dirty flags — with fifteen bound fields the
/// flag somebody forgets is exactly the one that loses data.
///
/// Interception happens in <see cref="Navigate(string?)"/> (page switches) and in
/// <c>MainWindow.OnClosing</c> (window close), because both can discard edits.
/// </summary>
public partial class MainViewModel
{
    private string? _settingsBaseline;

    /// <summary>Test seam: a modal dialog would hang a headless run. Production
    /// leaves this null and the real MessageBox decides.</summary>
    public Func<IReadOnlyList<string>, UnsavedSettingsDecision>? UnsavedSettingsPromptForTests { get; set; }

    /// <summary>True when the settings fields differ from the last saved baseline.</summary>
    public bool HasUnsavedSettings => SettingsDirtyPolicy.IsDirty(_settingsBaseline, CaptureSettingsSnapshot());

    private string _settingsDirtyHint = "";
    /// <summary>Shown next to 「保存设置」 — the user should not have to guess
    /// whether their last edit made it.</summary>
    public string SettingsDirtyHint { get => _settingsDirtyHint; private set => SetProperty(ref _settingsDirtyHint, value); }

    public bool HasSettingsDirtyHint => !string.IsNullOrEmpty(_settingsDirtyHint);

    /// <summary>The fields <c>ApplySettings</c> writes. Labels are the friendly
    /// names shown in the prompt, so "改了 2 项：内存分配、Java 路径" is possible.</summary>
    private static readonly (string Key, string Label)[] SettingsFields =
    {
        ("memory", "内存分配"),
        ("autoMemory", "自动内存"),
        ("releaseIdle", "闲置释放内存"),
        ("java", "Java 路径"),
        ("route", "下载线路"),
        ("proxy", "代理地址"),
        ("msClient", "微软登录 Client ID"),
        ("fabricApi", "自动装 Fabric API"),
        ("checkMods", "启动前模组体检"),
        ("updateMode", "更新方式"),
        ("updateFeed", "更新源地址"),
        ("cfKey", "CurseForge API Key"),
        ("window", "游戏窗口分辨率"),
        ("jvm", "额外 JVM 参数"),
        ("root", "存储目录"),
        ("closeAction", "关闭窗口时"),
        ("tray", "托盘图标"),
        ("theme", "主题"),
    };

    private IEnumerable<KeyValuePair<string, string?>> CaptureSettings()
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["memory"] = MemoryMb.ToString(),
            ["autoMemory"] = AutoMemory ? "1" : "0",
            ["releaseIdle"] = ReleaseIdleMemory ? "1" : "0",
            ["java"] = JavaPath ?? "",
            ["route"] = DownloadPolicyText ?? "",
            ["proxy"] = ProxyUrl ?? "",
            ["msClient"] = MicrosoftClientIdText ?? "",
            ["fabricApi"] = AutoInstallFabricApi ? "1" : "0",
            ["checkMods"] = CheckModsBeforeLaunch ? "1" : "0",
            ["updateMode"] = UpdateModeText ?? "",
            ["updateFeed"] = UpdateFeedUrlText ?? "",
            ["cfKey"] = CurseForgeApiKeyText ?? "",
            ["window"] = $"{WindowW}x{WindowH}",
            ["jvm"] = ExtraJvm ?? "",
            ["root"] = RootDirectoryText ?? "",
            ["closeAction"] = CloseActionText ?? "",
            ["tray"] = TrayEnabled ? "1" : "0",
            ["theme"] = DarkTheme ? "dark" : "light",
        };
        foreach (var (key, _) in SettingsFields)
            if (!values.ContainsKey(key)) values[key] = null;
        return values;
    }

    private string CaptureSettingsSnapshot() => SettingsDirtyPolicy.Snapshot(CaptureSettings());

    private System.Windows.Threading.DispatcherTimer? _settingsDirtyTimer;

    /// <summary>Round-90: baseline + live hint. The baseline is taken once the VM has
    /// loaded config (so opening the launcher is never "dirty").
    ///
    /// The hint is refreshed by a 500 ms ticker rather than by PropertyChanged:
    /// most settings fields are plain auto-properties (the TextBox pushes INTO the
    /// model, so nothing raises a notification). Adding 18 hand-written setters
    /// would be a lot of code for a hint — and the one that gets forgotten is
    /// exactly the field whose edit goes unreported. The tick only does work while
    /// the settings page is on screen.</summary>
    private void InitRound90SettingsGuard()
    {
        MarkSettingsSaved();
        _settingsDirtyTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _settingsDirtyTimer.Tick += (_, _) =>
        {
            if (!string.Equals(CurrentPageKey, "Settings", StringComparison.OrdinalIgnoreCase)) return;
            RefreshSettingsDirtyHint();
        };
        _settingsDirtyTimer.Start();
    }

    /// <summary>Called after a real save (and once at startup): the page is clean again.</summary>
    public void MarkSettingsSaved()
    {
        _settingsBaseline = CaptureSettingsSnapshot();
        RefreshSettingsDirtyHint();
    }

    /// <summary>「否」on the prompt: throw the edits away by reloading from config.
    /// Does NOT touch accounts/instances (see LoadSettingsFromConfig).</summary>
    public void DiscardSettingsChanges()
    {
        LoadSettingsFromConfig();
        MarkSettingsSaved();
        Status = "已放弃未保存的设置修改";
        Logger.Instance.Info("Config", "用户放弃了设置页的未保存修改");
    }

    private IReadOnlyList<string> ChangedSettingLabels()
    {
        var changed = SettingsDirtyPolicy.ChangedKeys(_settingsBaseline, CaptureSettingsSnapshot());
        var labels = new List<string>();
        foreach (var key in changed)
        {
            var match = SettingsFields.FirstOrDefault(f => f.Key == key);
            labels.Add(match.Label ?? key);
        }
        return labels;
    }

    private void RefreshSettingsDirtyHint()
    {
        if (_settingsBaseline == null) { SettingsDirtyHint = ""; return; }
        var labels = ChangedSettingLabels();
        SettingsDirtyHint = labels.Count == 0
            ? ""
            : $"有 {labels.Count} 项修改尚未保存（{string.Join("、", labels.Take(3))}" + (labels.Count > 3 ? " 等" : "") + "）";
        OnPropertyChanged(nameof(HasSettingsDirtyHint));
    }

    /// <summary>Round-90 gate in front of every page switch. Returns false to keep
    /// the user on the settings page.</summary>
    private bool ConfirmLeaveSettings(string target)
    {
        if (!string.Equals(CurrentPageKey, "Settings", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(target, "Settings", StringComparison.OrdinalIgnoreCase)) return true;
        if (!HasUnsavedSettings) return true;

        var labels = ChangedSettingLabels();
        var decision = UnsavedSettingsPromptForTests != null
            ? UnsavedSettingsPromptForTests(labels)
            : AskUnsavedSettings(labels);

        switch (decision)
        {
            case UnsavedSettingsDecision.Save:
                ApplySettings();
                // Save may fail (invalid storage path): staying beats a silent loss.
                if (HasUnsavedSettings) { Status = "设置没有保存成功，已留在当前页"; return false; }
                return true;

            case UnsavedSettingsDecision.Discard:
                DiscardSettingsChanges();
                return true;

            default:
                return false;
        }
    }

    private UnsavedSettingsDecision AskUnsavedSettings(IReadOnlyList<string> labels)
    {
        var answer = MessageBox.Show(Application.Current.MainWindow,
            SettingsDirtyPolicy.BuildPrompt(labels), "设置还没有保存",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Yes);
        Logger.Instance.Info("Config", $"离开设置页时提示未保存修改：用户选择 {answer}");
        return answer switch
        {
            MessageBoxResult.Yes => UnsavedSettingsDecision.Save,
            MessageBoxResult.No => UnsavedSettingsDecision.Discard,
            _ => UnsavedSettingsDecision.Cancel
        };
    }

    /// <summary>Round-90: the window close path asks the same question — closing is
    /// just another way to lose the edits. True = may continue closing.</summary>
    public bool ConfirmCloseWithUnsavedSettings()
    {
        if (!HasUnsavedSettings) return true;
        var labels = ChangedSettingLabels();
        var decision = UnsavedSettingsPromptForTests != null
            ? UnsavedSettingsPromptForTests(labels)
            : AskUnsavedSettings(labels);
        switch (decision)
        {
            case UnsavedSettingsDecision.Save:
                ApplySettings();
                return !HasUnsavedSettings;
            case UnsavedSettingsDecision.Discard:
                DiscardSettingsChanges();
                return true;
            default:
                Logger.Instance.Info("App", "退出时用户选择留在启动器（设置有未保存的修改）");
                return false;
        }
    }
}
