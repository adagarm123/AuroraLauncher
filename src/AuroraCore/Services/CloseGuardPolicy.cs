namespace AuroraCore.Services;

/// <summary>Round-73: what to do when the user closes the window mid-download.
///
/// Killing three half-finished 1 GB installs silently is rude; refusing to close
/// is worse. The rule kept here (and tested) is: ask once, and only when work is
/// actually in flight — a window with no jobs must close instantly.
///
/// "最小化到托盘" is deliberately NOT offered: the launcher has no tray
/// infrastructure (no icon, no lifetime management), and a hidden window that
/// keeps downloading without a visible entry point is a support nightmare.</summary>
public static class CloseGuardPolicy
{
    public static bool ShouldPrompt(int runningJobs, int queuedJobs, bool alreadyConfirmed)
        => !alreadyConfirmed && runningJobs + queuedJobs > 0;

    public static string BuildMessage(int runningJobs, int queuedJobs)
    {
        var running = Math.Max(0, runningJobs);
        var queued = Math.Max(0, queuedJobs);
        var detail = queued > 0
            ? $"{running} 个正在下载，{queued} 个排队中"
            : $"{running} 个正在下载";

        return $"还有 {running + queued} 个下载任务未完成（{detail}）。\n\n" +
               "现在退出会中断这些任务：已下载完成的文件不受影响，未完成的临时文件（.part）会被清理，下次可以重新下载。\n\n" +
               "确定要退出吗？\n选择「否」继续下载。";
    }
}
