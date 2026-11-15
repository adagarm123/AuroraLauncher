using AuroraCore.Models;

namespace AuroraCore.Services;

/// <summary>
/// Round-97 groundwork for the NEXT round's "auto-fix missing prerequisites".
/// The user explicitly asked to fix the parser first; this policy is the safe
/// foundation: nothing can reach Modrinth until it has gone through
/// ModDependencyPolicy.IsRelevant + Merge and these loader/slug rules.
///
/// Deliberately NO downloading here yet — that lands with the UI and the
/// DownloadJob integration in round 98.
/// </summary>
public static class PreflightAutoFixPolicy
{
    /// <summary>modId → Modrinth slug. Only entries we are sure about are
    /// mapped; everything else uses the literal id and must be confirmed by a
    /// search before download.</summary>
    private static readonly Dictionary<string, string> Slugs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fabric-api"] = "fabric-api",
        ["kotlinforforge"] = "kotlin-for-forge",
        ["puzzlesaccessapi"] = "puzzles-access-api",
        ["tacz"] = "timeless-and-classics-zero",
        ["architectury-api"] = "architectury-api",
        ["cloth-config"] = "cloth-config"
    };

    public static string SlugFor(string modId)
        => Slugs.TryGetValue((modId ?? "").Trim(), out var slug) ? slug : (modId ?? "").Trim();

    public static bool HasKnownSlug(string modId) => Slugs.ContainsKey((modId ?? "").Trim());

    /// <summary>The ONE loader tag sent to Modrinth's version API. A Forge
    /// instance must never produce a "fabric" facet — guarded by a test.</summary>
    public static string LoaderTag(ModLoaderType loader) => loader switch
    {
        ModLoaderType.Forge => "forge",
        ModLoaderType.NeoForge => "neoforge",
        ModLoaderType.Fabric => "fabric",
        ModLoaderType.Quilt => "quilt",
        _ => ""
    };

    public static IReadOnlyList<string> LoadersFor(ModLoaderType loader)
    {
        var tag = LoaderTag(loader);
        return tag.Length == 0 ? Array.Empty<string>() : new[] { tag };
    }

    /// <summary>Would this version be safe to hand to this instance?</summary>
    public static bool VersionAllowed(ModLoaderType loader, IReadOnlyList<string> versionLoaders,
        string mcVersion, IReadOnlyList<string> gameVersions)
    {
        var tag = LoaderTag(loader);
        if (tag.Length == 0) return false;
        if (!versionLoaders.Any(l => string.Equals(l, tag, StringComparison.OrdinalIgnoreCase))) return false;
        return gameVersions.Count == 0 || gameVersions.Contains(mcVersion);
    }

    /// <summary>All merged ranges must accept the candidate version.</summary>
    public static bool VersionInRange(ModDependency dep, string versionNumber)
        => dep.Ranges.Count == 0 || dep.Ranges.All(r => VersionRangePolicy.Matches(r, versionNumber));
}
