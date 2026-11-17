using AuroraCore.Utils;
using Microsoft.Win32;

namespace AuroraCore.Services;

/// <summary>Round-102: reads and removes ONLY the exact registry locations
/// allowlisted by <see cref="UninstallRegistryPolicy"/>. The registry is
/// machine state, so the scan is read-only until the user explicitly ticks
/// the cleanup checkbox on the uninstall dialog.</summary>
public static class UninstallRegistryService
{
    public sealed record Removal(int Removed, int Failed, IReadOnlyList<string> Failures);

    /// <summary>Finds launcher-owned entries that actually exist. Never writes.</summary>
    public static IReadOnlyList<UninstallRegistryTarget> Scan(string exePath)
    {
        var found = new List<UninstallRegistryTarget>();
        foreach (var candidate in UninstallRegistryPolicy.Candidates)
        {
            try
            {
                if (candidate.IsValue)
                {
                    using var key = Registry.CurrentUser.OpenSubKey(candidate.Key);
                    if (key?.GetValue(candidate.ValueName) is string data &&
                        UninstallRegistryPolicy.IsAllowedValue(candidate.Key, candidate.ValueName, data, exePath))
                        found.Add(candidate);
                    continue;
                }

                using var existing = Registry.CurrentUser.OpenSubKey(candidate.Key);
                if (existing == null) continue;
                if (UninstallRegistryPolicy.NeedsVerification(candidate.Key))
                {
                    var display = existing.GetValue("DisplayName") as string;
                    var command = existing.GetValue("UninstallString") as string ??
                                  existing.GetValue("") as string;
                    if (!UninstallRegistryPolicy.IsVerifiedKey(candidate.Key, display, command, exePath)) continue;
                }
                found.Add(candidate);
            }
            catch (Exception ex)
            {
                Logger.Instance.Warn("Uninstall", $"注册表读取失败 {candidate.Display}: {ex.Message}");
            }
        }
        return found;
    }

    public static Removal Remove(IReadOnlyList<UninstallRegistryTarget> targets)
    {
        var removed = 0;
        var failed = 0;
        var failures = new List<string>();
        foreach (var t in targets)
        {
            // Re-check the allowlist at deletion time. A caller must never be
            // able to smuggle an arbitrary key through this entry point.
            if (!UninstallRegistryPolicy.IsAllowedKey(t.Key))
            {
                failed++;
                failures.Add(t.Display + "：不在白名单");
                continue;
            }
            try
            {
                if (t.IsValue)
                {
                    using var key = Registry.CurrentUser.OpenSubKey(t.Key, writable: true);
                    if (key == null) continue;
                    key.DeleteValue(t.ValueName, throwOnMissingValue: false);
                }
                else
                {
                    // Only delete when the key really exists; fail-closed on
                    // any error so the caller can report it honestly.
                    if (Registry.CurrentUser.OpenSubKey(t.Key) == null) continue;
                    Registry.CurrentUser.DeleteSubKeyTree(t.Key, throwOnMissingSubKey: false);
                }
                removed++;
                Logger.Instance.Info("Uninstall", "已清理 " + UninstallRegistryPolicy.Describe(t));
            }
            catch (Exception ex)
            {
                failed++;
                failures.Add(t.Display + "：" + ex.Message);
                Logger.Instance.Warn("Uninstall", "注册表清理失败 " + t.Display + ": " + ex.Message);
            }
        }
        return new Removal(removed, failed, failures);
    }
}
