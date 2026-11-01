using AuroraCore.Download;
using AuroraCore.Models;
using AuroraCore.Utils;
using System.IO.Compression;

namespace AuroraCore.Services;

/// <summary>
/// Installs a (vanilla) Minecraft version: resolves inheritance, persists the
/// merged version JSON, downloads the client jar, libraries, asset index and
/// asset objects, then extracts native libraries. All downloads go through the
/// shared DownloadEngine (concurrency, Range resume, SHA-1, atomic commit).
/// </summary>
public sealed class GameInstaller
{
    private readonly VersionResolver _resolver;
    private readonly DownloadEngine _engine;
    private readonly Logger _log = Logger.Instance;

    public GameInstaller(VersionResolver resolver, DownloadEngine engine)
    {
        _resolver = resolver;
        _engine = engine;
    }

    public string VersionDir(string versionId) => Path.Combine(LauncherConfig.Instance.VersionsDir, versionId);
    public string VersionJsonPath(string versionId) => Path.Combine(VersionDir(versionId), versionId + ".json");
    public string ClientJarPath(string versionId) => Path.Combine(VersionDir(versionId), versionId + ".jar");
    public string NativesDir(string versionId) => Path.Combine(VersionDir(versionId), "natives");

    /// <summary>Single source of truth for install completeness. Used by the
    /// pre-launch repair, the UI state display and the download statistics so
    /// they can never disagree again.
    ///  - version JSON present
    ///  - required files: client jar / libraries / loader libs
    ///  - loader versions: JSON only (they have no client jar of their own)
    ///  - natives are extracted fresh at every launch (BuildLaunchSpec) —
    ///    checked only when <paramref name="requireNatives"/> is set, so a
    ///    stale vanilla natives dir can never trigger a bogus repair
    ///  - optional files (assets) never affect Complete.</summary>
    public InstallState Evaluate(string versionId, bool requireNatives = true, string? mcVersion = null)
    {
        var state = new InstallState { VersionId = versionId };
        try
        {
            var version = _resolver.TryReadLocalRaw(versionId);
            if (version == null)
            {
                state.Missing.Add("版本 JSON");
                return state;
            }

            // Client jar: only expected when this id IS a vanilla version.
            var isLoaderVersion = versionId.Contains("fabric-loader-", StringComparison.OrdinalIgnoreCase)
                || versionId.Contains("quilt-loader-", StringComparison.OrdinalIgnoreCase)
                || versionId.Contains("forge-", StringComparison.OrdinalIgnoreCase)
                || versionId.Contains("neoforge-", StringComparison.OrdinalIgnoreCase);
            var jar = ClientJarPath(versionId);
            if (!isLoaderVersion)
            {
                if (!File.Exists(jar) || new FileInfo(jar).Length <= 1024)
                    state.Missing.Add("客户端 jar");
            }
            else if (File.Exists(jar) && new FileInfo(jar).Length > 1024)
            {
                state.HasClientJar = true; // Forge installers do emit one
            }

            // Natives: extracted fresh at launch — informational only unless
            // the caller explicitly requires them.
            var natives = NativesDir(versionId);
            state.HasNatives = Directory.Exists(natives) && Directory.EnumerateFiles(natives).Any();
            if (requireNatives && !state.HasNatives)
                state.Missing.Add("本地库 (natives)");

            // Loader libs: the merged profile references its own libraries
            // (fabric-loader jar, asm, intermediary...) — spot-check each.
            foreach (var lib in version.Libraries)
            {
                if (!LaunchArgumentBuilder.IsAllowed(lib.Rules)) continue;
                var path = lib.Downloads?.Artifact?.Path ?? PathEx.MavenNameToPath(lib.Name);
                if (string.IsNullOrEmpty(path)) continue;
                var file = Path.Combine(LauncherConfig.Instance.LibrariesDir, path);
                if (!File.Exists(file))
                    state.Missing.Add("库 " + lib.Name);
                if (state.Missing.Count > 8) break; // enough evidence
            }
        }
        catch (Exception ex)
        {
            state.Missing.Add("评估异常: " + ex.Message);
        }
        return state;
    }

    public sealed class InstallState
    {
        public string VersionId { get; set; } = "";
        public List<string> Missing { get; } = new();
        public bool HasClientJar { get; set; }
        public bool HasNatives { get; set; }
        public bool Complete => Missing.Count == 0;
        public override string ToString() => Complete ? "Complete" : "Missing: " + string.Join(", ", Missing);
    }

    public bool IsInstalled(string versionId) =>
        File.Exists(ClientJarPath(versionId)) && File.Exists(VersionJsonPath(versionId));

    /// <summary>JSON-only check for loader versions: Fabric/Quilt/Forge
    /// versions have NO client jar of their own (they inherit the vanilla
    /// one), so requiring it here made every launch re-run the repair.</summary>
    public bool IsVersionJsonInstalled(string versionId) =>
        File.Exists(VersionJsonPath(versionId)) && new FileInfo(VersionJsonPath(versionId)).Length > 128;

    /// <summary>Deeper check than <see cref="IsInstalled"/>: client jar, version
    /// JSON and extracted natives must all be present (used for the pre-launch
    /// scan-and-repair pass).</summary>
    public bool IsFullyInstalled(string versionId)
    {
        if (!IsInstalled(versionId)) return false;
        try
        {
            var natives = NativesDir(versionId);
            if (!Directory.Exists(natives) || !Directory.EnumerateFiles(natives).Any()) return false;
            return new FileInfo(ClientJarPath(versionId)).Length > 1024;
        }
        catch { return false; }
    }

    public async Task<VersionJson> InstallAsync(string versionId,
        Action<DownloadProgress>? onProgress = null,
        CancellationToken ct = default,
        bool includeAssets = true,
        Action<string>? onStage = null)
    {
        _log.Info("Install", $"Installing Minecraft {versionId} ...");

        // ---- Stage: resolve + persist version JSON ----
        onStage?.Invoke("json");
        var version = await _resolver.ResolveAsync(versionId, ct).ConfigureAwait(false);
        PathEx.EnsureDirectory(VersionDir(versionId));
        await File.WriteAllTextAsync(VersionJsonPath(versionId), JsonHelper.SerializeIndented(version), ct).ConfigureAwait(false);

        var clientEntries = new List<DownloadEntry>();
        var libEntries = new List<DownloadEntry>();
        var assetEntries = new List<DownloadEntry>();

        // 1) Client jar + logging config
        var client = version.Downloads?.Client;
        if (client != null && !string.IsNullOrEmpty(client.Url))
        {
            clientEntries.Add(new DownloadEntry
            {
                Url = LauncherConfig.Instance.RewriteUrl(client.Url),
                Destination = ClientJarPath(versionId),
                ExpectedSha1 = client.Sha1,
                ExpectedSize = client.Size,
                DisplayName = $"client {versionId}",
                Category = "client"
            });
        }
        if (version.Logging?.Client?.File != null)
        {
            var lf = version.Logging.Client.File;
            if (!string.IsNullOrEmpty(lf.Url))
                clientEntries.Add(new DownloadEntry
                {
                    Url = LauncherConfig.Instance.RewriteUrl(lf.Url),
                    Destination = Path.Combine(VersionDir(versionId), "log_config.xml"),
                    ExpectedSha1 = lf.Sha1,
                    ExpectedSize = lf.Size,
                    DisplayName = "logging config",
                    Category = "client"
                });
        }

        // 2) Libraries (artifacts + natives)
        foreach (var lib in version.Libraries)
        {
            if (!LaunchArgumentBuilder.IsAllowed(lib.Rules)) continue;

            if (lib.Downloads?.Artifact != null)
                libEntries.Add(BuildLibEntry(lib, lib.Downloads.Artifact, null));
            else if (!string.IsNullOrEmpty(lib.Url))
            {
                // Loader-provided libraries (Fabric/Quilt/Forge) point at their
                // own maven via lib.Url — previously these were SILENTLY
                // SKIPPED, leaving the game unable to start.
                libEntries.Add(BuildLibEntry(lib, new ArtifactInfo
                {
                    Path = PathEx.MavenNameToPath(lib.Name),
                    Sha1 = lib.Sha1
                }, null));
            }
            else
            {
                var path = PathEx.MavenNameToPath(lib.Name);
                libEntries.Add(new DownloadEntry
                {
                    Url = LauncherConfig.Instance.RewriteUrl("https://libraries.minecraft.net/" + path),
                    Destination = Path.Combine(LauncherConfig.Instance.LibrariesDir, path),
                    DisplayName = lib.Name,
                    Category = "library"
                });
            }

            if (lib.Natives != null && lib.Natives.TryGetValue("windows", out var classifier))
            {
                if (lib.Downloads?.Classifiers != null && lib.Downloads.Classifiers.TryGetValue(classifier, out var nat))
                    libEntries.Add(BuildLibEntry(lib, nat, classifier));
            }
        }

        // ---- Stage: client ----
        onStage?.Invoke("client");
        await DownloadBatchAsync(clientEntries, onProgress, ct).ConfigureAwait(false);

        // ---- Stage: libraries ----
        onStage?.Invoke("libraries");
        await DownloadBatchAsync(libEntries, onProgress, ct).ConfigureAwait(false);

        // ---- Stage: assets ----
        if (includeAssets)
        {
            onStage?.Invoke("assets");
            await AddAssetEntriesAsync(version, assetEntries, ct).ConfigureAwait(false);
            await DownloadBatchAsync(assetEntries, onProgress, ct).ConfigureAwait(false);
        }

        // ---- Stage: natives ----
        onStage?.Invoke("natives");
        await ExtractNativesAsync(version, NativesDir(versionId)).ConfigureAwait(false);

        _log.Info("Install", $"Installation of {versionId} complete.");
        return version;
    }

    private static DownloadEntry ToBmclapiEntry(DownloadEntry e) => new()
    {
        Url = LauncherConfig.ToBmclapiUrl(e.Url),
        Destination = e.Destination,
        ExpectedSha1 = e.ExpectedSha1,
        ExpectedSize = e.ExpectedSize,
        Required = e.Required,
        DisplayName = e.DisplayName,
        Category = e.Category
    };

    /// <summary>Downloads a batch via the engine; if the official source fails,
    /// retries the failed files through the BMCLAPI mirror automatically.</summary>
    private async Task DownloadBatchAsync(List<DownloadEntry> entries, Action<DownloadProgress>? onProgress, CancellationToken ct)
    {
        if (entries.Count == 0) return;
        try
        {
            await _engine.DownloadAsync(entries, onProgress, ct).ConfigureAwait(false);
        }
        catch (DownloadException)
        {
            _log.Warn("Install", "官方源部分文件下载失败，自动切换 BMCLAPI 镜像重试…");
            await _engine.DownloadAsync(entries.Select(ToBmclapiEntry).ToList(), onProgress, ct).ConfigureAwait(false);
            _log.Info("Install", "BMCLAPI 镜像下载完成。");
        }
    }

    private DownloadEntry BuildLibEntry(Library lib, ArtifactInfo artifact, string? classifier)
    {
        var path = !string.IsNullOrEmpty(artifact.Path)
            ? artifact.Path
            : PathEx.MavenNameToPath(classifier == null ? lib.Name : lib.Name + ":" + classifier);

        string url;
        if (!string.IsNullOrEmpty(artifact.Url))
            url = LauncherConfig.Instance.RewriteUrl(artifact.Url);
        else if (!string.IsNullOrEmpty(lib.Url))
            url = lib.Url.TrimEnd('/') + "/" + path;
        else
            url = LauncherConfig.Instance.RewriteUrl("https://libraries.minecraft.net/" + path);

        return new DownloadEntry
        {
            Url = url,
            Destination = Path.Combine(LauncherConfig.Instance.LibrariesDir, path),
            ExpectedSha1 = artifact.Sha1,
            ExpectedSize = artifact.Size,
            DisplayName = lib.Name + (classifier == null ? "" : ":" + classifier),
            Category = classifier == null ? "library" : "native"
        };
    }

    private async Task AddAssetEntriesAsync(VersionJson version, List<DownloadEntry> entries, CancellationToken ct)
    {
        var assetIndex = version.AssetIndex;
        if (assetIndex == null && !string.IsNullOrEmpty(version.Assets))
            assetIndex = new AssetIndexInfo { Id = version.Assets };

        if (assetIndex == null || string.IsNullOrEmpty(assetIndex.Id))
        {
            _log.Warn("Install", "No asset index information; skipping assets.");
            return;
        }

        string indexUrl = assetIndex.Url != null
            ? LauncherConfig.Instance.RewriteUrl(assetIndex.Url)
            : LauncherConfig.Instance.RewriteUrl($"https://resources.download.minecraft.net/indexes/{assetIndex.Id}.json");

        var indexPath = Path.Combine(LauncherConfig.Instance.AssetIndexesDir, assetIndex.Id + ".json");
        PathEx.EnsureDirectory(LauncherConfig.Instance.AssetIndexesDir);
        _log.Info("Install", $"Downloading asset index {assetIndex.Id}");
        if (!File.Exists(indexPath))
        {
            // Unified component: hedged routes (official×2 + BMCLAPI×2) with
            // per-route timeout, whole-hedge retry and aggregated errors.
            var json = await NetRouting.GetStringAsync(indexUrl, LauncherConfig.ToBmclapiUrl(indexUrl), ct,
                perRouteTimeoutSec: 30, logTag: "Install").ConfigureAwait(false);
            await File.WriteAllTextAsync(indexPath, json, ct).ConfigureAwait(false);
        }

        var index = JsonHelper.Deserialize<AssetIndex>(File.ReadAllText(indexPath));
        _log.Info("Install", $"Asset index contains {index.Objects.Count} objects.");
        foreach (var kv in index.Objects)
        {
            var hash = kv.Value.Hash;
            if (string.IsNullOrEmpty(hash) || hash.Length < 2) continue;
            var rel = $"{hash[..2]}/{hash}";
            entries.Add(new DownloadEntry
            {
                Url = LauncherConfig.Instance.RewriteUrl("https://resources.download.minecraft.net/" + rel),
                Destination = Path.Combine(LauncherConfig.Instance.AssetObjectsDir, rel),
                ExpectedSha1 = hash,
                ExpectedSize = kv.Value.Size,
                DisplayName = "asset " + kv.Key,
                Category = "asset",
                // A missing sound/texture must not block the whole install:
                // optional files are skipped with a warning after retries.
                Required = false
            });
        }
    }

    /// <summary>Extracts native classifier jars into <paramref name="nativesDir"/> (idempotent).</summary>
    public async Task ExtractNativesAsync(VersionJson version, string nativesDir)
    {
        PathEx.EnsureDirectory(nativesDir);
        foreach (var lib in version.Libraries)
        {
            if (!LaunchArgumentBuilder.IsAllowed(lib.Rules)) continue;
            if (lib.Natives == null || !lib.Natives.TryGetValue("windows", out var classifier)) continue;
            if (lib.Downloads?.Classifiers == null || !lib.Downloads.Classifiers.TryGetValue(classifier, out var nat)) continue;

            var path = !string.IsNullOrEmpty(nat.Path)
                ? nat.Path
                : PathEx.MavenNameToPath(lib.Name + ":" + classifier);
            var jarPath = Path.Combine(LauncherConfig.Instance.LibrariesDir, path);
            if (!File.Exists(jarPath)) continue;

            var excludes = lib.Extract?.Exclude ?? new List<string>();
            try
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(jarPath);
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith("/")) continue;
                    if (excludes.Any(ex => entry.FullName.StartsWith(ex, StringComparison.OrdinalIgnoreCase))) continue;
                    if (entry.FullName.Equals("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;
                    var dest = Path.Combine(nativesDir, entry.FullName);
                    PathEx.EnsureDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, true);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("Install", $"Failed to extract natives from {jarPath}: {ex.Message}");
            }
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
