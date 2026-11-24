using Microsoft.Win32;

namespace AuroraCore.Services;

/// <summary>Round-104: "跟随系统" was stored in config but every reader
/// treated it as Dark. Resolve it explicitly and read the Windows app theme.</summary>
public static class ThemePolicy
{
    public static ThemePreference Resolve(ThemePreference preference, bool systemUsesLight)
        => preference == ThemePreference.System
            ? (systemUsesLight ? ThemePreference.Light : ThemePreference.Dark)
            : preference;

    public static bool SystemPrefersLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int i ? i != 0 : true;
        }
        catch { return true; }
    }
}
