using AuroraCore.Models;

namespace AuroraCore.Services;

/// <summary>Round-71: on-disk copy of the last good version manifest.
///
/// The round-70 screenshot was an EMPTY version list with a red error: the
/// mirror's DNS died, every route hit the same dead host, and there was nothing
/// to fall back to. A launcher that cannot even SHOW versions while offline is
/// worse than one that shows a stale list with a clear "cached" hint — the user
/// can still read which versions exist and understand what is missing.
///
/// Pure file IO, no network, so it is fully testable.</summary>
public static class ManifestCache
{
    public static string PathFor(string rootDir) =>
        Path.Combine(rootDir, "cache", "version_manifest_v2.json");

    public static void Save(string rootDir, string json)
    {
        try
        {
            var path = PathFor(rootDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }
        catch
        {
            // A cache write must never break a successful fetch.
        }
    }

    /// <summary>Returns null for a missing, unreadable, malformed or empty
    /// cache — the caller then reports the original network error instead of
    /// pretending an empty list is valid data.</summary>
    public static VersionManifest? TryLoad(string rootDir)
    {
        try
        {
            var path = PathFor(rootDir);
            if (!File.Exists(path)) return null;
            var manifest = Utils.JsonHelper.Deserialize<VersionManifest>(File.ReadAllText(path));
            return manifest.Versions.Count > 0 ? manifest : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool Exists(string rootDir) => File.Exists(PathFor(rootDir));
}
