using System.IO;

namespace AuroraCore.Services;

/// <summary>
/// Round-84: the data root must never be a throw-away folder.
///
/// The round-78 crash-card smoke sets <c>RootDirectory</c> to a temp dir so it
/// cannot touch user data — but a repair button inside that smoke calls
/// <c>cfg.Save()</c>, which writes to the REAL config path. The result shipped:
/// a user's launcher started reading <c>%TEMP%\aurora-crash-smoke-xxxx</c>,
/// showing one empty instance that cannot launch, while their real instances
/// were still on disk. Two defences:
///
/// 1. <see cref="IsEphemeral"/> labels such a root so <c>Save()</c> refuses to
///    persist it and <c>Load()</c> heals an already-broken config.
/// 2. Tests must isolate the config file itself (LauncherConfig.UseTestRoot),
///    not just the root directory.
/// </summary>
public static class DataRootPolicy
{
    /// <summary>Production root: the portable folder when portable.txt exists,
    /// otherwise %APPDATA%\AuroraLauncher.</summary>
    public static string DefaultRoot()
        => PortableModePolicy.ResolveRoot(Environment.ProcessPath)
           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AuroraLauncher");

    /// <summary>Round-84: what the config should use, given what it stored.
    /// Pure so the heal is testable without rewriting a real config file.
    /// <c>Healed=true</c> means the caller must persist the repaired value.</summary>
    public static (string Root, bool Healed) Sanitize(string? stored, string defaultRoot)
        => IsEphemeral(stored) ? (defaultRoot, true) : (stored ?? defaultRoot, false);

    /// <summary>True for our own test/smoke roots: a direct child of %TEMP%
    /// whose name starts with "aurora-". A real user never stores data there,
    /// and if they somehow did, moving it back to %APPDATA% is still far better
    /// than reading a folder Windows deletes on the next cleanup.</summary>
    public static bool IsEphemeral(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!full.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return false;
            var name = Path.GetFileName(full);
            return name.StartsWith("aurora-", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
