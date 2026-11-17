namespace AuroraCore.Services;

/// <summary>
/// Round-101: the words and decisions behind the visible update window.
/// Kept pure so the cancel/progress semantics can be tested offline — a
/// "cancel" that silently leaves a half-installed exe is worse than no cancel.
/// </summary>
public static class UpdateUiPolicy
{
    /// <summary>Header text for the download window (the apply window has its
    /// own, deliberately different wording — see UpdateApplyingWindow).</summary>
    public static string DownloadTitle(int round) => $"正在下载启动器更新（第 {round} 轮）";

    public static string DescribeProgress(int percent, string size, string speed)
        => $"正在下载更新 {percent:0}%（{size} · {speed}）";

    /// <summary>Cancellation is a normal outcome, not an error. Say exactly
    /// what happened to the staged files so the user knows there is nothing
    /// half-installed (and that an older ready update is still available).</summary>
    public static string DescribeCanceled(bool hadStagedUpdateBefore)
        => hadStagedUpdateBefore
            ? "已取消本次下载。之前已就绪的更新仍保留，可随时点「立即重启并应用更新」。"
            : "已取消更新。未下载完的更新包已清理，不会安装半个文件。";

    /// <summary>The cancel button is available only while a download is
    /// actually running; a second click must not race the first.</summary>
    public static bool CanCancel(bool active, bool cancellationRequested)
        => active && !cancellationRequested;

    /// <summary>Once bytes are staged and the updater host has started, the
    /// swap must not be cancellable — shutting down mid-copy would corrupt the
    /// running EXE. The UI says so instead of offering a fake choice.</summary>
    public static string ApplyStageHint => "安装阶段不可取消：正在替换启动器本体，强制关闭可能损坏文件。下载阶段可随时取消。";
}
