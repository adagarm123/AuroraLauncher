using AuroraCore.Models;
using AuroraCore.Utils;

namespace AuroraCore.Services;

/// <summary>
/// Resolves a Minecraft version by recursively following <c>inheritsFrom</c>
/// and merging fields (libraries appended, scalar fields overridden by child,
/// argument lists concatenated). The result is a fully-populated VersionJson
/// ready for installation and launching.
/// </summary>
public sealed class VersionResolver
{
    private readonly VersionManifestService _manifest;
    private readonly Logger _log = Logger.Instance;

    public VersionResolver(VersionManifestService manifest) => _manifest = manifest;

    public async Task<VersionJson> ResolveAsync(string versionId, CancellationToken ct = default, bool useCache = true)
    {
        var cachePath = Path.Combine(LauncherConfig.Instance.VersionsDir, versionId, versionId + ".json");
        if (useCache && File.Exists(cachePath))
        {
            try
            {
                var cached = JsonHelper.Deserialize<VersionJson>(File.ReadAllText(cachePath));
                if (cached.MainClass != null)
                {
                    // CRITICAL: a cached loader profile (fabric/quilt) inherits
                    // from the vanilla version and has NO arguments of its own.
                    // Returning it raw produced a classpath-less launch command
                    // (ClassNotFoundException: KnotClient). Always merge with
                    // the inherited parent.
                    if (string.IsNullOrEmpty(cached.InheritsFrom)) return cached;
                    var parent = await ResolveAsync(cached.InheritsFrom, ct, useCache).ConfigureAwait(false);
                    return Merge(parent, cached);
                }
                // Cache entry is valid JSON but missing fields (e.g. no
                // mainClass) — treat as unusable and fall through to network.
                _log.Warn("Resolver", $"Cache entry for {versionId} is incomplete; refetching.");
                TryDeleteCache(cachePath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Corrupted cache entry: degrade gracefully to the network
                // path instead of failing.
                _log.Warn("Resolver", $"Cache entry for {versionId} is corrupt ({ex.Message}); refetching.");
                TryDeleteCache(cachePath);
            }
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return await ResolveRecursive(versionId, seen, ct).ConfigureAwait(false);
    }

    private static void TryDeleteCache(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>THE sanctioned way to read a raw (unmerged) local version
    /// JSON from outside this class. Everything else must go through
    /// ResolveAsync — a guard test enforces that no other code deserializes
    /// VersionJson directly.</summary>
    public VersionJson? TryReadLocalRaw(string versionId)
    {
        try
        {
            var cachePath = Path.Combine(LauncherConfig.Instance.VersionsDir, versionId, versionId + ".json");
            if (!File.Exists(cachePath)) return null;
            var v = JsonHelper.Deserialize<VersionJson>(File.ReadAllText(cachePath));
            return v.MainClass == null ? null : v; // incomplete entry = unusable
        }
        catch { return null; }
    }

    private async Task<VersionJson> ResolveRecursive(string versionId, HashSet<string> seen, CancellationToken ct)
    {
        if (!seen.Add(versionId))
            throw new InvalidOperationException($"Version inheritance cycle detected at '{versionId}'.");

        // Local installed version JSON is authoritative (loader profiles are
        // NOT in the Mojang manifest) — only fall back to the manifest when
        // the file is absent or unreadable.
        VersionJson raw;
        var cachePath = Path.Combine(LauncherConfig.Instance.VersionsDir, versionId, versionId + ".json");
        if (File.Exists(cachePath))
        {
            try
            {
                raw = JsonHelper.Deserialize<VersionJson>(File.ReadAllText(cachePath));
                if (raw.MainClass == null) throw new InvalidOperationException("version JSON has no mainClass");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn("Resolver", $"Local version JSON for {versionId} is unreadable ({ex.Message}); trying manifest.");
                raw = JsonHelper.Deserialize<VersionJson>(await _manifest.GetVersionJsonRawAsync(versionId, ct).ConfigureAwait(false));
            }
        }
        else
        {
            raw = JsonHelper.Deserialize<VersionJson>(await _manifest.GetVersionJsonRawAsync(versionId, ct).ConfigureAwait(false));
        }

        if (string.IsNullOrEmpty(raw.InheritsFrom))
            return raw;

        _log.Info("Resolver", $"Resolving inheritance: {versionId} -> inherits {raw.InheritsFrom}");
        var parent = await ResolveRecursive(raw.InheritsFrom, seen, ct).ConfigureAwait(false);
        return Merge(parent, raw);
    }

    private static VersionJson Merge(VersionJson parent, VersionJson child)
    {
        var m = new VersionJson
        {
            Id = child.Id ?? parent.Id,
            Type = child.Type ?? parent.Type,
            MainClass = child.MainClass ?? parent.MainClass,
            Assets = child.Assets ?? parent.Assets,
            AssetIndex = child.AssetIndex ?? parent.AssetIndex,
            JavaVersion = child.JavaVersion ?? parent.JavaVersion,
            MinimumLauncherVersion = child.MinimumLauncherVersion != 0 ? child.MinimumLauncherVersion : parent.MinimumLauncherVersion,
            ComplianceLevel = child.ComplianceLevel != 0 ? child.ComplianceLevel : parent.ComplianceLevel,
            Logging = child.Logging ?? parent.Logging,
            Time = child.Time ?? parent.Time,
            ReleaseTime = child.ReleaseTime ?? parent.ReleaseTime,
            Downloads = MergeDownloads(parent.Downloads, child.Downloads),
            Libraries = parent.Libraries.Concat(child.Libraries).ToList(),
            Processors = child.Processors ?? parent.Processors,
            Data = MergeData(parent.Data, child.Data)
        };

        if (!string.IsNullOrEmpty(child.MinecraftArguments))
        {
            m.MinecraftArguments = child.MinecraftArguments;
            m.Arguments = new ArgumentSection
            {
                Game = parent.Arguments?.Game?.ToList(),
                Jvm = MergeLists(parent.Arguments?.Jvm, child.Arguments?.Jvm)
            };
        }
        else
        {
            m.MinecraftArguments = parent.MinecraftArguments;
            m.Arguments = new ArgumentSection
            {
                Game = MergeLists(parent.Arguments?.Game, child.Arguments?.Game),
                Jvm = MergeLists(parent.Arguments?.Jvm, child.Arguments?.Jvm)
            };
        }
        return m;
    }

    private static DownloadInfo? MergeDownloads(DownloadInfo? parent, DownloadInfo? child)
    {
        if (parent == null) return child;
        if (child == null) return parent;
        return new DownloadInfo
        {
            Client = child.Client ?? parent.Client,
            Server = child.Server ?? parent.Server,
            ClientMappings = child.ClientMappings ?? parent.ClientMappings,
            ServerMappings = child.ServerMappings ?? parent.ServerMappings
        };
    }

    private static Dictionary<string, JsonElement>? MergeData(Dictionary<string, JsonElement>? parent, Dictionary<string, JsonElement>? child)
    {
        if (parent == null) return child;
        if (child == null) return parent;
        var d = new Dictionary<string, JsonElement>(parent);
        foreach (var kv in child) d[kv.Key] = kv.Value;
        return d;
    }

    private static List<JsonElement>? MergeLists(List<JsonElement>? a, List<JsonElement>? b)
    {
        if (a == null && b == null) return null;
        var list = new List<JsonElement>();
        if (a != null) list.AddRange(a);
        if (b != null) list.AddRange(b);
        return list;
    }

    private static List<JsonElement>? MergeLists(List<JsonElement>? a, List<JsonElement>? b, List<JsonElement>? c)
    {
        if (a == null && b == null && c == null) return null;
        var list = new List<JsonElement>();
        if (a != null) list.AddRange(a);
        if (b != null) list.AddRange(b);
        if (c != null) list.AddRange(c);
        return list;
    }

    public static int GetRequiredJavaMajor(VersionJson version) => version.JavaVersion?.MajorVersion ?? 8;
}
