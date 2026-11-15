using System.Security.Cryptography;
using System.Text;

namespace AuroraCore.Services;

/// <summary>
/// Round-74: one launcher per data root. Two copies of the EXE would otherwise
/// fight over the same config.json and instances/ (the second instance's
/// periodic Save() can drop the first one's changes, and two installs can write
/// the same file — the very race PathWriteGate fixes inside one process).
///
/// The names are derived from the data root, NOT from the exe path: a normal
/// install and a portable USB stick are different roots and may legitimately run
/// side by side, while two copies of the same install must not.
/// </summary>
public static class SingleInstancePolicy
{
    /// <summary>Session-local names on purpose: <c>Global\</c> needs
    /// SeCreateGlobalPrivilege, which a standard user does not have, and the
    /// failure mode (no mutex at all) is worse than "one instance per session"
    /// — sessions cannot share a config file anyway without a second login.</summary>
    public static string MutexName(string rootDirectory) => "AuroraLauncher.Instance." + Fingerprint(rootDirectory);

    public static string ActivationEventName(string rootDirectory) => "AuroraLauncher.Activate." + Fingerprint(rootDirectory);

    /// <summary>Round-75: the running instance acknowledges an activation request.
    /// Without an acknowledgement the caller cannot tell "the window came
    /// forward" from "nobody is listening" — round 74 exited silently in the
    /// second case, which looked exactly like "double-click does nothing".</summary>
    public static string ActivationAckEventName(string rootDirectory) => "AuroraLauncher.ActivateAck." + Fingerprint(rootDirectory);

    /// <summary>Stable, filesystem-safe key for a data root.</summary>
    public static string Fingerprint(string rootDirectory)
    {
        var normalized = Normalize(rootDirectory);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes)[..16];
    }

    public static string Normalize(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory)) return "(default)";
        var value = rootDirectory.Trim().Replace('/', '\\').TrimEnd('\\');
        return value.ToLowerInvariant();
    }

    /// <summary>Message for the second process' log — the user sees the first
    /// window come forward instead of an error.</summary>
    public static string SecondInstanceNote(string rootDirectory)
        => $"已有启动器在使用同一数据目录（{rootDirectory}），本次启动已切换到那个窗口。";

    /// <summary>Round-75: the existing process never answered — it is hung, or its
    /// window was closed while the process kept the mutex. Starting a new window
    /// is strictly better than doing nothing.</summary>
    public static string StaleInstanceNote(string rootDirectory)
        => $"同一数据目录（{rootDirectory}）的启动器进程没有响应（可能已卡死或窗口已关闭），本次改为直接打开新窗口。";
}
