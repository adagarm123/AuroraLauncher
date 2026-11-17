namespace AuroraCore.Services;

/// <summary>Round-102: one registry entry the uninstaller may remove.</summary>
public sealed record UninstallRegistryTarget(string Key, string ValueName, string Reason)
{
    public bool IsValue => ValueName.Length > 0;
    public string Display => IsValue ? Key + " → " + ValueName : Key;
}

/// <summary>
/// Round-102: fail-closed registry allowlist. The launcher itself writes NO
/// registry keys today, but older installs / optional helpers may have left a
/// few, and users reported "注册表残留". Only these exact, Aurora-specific
/// locations may be touched — no wildcard sweeps, no vendor keys, no Java keys.
/// Everything here is pure so the allowlist is testable without touching the
/// machine's registry.
/// </summary>
public static class UninstallRegistryPolicy
{
    public const string AppKey = @"Software\AuroraLauncher";
    public const string ClassesKey = @"Software\Classes\AuroraLauncher";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AuroraLauncher";
    public const string AppPathsKey = @"Software\Microsoft\Windows\CurrentVersion\App Paths\AuroraLauncher.exe";
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValueName = "AuroraLauncher";

    public static IReadOnlyList<UninstallRegistryTarget> Candidates => new[]
    {
        new UninstallRegistryTarget(AppKey, "", "AuroraLauncher 专属设置项"),
        new UninstallRegistryTarget(ClassesKey, "", "AuroraLauncher 文件关联"),
        new UninstallRegistryTarget(UninstallKey, "", "AuroraLauncher 卸载登记项"),
        new UninstallRegistryTarget(AppPathsKey, "", "AuroraLauncher 应用路径登记"),
        new UninstallRegistryTarget(RunKey, RunValueName, "AuroraLauncher 开机启动项")
    };

    public static bool IsAllowedKey(string key)
        => Candidates.Any(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The Run value is shared with every other program: delete only
    /// the one value whose command clearly points at THIS launcher.</summary>
    public static bool IsAllowedValue(string key, string valueName, string? data, string exePath)
    {
        if (!string.Equals(key, RunKey, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(valueName, RunValueName, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(data) || string.IsNullOrWhiteSpace(exePath)) return false;
        return data.IndexOf(Path.GetFileName(exePath), StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Keys that can be removed without reading their contents: the
    /// namespace is already Aurora-specific. The Uninstall/App Paths keys need
    /// content verification, see <see cref="IsVerifiedKey"/>.</summary>
    public static bool NeedsVerification(string key)
        => string.Equals(key, UninstallKey, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(key, AppPathsKey, StringComparison.OrdinalIgnoreCase);

    public static bool IsVerifiedKey(string key, string? displayName, string? command, string exePath)
    {
        if (!NeedsVerification(key)) return true;
        var nameHit = (displayName ?? "").IndexOf("Aurora", StringComparison.OrdinalIgnoreCase) >= 0;
        var pathHit = !string.IsNullOrWhiteSpace(exePath) &&
                      ((command ?? "").IndexOf(exePath, StringComparison.OrdinalIgnoreCase) >= 0 ||
                       (command ?? "").IndexOf(Path.GetFileName(exePath), StringComparison.OrdinalIgnoreCase) >= 0);
        // A key must look like ours AND, when a command exists, actually point
        // at our EXE. Missing command is fine (some entries only have a name).
        return nameHit && (string.IsNullOrWhiteSpace(command) || pathHit);
    }

    public static string Describe(UninstallRegistryTarget target)
        => "注册表：" + target.Display + "（" + target.Reason + "）";
}
