namespace AuroraCore.Services;

/// <summary>
/// Round-96: the header used to say「正在安装… 获取加载器安装器」while the job
/// list was empty and the speed read 0 B/s, with no way out. Any job that does
/// not report progress for too long is now failed in place (the row stays red
/// and retryable) and the busy state is cleared.
///
/// Timeouts are deliberately stage-aware: a "准备中" step does no I/O at all
/// (30 s is plenty), fetching a ~9 MB installer jar over a flaky link has been
/// observed to take 33 s (90 s), and running the official Forge installer can
/// legitimately take 165 s of near-silence — it already has its own 15 min cap,
/// so we must not kill it at 30 s.
/// </summary>
public static class JobWatchdogPolicy
{
    public const int PreparingTimeoutSeconds = 30;
    public const int InstallerDownloadTimeoutSeconds = 90;
    public const int ActiveTimeoutSeconds = 15 * 60;

    public static int TimeoutSeconds(JobState state, string? stageKey)
    {
        // Round-98: a repair job starts in Preparing (it must appear in the
        // list before the first byte). Once it has ENTERED a named stage it is
        // doing real I/O — treating it as "30 s, no I/O" killed a 9 MB
        // installer jar that simply had no progress callback at the time.
        if (state == JobState.Preparing && string.IsNullOrWhiteSpace(stageKey))
            return PreparingTimeoutSeconds;
        return string.Equals(stageKey, "installer-download", StringComparison.OrdinalIgnoreCase)
            ? InstallerDownloadTimeoutSeconds
            : ActiveTimeoutSeconds;
    }

    /// <summary>Round-98: the 2-minute "no NEW file completed" rule applies to
    /// multi-file batches. An installer jar is ONE file and the installer JVM
    /// owns its own downloads — there is no file counter to advance there, so
    /// only the byte-progress (quiet) watchdog applies in those stages.</summary>
    public static bool ShouldUseFileStall(string? stageKey)
        => !string.Equals(stageKey, "installer-download", StringComparison.OrdinalIgnoreCase) &&
           !string.Equals(stageKey, InstallerRunStage, StringComparison.OrdinalIgnoreCase);

    private const string InstallerRunStage = "installer-run";

    public static bool IsStalled(DateTime lastProgressUtc, DateTime nowUtc, int timeoutSeconds)
        => (nowUtc - lastProgressUtc).TotalSeconds >= Math.Max(1, timeoutSeconds);

    /// <summary>Round-97: "87/90 一动不动" detector. Independent of bytes/s:
    /// if no file has flipped to completed for 2 minutes while a file-count
    /// flow is active, the transfer is crawling, not working.</summary>
    public const int FileStallSeconds = 120;

    public static bool IsFileProgressStalled(DateTime lastFileCompletedUtc, DateTime nowUtc, int trackedTotalFiles)
        => trackedTotalFiles > 0 && (nowUtc - lastFileCompletedUtc).TotalSeconds >= FileStallSeconds;

    public static string FileStalledMessage(DateTime lastFileCompletedUtc, DateTime nowUtc,
        int completedFiles, int totalFiles)
    {
        var quiet = Math.Max(0, (int)(nowUtc - lastFileCompletedUtc).TotalSeconds);
        return $"已 {quiet} 秒没有新文件完成（停在 {completedFiles}/{totalFiles}），" +
               "即使还有少量下行速度也判定为网络异常并中止；可点「重试」再来一次。";
    }

    public static string StalledMessage(DateTime lastProgressUtc, DateTime nowUtc, int timeoutSeconds)
    {
        var quiet = Math.Max(0, (int)(nowUtc - lastProgressUtc).TotalSeconds);
        return $"已 {quiet} 秒没有进度（上限 {timeoutSeconds} 秒），判定为卡住并中止；可点「重试」再来一次。";
    }
}
