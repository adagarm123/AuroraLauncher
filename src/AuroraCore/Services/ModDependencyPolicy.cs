using AuroraCore.Models;

namespace AuroraCore.Services;

/// <summary>
/// Round-97: two bugs in one screenshot of a FORGE 1.20.1 pack.
///
/// 1. "缺少前置：fabric-api" — impossible for Forge. TryParse used to prefer
///    fabric.mod.json unconditionally, and multi-platform jars ship BOTH that
///    and META-INF/mods.toml. The Fabric metadata's "fabric-api" dependency
///    was then reported as a hard requirement of a Forge instance.
/// 2. fabric-api / kotlinforforge listed repeatedly — Scan deduplicated by
///    (depId, requiredByMod), so N dependants produced N rows.
///
/// This policy is the single place that decides relevance and merging. It is
/// also the gate any future "auto-fix" MUST pass through before downloading
/// anything into mods/.
/// </summary>
public static class ModDependencyPolicy
{
    private static readonly HashSet<string> PlatformIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "java",
        "fabricloader", "fabric_loader", "fabric-loader",
        "forge", "neoforge",
        "quilt_loader", "quiltloader", "quilt-loader"
    };

    private static bool IsFabricFamily(string id)
        => id.Equals("fabric-api", StringComparison.OrdinalIgnoreCase) ||
           id.StartsWith("fabric-", StringComparison.OrdinalIgnoreCase) ||
           id.StartsWith("fabric_", StringComparison.OrdinalIgnoreCase) ||
           id.StartsWith("quilt", StringComparison.OrdinalIgnoreCase) ||
           id.Contains("fabricloader", StringComparison.OrdinalIgnoreCase);

    private static bool IsForgeFamily(string id)
        => id.Equals("forge", StringComparison.OrdinalIgnoreCase) ||
           id.Equals("neoforge", StringComparison.OrdinalIgnoreCase) ||
           id.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase) ||
           id.Contains("kotlinforforge", StringComparison.OrdinalIgnoreCase) ||
           id.EndsWith("-forge", StringComparison.OrdinalIgnoreCase);

    /// <summary>Would an auto-fix ever be allowed to download this id for this
    /// loader? Cross-loader ids are noise, not missing prerequisites.</summary>
    public static bool IsRelevant(string depId, ModLoaderType loader)
    {
        if (string.IsNullOrWhiteSpace(depId)) return false;
        var id = depId.Trim();
        if (PlatformIds.Contains(id)) return false;
        return loader switch
        {
            ModLoaderType.Forge or ModLoaderType.NeoForge => !IsFabricFamily(id),
            ModLoaderType.Fabric or ModLoaderType.Quilt => !IsForgeFamily(id),
            _ => !IsFabricFamily(id) && !IsForgeFamily(id)
        };
    }

    /// <summary>One row per modId. Ranges and "required by" lists are merged so
    /// a later auto-fix can compute their intersection instead of downloading
    /// the same jar three times.</summary>
    public static IReadOnlyList<ModDependency> Merge(IEnumerable<ModDependency> deps, ModLoaderType loader)
        => deps.Where(d => IsRelevant(d.ModId, loader))
            .GroupBy(d => d.ModId, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var ranges = g.Select(d => d.Range).Where(r => !string.IsNullOrWhiteSpace(r))
                    .Distinct(StringComparer.Ordinal).ToList();
                var requiredBy = g.Select(d => d.RequiredBy).Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.Ordinal).ToList();
                return new ModDependency(
                    g.Key,
                    VersionRangePolicy.MergeLabel(ranges),
                    requiredBy.Count == 0 ? "" : string.Join("、", requiredBy.Take(4)) +
                        (requiredBy.Count > 4 ? $" 等 {requiredBy.Count} 个模组" : ""),
                    g.First().RequiredByFile)
                {
                    Ranges = ranges,
                    RequiredByList = requiredBy
                };
            })
            .OrderBy(d => d.ModId, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
