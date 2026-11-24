namespace AuroraCore.Services;

/// <summary>Round-102: command-line contract for the temporary uninstall
/// helper. Pure so the round-trip is unit-testable without spawning a process.</summary>
public static class UninstallCleanupArgs
{
    public const string Switch = "--uninstall-cleanup";

    public static string Build(string manifestPath)
        => Switch + " \"" + manifestPath + "\"";

    /// <summary>Round-104: the manifest arrives from a command line, so the
    /// helper must verify the EXE it deletes really is OUR launcher. Without
    /// this, a hand-written manifest could point ExePath at any user file.
    /// Helper copies always live in %TEMP% under our private prefix.</summary>
    public static bool IsPlausibleExePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var name = Path.GetFileName(path);
        if (!string.Equals(name, "AuroraLauncher.exe", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var dir = Path.GetFullPath(Path.GetDirectoryName(path) ?? "")
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            // A copied helper deleting the ORIGINAL exe is expected. A random
            // AuroraLauncher.exe elsewhere is not something the helper may touch.
            return !dir.StartsWith(temp, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool IsPlausibleHelperPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var name = Path.GetFileName(path);
        if (!name.StartsWith("aurora-uninstall-", StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var dir = Path.GetFullPath(Path.GetDirectoryName(path) ?? "")
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return dir.StartsWith(temp, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool TryParse(IEnumerable<string> args, out string? manifestPath)
    {
        manifestPath = null;
        var list = args.ToList();
        var index = list.FindIndex(a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= list.Count) return false;
        var path = list[index + 1].Trim('"');
        if (path.Length == 0) return false;
        manifestPath = path;
        return true;
    }
}
