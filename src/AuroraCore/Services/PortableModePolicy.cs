using System.IO;

namespace AuroraCore.Services;

/// <summary>
/// Round-74: portable mode. A USB stick with the launcher on it should not
/// scatter instances and caches into <c>%APPDATA%</c> on every machine it is
/// plugged into.
///
/// Detection is intentionally file-based and explicit:
///  - <c>portable.txt</c> next to the EXE  → portable (documented, one file)
///  - <c>config.json</c> next to the EXE   → portable (an existing portable
///    layout keeps working even if the marker is lost)
///
/// Nothing is ever migrated automatically. Moving gigabytes of instances
/// behind the user's back — and possibly across drives — is exactly the kind
/// of "helpful" behaviour that loses data, so the switch only changes where
/// NEW data is written and the user is told so.
/// </summary>
public static class PortableModePolicy
{
    public const string MarkerFileName = "portable.txt";
    public const string ConfigFileName = "config.json";

    /// <summary>Returns the portable root (the EXE's own folder) or null when
    /// this is a normal installation.</summary>
    public static string? ResolveRoot(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        string dir;
        try { dir = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? ""; }
        catch { return null; }
        if (dir.Length == 0 || !Directory.Exists(dir)) return null;

        if (File.Exists(Path.Combine(dir, MarkerFileName))) return dir;
        if (File.Exists(Path.Combine(dir, ConfigFileName))) return dir;
        return null;
    }

    public static bool IsPortable(string? exePath) => ResolveRoot(exePath) != null;

    /// <summary>Creates the marker next to the running EXE. Returns the path,
    /// or throws with a readable reason (typically a read-only folder such as
    /// Program Files).</summary>
    public static string CreateMarker(string exePath, string? note = null)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(exePath))
                  ?? throw new IOException("无法定位程序所在目录。");
        var path = Path.Combine(dir, MarkerFileName);
        var text = "# 便携模式标记：存在此文件时，启动器把配置、实例、缓存都放在本目录。\r\n" +
                   "# 由 AuroraLauncher 于 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " 创建。\r\n" +
                   (string.IsNullOrWhiteSpace(note) ? "" : "# " + note + "\r\n");
        File.WriteAllText(path, text, new System.Text.UTF8Encoding(true));
        return path;
    }

    /// <summary>User-facing one-liner for the settings page.</summary>
    public static string Describe(bool portable, string rootDirectory, string? exePath)
    {
        if (portable)
            return $"便携模式：所有数据（配置 / 实例 / 缓存）保存在程序目录 {rootDirectory}，不写系统用户目录。";
        var marker = string.IsNullOrWhiteSpace(exePath)
            ? MarkerFileName
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? "", MarkerFileName);
        return $"标准模式：数据保存在 {rootDirectory}。在程序目录放置 {MarkerFileName}（{marker}）后重启即为便携模式。";
    }
}
