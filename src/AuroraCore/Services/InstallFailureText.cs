using AuroraCore.Download;

namespace AuroraCore.Services;

/// <summary>
/// Round-98: a cancelled install used to surface as the raw CLR string
/// "The operation was canceled." — the player never pressed cancel, so the
/// message was both untranslated and wrong. The real sequence was:
/// watchdog or slow maven → linked token cancelled → the raw exception
/// escaped the installer's catch → a SECOND FinishJob call overwrote the
/// watchdog's Chinese message with the English one.
///
/// This policy owns every "why did my install stop" sentence. It is pure so
/// the wording can be locked by tests without a dispatcher or a network.
/// </summary>
public static class InstallFailureText
{
    public static bool IsCancellation(Exception ex)
        => ex is OperationCanceledException or TaskCanceledException or TimeoutException;

    /// <summary>A route failure is worth retrying on the mirror ONLY when the
    /// caller did not cancel us. Cancelling the token ourselves is a decision,
    /// not a network fault — retrying would fight the watchdog.</summary>
    public static bool IsRetryableRouteFailure(Exception ex, bool externallyCanceled)
    {
        if (externallyCanceled) return false;
        return ex is DownloadException or HttpRequestException or IOException
            or TimeoutException or TaskCanceledException;
    }

    public static string Describe(Exception ex, string? stage = null, bool externallyCanceled = false)
    {
        // Our own checks already speak Chinese and name the next step.
        if (LooksIntentional(ex.Message)) return ex.Message;

        if (IsCancellation(ex))
        {
            if (externallyCanceled)
                return "已取消安装（未完成的临时文件已清理）。想继续的话点「重试」。";
            var where = string.Equals(stage, "installer-download", StringComparison.OrdinalIgnoreCase)
                ? "下载加载器安装器" : "安装";
            return $"{where}超时被中止——连接 Maven 仓库没有按时完成，不完整的临时文件已清理。" +
                   "这不是你点了取消；请点「重试」，启动器会先走 BMCLAPI 镜像再试官方线路。";
        }

        return ex switch
        {
            DownloadException =>
                "下载失败：" + Short(ex.Message) + "。请点「重试」（会自动改用 BMCLAPI 镜像），或检查网络/代理。",
            HttpRequestException =>
                "连接下载服务器失败：" + Short(ex.Message) + "。检查网络/代理后点「重试」。",
            IOException =>
                "读写安装文件失败：" + Short(ex.Message) + "。检查磁盘空间与杀毒软件后重试。",
            _ => Short(ex.Message) + "（详细堆栈已写入日志）"
        };
    }

    /// <summary>Used when a mirror retry also failed — say exactly what to do
    /// instead of dumping six lines of maven coordinates at the player.</summary>
    public static string BothRoutesFailed(string officialUrl, string mirrorUrl, string type, Exception second)
        => $"安装器下载失败（官方与镜像都不可用）：\n" +
           $"· {officialUrl}\n· {mirrorUrl}\n" +
           $"常见原因：maven 上不存在这个 {type} 版本，或网络/代理拦截了下载。" +
           $"Forge 的路径必须是「MC版本-Forge版本」（例如 1.20.1-47.4.22）。\n" +
           $"原始错误：{Short(second.Message)}";

    private static bool LooksIntentional(string message)
    {
        if (message.Length < 8) return false;
        foreach (var c in message)
            if (c >= 0x4E00 && c <= 0x9FFF) return true;
        return message.Contains("not installed", StringComparison.OrdinalIgnoreCase);
    }

    private static string Short(string message)
        => message.Length <= 160 ? message : message[..160] + "…";
}
