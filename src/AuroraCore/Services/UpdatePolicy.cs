namespace AuroraCore.Services;

/// <summary>How the launcher behaves when a new version is published
/// (round-58, user-facing 3-way switch in 设置 → 更新).</summary>
public enum UpdateMode
{
    /// <summary>1 · 自动更新：启动检查 → 后台自动下载 → 下次启动自动换装（不弹窗）。</summary>
    Auto = 1,

    /// <summary>2 · 有更新时提醒我，但不自动更新：启动检查 → 弹窗（立即更新 / 关闭），
    /// 只有用户点「立即更新」才下载。默认模式。</summary>
    Notify = 2,

    /// <summary>3 · 不获取更新：启动时不联网检查；手动「检查更新」仍然可用。</summary>
    Disabled = 3,
}

public enum UpdateAction
{
    /// <summary>什么都不做。</summary>
    None,
    /// <summary>弹窗提醒，等用户决定是否立即更新。</summary>
    Notify,
    /// <summary>后台自动下载（模式 1）。</summary>
    AutoDownload,
}

/// <summary>Pure, unit-testable decision logic for the update mode switch —
/// keeps the "state judgment in one place" discipline (round-32 Evaluate
/// lesson) instead of scattering string/flag comparisons across the VM.</summary>
public static class UpdatePolicy
{
    /// <summary>模式 3 启动时不获取更新；其余模式每次启动都检查。</summary>
    public static bool ShouldCheckOnStartup(UpdateMode mode) => mode != UpdateMode.Disabled;

    /// <summary>Decides what to do once a check completed.
    /// <paramref name="manual"/> = 手动「检查更新」：无论什么模式（含模式 3），
    /// 都把结果弹窗交给用户决定——用户主动发起的检查必须有明确反馈。</summary>
    public static UpdateAction Decide(UpdateMode mode, bool hasUpdate, bool manual)
    {
        if (!hasUpdate) return UpdateAction.None;
        if (manual) return UpdateAction.Notify;
        return mode switch
        {
            UpdateMode.Auto => UpdateAction.AutoDownload,
            UpdateMode.Notify => UpdateAction.Notify,
            _ => UpdateAction.None,
        };
    }

    public static string DisplayText(UpdateMode mode) => mode switch
    {
        UpdateMode.Auto => "自动更新",
        UpdateMode.Notify => "有更新时提醒我，但不自动更新",
        _ => "不获取更新",
    };

    public static UpdateMode FromDisplayText(string? text) => text switch
    {
        "自动更新" => UpdateMode.Auto,
        "有更新时提醒我，但不自动更新" => UpdateMode.Notify,
        "不获取更新" => UpdateMode.Disabled,
        _ => UpdateMode.Notify,
    };
}
