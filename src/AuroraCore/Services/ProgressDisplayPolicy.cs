namespace AuroraCore.Services;

/// <summary>
/// Round-97: a job that is still running can NEVER read 100%.
///
/// The screenshot showed 100% / 378.8 KB/s while the row still said 87/90.
/// Root cause: JobProgressPolicy.PercentFor legitimately returns 100 once the
/// stage index reaches the end, and DownloadEngine counts FAILED files as
/// "completed" so CompletedFiles/TotalFiles also hit 1.0 — even though three
/// files never arrived and the installer JVM was still crawling. Nothing was
/// lying about bytes; the DISPLAY was claiming a finished job.
///
/// Rule: floor (never round up) and cap at 99 until the job is truly finished.
/// </summary>
public static class ProgressDisplayPolicy
{
    public const int MaxUnfinishedPercent = 99;
    public const string InstallerRunStage = "installer-run";

    public static int Display(int rawPercent, bool finished)
        => finished ? 100 : Math.Clamp((int)Math.Floor((double)rawPercent), 0, MaxUnfinishedPercent);

    /// <summary>Once the official installer JVM takes over, its internal maven
    /// fetches are a black box to us. A hard "87/90" bar is a lie; show a
    /// sweeping indeterminate bar and say so in words instead.</summary>
    public static bool Indeterminate(string? stageKey)
        => string.Equals(stageKey, InstallerRunStage, StringComparison.OrdinalIgnoreCase);

    public static string Hint(string? stageKey)
        => Indeterminate(stageKey)
            ? "正在执行 Forge / NeoForge 安装器内部下载（受网络影响，可能较慢）"
            : "";
}
