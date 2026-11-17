namespace AuroraCore.Services;

/// <summary>Round-102: command-line contract for the temporary uninstall
/// helper. Pure so the round-trip is unit-testable without spawning a process.</summary>
public static class UninstallCleanupArgs
{
    public const string Switch = "--uninstall-cleanup";

    public static string Build(string manifestPath)
        => Switch + " \"" + manifestPath + "\"";

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
