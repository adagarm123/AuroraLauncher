namespace AuroraCore.Services;

/// <summary>
/// Round-92: turns a launch exception into a sentence a player can act on.
///
/// The screenshot that started this round said「启动失败: Object reference not
/// set to an instance of an object.」— technically accurate, useless to a human,
/// and impossible to diagnose later because only the message (never the stack)
/// reached the log. Both ends are fixed here: the message says what to do, and
/// the caller logs <see cref="Describe"/> plus the full exception.
/// </summary>
public static class LaunchFailureText
{
    public static string Describe(Exception ex)
    {
        // Our own guards already speak Chinese and name the next step.
        if (ex is InvalidOperationException && LooksIntentional(ex.Message)) return ex.Message;

        return ex switch
        {
            NullReferenceException =>
                "启动器在准备启动参数时遇到了内部错误（空引用）。通常是加载器文件不完整或版本 JSON 损坏 —— " +
                "请在「实例管理」里点「补全文件」后重试；详细堆栈已写入日志。",
            FileNotFoundException or DirectoryNotFoundException =>
                $"缺少启动所需的文件：{Short(ex.Message)}。请点「补全文件」重新下载。",
            UnauthorizedAccessException =>
                $"没有权限访问游戏文件：{Short(ex.Message)}。关掉杀毒软件的实时扫描或换一个存储目录再试。",
            IOException =>
                $"读写游戏文件失败：{Short(ex.Message)}。检查磁盘空间与杀毒软件，然后重试。",
            System.ComponentModel.Win32Exception =>
                $"无法启动 Java 进程：{Short(ex.Message)}。请在设置里换一个 Java 版本。",
            OperationCanceledException or TaskCanceledException or TimeoutException =>
                InstallFailureText.Describe(ex),
            _ => $"{ex.GetType().Name}：{Short(ex.Message)}（详细堆栈已写入日志）"
        };
    }

    /// <summary>Messages written by our own checks (version not installed, loader
    /// missing, bad credentials) are already user-facing — pass them through.</summary>
    private static bool LooksIntentional(string message)
    {
        if (message.Length < 8) return false;
        // Han characters mean a human wrote it for a human.
        foreach (var c in message)
            if (c >= 0x4E00 && c <= 0x9FFF) return true;
        return message.Contains("not installed", StringComparison.OrdinalIgnoreCase);
    }

    private static string Short(string message)
        => message.Length <= 160 ? message : message[..160] + "…";
}
