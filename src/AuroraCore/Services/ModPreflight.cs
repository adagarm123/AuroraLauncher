using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuroraCore.Models;

namespace AuroraCore.Services;

/// <summary>One unmet requirement found before launch.</summary>
public sealed record ModDependency(string ModId, string Range, string RequiredBy, string RequiredByFile)
{
    /// <summary>Round-97: every distinct range demanded for this id (merged).</summary>
    public IReadOnlyList<string> Ranges { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RequiredByList { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Round-77: pre-launch mod precheck. Reads <c>fabric.mod.json</c> /
/// <c>META-INF/mods.toml</c> inside every enabled jar and answers three
/// questions the crash log can only answer after the fact:
///  - is a required mod missing?
///  - do two installed mods declare each other incompatible?
///  - is the same mod installed twice (two jars, same id)?
///
/// Deliberately conservative: platform ids and Fabric's module ids are resolved
/// through <c>provides</c>, because a monolithic Fabric API jar satisfies dozens
/// of <c>fabric-*</c> module dependencies. False alarms here are worse than no
/// feature at all — the user would learn to click through the dialog.
/// </summary>
public static class ModPreflight
{
    /// <summary>Ids that come from the loader/platform, never from a jar.</summary>
    private static readonly HashSet<string> PlatformIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "java",
        "fabricloader", "fabric_loader", "fabric-loader",
        "forge", "neoforge",
        "quilt_loader", "quiltloader", "quilt-loader"
    };

    public sealed class Report
    {
        public int ScannedJars { get; init; }
        public IReadOnlyList<ModDependency> Missing { get; init; } = Array.Empty<ModDependency>();
        public IReadOnlyList<string> Conflicts { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Duplicates { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> WrongLoader { get; init; } = Array.Empty<string>();
        /// <summary>Fabric API is missing and at least one mod needs it.</summary>
        public bool FabricApiMissing { get; init; }

        /// <summary>Round-100: every id the scan can actually see (own ids +
        /// <c>provides</c>). The auto-fix uses this as the ONLY proof that a
        /// downloaded prerequisite is really installed.</summary>
        public IReadOnlyList<string> PresentIds { get; init; } = Array.Empty<string>();

        public bool HasIssues => Missing.Count > 0 || Conflicts.Count > 0 ||
                                 Duplicates.Count > 0 || WrongLoader.Count > 0;

        public int IssueCount => Missing.Count + Conflicts.Count + Duplicates.Count + WrongLoader.Count;

        public string Headline => Missing.Count > 0
            ? $"缺少 {Missing.Count} 个前置模组"
            : Conflicts.Count > 0 ? $"检测到 {Conflicts.Count} 处模组冲突"
            : Duplicates.Count > 0 ? "有模组被重复安装"
            : WrongLoader.Count > 0 ? "有模组用错了加载器"
            : "模组检查通过";
    }

    /// <summary>Scans a single instance's <c>mods/</c> folder. Pure file IO, no
    /// network — measured at ~1 ms per jar, so a 60-mod pack costs well under a
    /// second and can run on a background thread before launch.</summary>
    public static Report Scan(string instanceDirectory, ModLoaderType loader, Action<string>? log = null)
    {
        var modsDir = Path.Combine(instanceDirectory, "mods");
        var mods = new List<ParsedMod>();
        if (!Directory.Exists(modsDir))
            return new Report { ScannedJars = 0 };

        foreach (var file in Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly))
        {
            var parsed = TryParse(file, loader, log);
            if (parsed != null) mods.Add(parsed);
        }

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
        {
            if (mod.ModId.Length > 0) present.Add(mod.ModId);
            foreach (var provided in mod.Provides) present.Add(provided);
        }

        var missing = new List<ModDependency>();
        var seenMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
        {
            foreach (var (depId, range) in mod.Depends)
            {
                if (PlatformIds.Contains(depId)) continue;
                // Round-97: a Forge instance must never be told it needs
                // fabric-api (and vice versa). Cross-loader ids are noise from
                // multi-platform jars, not missing prerequisites.
                if (!ModDependencyPolicy.IsRelevant(depId, loader)) continue;
                if (present.Contains(depId)) continue;
                var key = depId + "|" + mod.ModId;
                if (!seenMissing.Add(key)) continue;
                missing.Add(new ModDependency(depId, range, mod.DisplayName, mod.FileName));
            }
        }

        // Conflicts: a "breaks" entry whose target is actually installed.
        var displayById = mods
            .Where(m => m.ModId.Length > 0)
            .GroupBy(m => m.ModId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DisplayName, StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<string>();
        foreach (var mod in mods)
        {
            foreach (var (brokenId, _) in mod.Breaks)
            {
                if (!present.Contains(brokenId)) continue;
                var other = displayById.TryGetValue(brokenId, out var name) ? name : brokenId;
                var line = $"「{mod.DisplayName}」声明与「{other}」不兼容";
                if (!conflicts.Contains(line)) conflicts.Add(line);
            }
        }

        var duplicates = mods
            .Where(m => m.ModId.Length > 0)
            .GroupBy(m => m.ModId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => $"模组 {g.Key} 重复安装：{string.Join("、", g.Select(m => m.FileName))}")
            .ToList();

        var wrongLoader = new List<string>();
        foreach (var mod in mods)
        {
            var line = loader switch
            {
                ModLoaderType.Fabric when mod.Loader is "forge" or "neoforge" =>
                    $"{mod.FileName} 是 {mod.Loader} 模组，当前实例用的是 Fabric",
                ModLoaderType.Forge when mod.Loader == "fabric" =>
                    $"{mod.FileName} 是 Fabric 模组，当前实例用的是 Forge",
                ModLoaderType.NeoForge when mod.Loader == "fabric" =>
                    $"{mod.FileName} 是 Fabric 模组，当前实例用的是 NeoForge",
                _ => null
            };
            if (line != null) wrongLoader.Add(line);
        }

        // Round-97: one row per modId, with all ranges/dependants merged.
        var mergedMissing = ModDependencyPolicy.Merge(missing, loader);
        var fabricApiRelevant = loader == ModLoaderType.Fabric &&
            mergedMissing.Any(d => d.ModId.Equals("fabric-api", StringComparison.OrdinalIgnoreCase) ||
                                   d.ModId.StartsWith("fabric-", StringComparison.OrdinalIgnoreCase));

        var report = new Report
        {
            ScannedJars = mods.Count,
            Missing = mergedMissing,
            Conflicts = conflicts,
            Duplicates = duplicates,
            WrongLoader = wrongLoader,
            FabricApiMissing = fabricApiRelevant,
            PresentIds = present.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
        };

        log?.Invoke($"[Preflight] 扫描 {report.ScannedJars} 个模组：缺少前置 {mergedMissing.Count}（原始 {missing.Count} 条）· " +
                    $"按 {loader} 过滤并去重、" +
                    $"冲突 {conflicts.Count}、重复 {duplicates.Count}、加载器不符 {wrongLoader.Count}");
        return report;
    }

    private sealed class ParsedMod
    {
        public string FileName { get; init; } = "";
        public string ModId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Loader { get; set; } = "";
        public List<string> Provides { get; } = new();
        public List<(string Id, string Range)> Depends { get; } = new();
        public List<(string Id, string Range)> Breaks { get; } = new();
    }

    /// <summary>Round-97: pick the metadata file that belongs to THIS
    /// instance's loader FIRST. Multi-platform jars ship both fabric.mod.json
    /// and META-INF/mods.toml; reading the Fabric one for a Forge instance is
    /// what produced the impossible "Forge needs fabric-api" report.
    /// The other format is only a fallback (and its deps are still filtered by
    /// ModDependencyPolicy.IsRelevant).</summary>
    private static ParsedMod? TryParse(string jarPath, ModLoaderType loader, Action<string>? log)
    {
        var fileName = Path.GetFileName(jarPath);
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            var preferFabric = loader is ModLoaderType.Fabric or ModLoaderType.Quilt;
            var readers = preferFabric
                ? new Func<ParsedMod?>[] { () => ReadFabricMeta(zip, fileName), () => ReadForgeMeta(zip, fileName) }
                : new Func<ParsedMod?>[] { () => ReadForgeMeta(zip, fileName), () => ReadFabricMeta(zip, fileName) };
            foreach (var read in readers)
            {
                var parsed = read();
                if (parsed != null) return parsed;
            }
            return null; // not a mod jar (resource pack, shader, plain library)
        }
        catch (Exception ex)
        {
            log?.Invoke($"[Preflight] 无法读取 {fileName} 的元数据：{ex.Message}");
            return null;
        }
    }

    private static ParsedMod? ReadFabricMeta(ZipArchive zip, string fileName)
    {
        foreach (var entryName in new[] { "fabric.mod.json", "quilt.mod.json" })
        {
            var entry = zip.GetEntry(entryName);
            if (entry == null) continue;
            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.TryGetProperty("quilt_loader", out var quiltLoader)) root = quiltLoader;

            var mod = new ParsedMod
            {
                FileName = fileName,
                Loader = entryName.Contains("quilt") ? "quilt" : "fabric"
            };
            if (root.TryGetProperty("id", out var id)) mod.ModId = id.GetString() ?? "";
            if (root.TryGetProperty("name", out var name)) mod.DisplayName = name.GetString() ?? "";
            if (mod.DisplayName.Length == 0) mod.DisplayName = mod.ModId.Length > 0 ? mod.ModId : fileName;

            if (root.TryGetProperty("provides", out var provides) && provides.ValueKind == JsonValueKind.Array)
                foreach (var item in provides.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } pid)
                        mod.Provides.Add(pid);

            ReadFabricMap(root, "depends", mod.Depends);
            ReadFabricMap(root, "breaks", mod.Breaks);
            return mod;
        }
        return null;
    }

    private static ParsedMod? ReadForgeMeta(ZipArchive zip, string fileName)
    {
        foreach (var tomlName in new[] { "META-INF/mods.toml", "META-INF/neoforge.mods.toml" })
        {
            var entry = zip.GetEntry(tomlName);
            if (entry == null) continue;
            using var reader = new StreamReader(entry.Open());
            var toml = reader.ReadToEnd();
            var mod = new ParsedMod
            {
                FileName = fileName,
                Loader = (tomlName.Contains("neoforge") ||
                          (toml.Contains("modLoader=\"javafml\"") && toml.Contains("neo")))
                    ? "neoforge" : "forge"
            };
            mod.ModId = FirstValue(toml, "modId") ?? "";
            mod.DisplayName = FirstValue(toml, "displayName") ?? (mod.ModId.Length > 0 ? mod.ModId : fileName);
            ReadForgeDependencies(toml, mod.Depends, mod.Breaks);
            return mod;
        }
        return null;
    }

    private static void ReadFabricMap(JsonElement root, string property, List<(string, string)> target)
    {
        if (!root.TryGetProperty(property, out var map) || map.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in map.EnumerateObject())
        {
            var range = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? "*",
                JsonValueKind.Array => string.Join(" || ", prop.Value.EnumerateArray()
                    .Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString())),
                _ => "*"
            };
            if (prop.Name.Length > 0) target.Add((prop.Name, string.IsNullOrEmpty(range) ? "*" : range));
        }
    }

    /// <summary>Targeted TOML reader for <c>[[dependencies.&lt;modid&gt;]]</c>
    /// blocks. A full TOML parser would be nice, but this file has one shape and
    /// pulling in a parser is not worth a dependency.</summary>
    private static void ReadForgeDependencies(string toml, List<(string, string)> depends, List<(string, string)> breaks)
    {
        var lines = toml.Split('\n');
        var inBlock = false;
        string depId = "", range = "*", type = "";
        var mandatory = true;

        void Flush()
        {
            if (!inBlock || depId.Length == 0) return;
            if (!mandatory || type.Equals("optional", StringComparison.OrdinalIgnoreCase)) return;
            if (type.Equals("incompatible", StringComparison.OrdinalIgnoreCase)) breaks.Add((depId, range));
            else depends.Add((depId, range));
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("[[dependencies.", StringComparison.Ordinal))
            {
                Flush();
                inBlock = true;
                depId = ""; range = "*"; type = ""; mandatory = true;
                continue;
            }
            if (line.StartsWith("[[", StringComparison.Ordinal) || line.StartsWith("[", StringComparison.Ordinal))
            {
                Flush();
                inBlock = false;
                continue;
            }
            if (!inBlock) continue;

            if (line.StartsWith("modId", StringComparison.Ordinal))
                depId = Unquote(line[(line.IndexOf('=') + 1)..]);
            else if (line.StartsWith("versionRange", StringComparison.Ordinal))
                range = Unquote(line[(line.IndexOf('=') + 1)..]);
            else if (line.StartsWith("mandatory", StringComparison.Ordinal))
                mandatory = !line.EndsWith("false", StringComparison.OrdinalIgnoreCase);
            else if (line.StartsWith("type", StringComparison.Ordinal))
                type = Unquote(line[(line.IndexOf('=') + 1)..]);
        }
        Flush();
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim().TrimEnd(',');
        var match = Regex.Match(trimmed, "\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : trimmed;
    }

    private static string? FirstValue(string toml, string key)
    {
        var m = Regex.Match(toml, $"(?m)^\\s*{Regex.Escape(key)}\\s*=\\s*\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }
}
