using System.Xml.Linq;
using AuroraCore.Download;
using AuroraCore.Utils;

namespace AuroraCore.Services;

public enum ModLoaderType { Vanilla, NeoForge, Forge, Fabric, Quilt }

public class ModLoaderInstallResult
{
    public bool Success { get; set; }
    public string? VersionId { get; set; }
    /// <summary>The BARE loader version (e.g. "0.20.1") — distinct from
    /// VersionId so instances never store a full version id here.</summary>
    public string? LoaderVersion { get; set; }
    public string? MainClass { get; set; }
    public string Log { get; set; } = "";
    public string? Error { get; set; }
}

public sealed record LoaderVersionInfo(string Version, bool Stable);

/// <summary>Installs Forge / NeoForge / Fabric / Quilt by downloading the official
/// installer JAR and executing it against the shared game root (so the loader's
/// version JSON, libraries and processed jars land in our central
/// versions/libraries folders and launch paths stay consistent). This is a real
/// install, not a placeholder: the installer runs the loader's full setup
/// including any jar processing.
/// </summary>
public sealed class ModLoaderInstaller
{
    private readonly DownloadEngine _engine;
    private readonly Logger _log = Logger.Instance;
    private static readonly string CacheDir = Path.Combine(LauncherConfig.Instance.RootDirectory, "cache", "installers");

    public ModLoaderInstaller(DownloadEngine engine) => _engine = engine;

    /// <summary>NeoForge versions its builds after the MC version WITHOUT the
    /// leading "1." (MC 1.21.1 → 21.1.x, MC 1.21 → 21.0.x). Translates an MC
    /// version into the matching NeoForge version prefix.</summary>
    internal static string NeoForgePrefix(string mc)
    {
        var p = mc.Split('.');
        if (p.Length >= 3 && p[0] == "1") return p[1] + "." + p[2];
        if (p.Length == 2 && p[0] == "1") return p[1] + ".0";
        return mc;
    }

    /// <summary>GETs loader metadata via hedged multi-route: 官方(默认) /
    /// 官方(直连) / BMCLAPI(默认) / BMCLAPI(直连) fired simultaneously — first
    /// success wins, so dead routes no longer stall the version list.</summary>
    internal static Task<string> GetStringWithFallbackAsync(string url, CancellationToken ct)
        => NetRouting.GetStringAsync(url, LauncherConfig.ToBmclapiUrl(url), ct, perRouteTimeoutSec: 15, logTag: "ModLoader");

    // ---- In-memory caches (round-49: loader list must feel instant) ----
    // Forge/NeoForge metadata is a LARGE maven-metadata.xml covering ALL MC
    // versions (~1MB+, thousands of entries). Re-downloading it on every
    // version click was the dominant "loader panel appears slowly" cost.
    //
    // TTL semantics (round-51, deliberate): ABSOLUTE expiry — entries expire
    // 30 minutes after FETCH, never renewed on access (predictable: a new
    // loader release becomes visible at most 30 minutes later, no matter how
    // often the user browses). Cache hits are logged with the remaining TTL.
    //
    // Two layers, independent invalidation (round-51 fingerprint fix — the
    // round-33 "derived cache without fingerprint" mistake, caught here):
    //   layer 1: raw maven-metadata.xml per loader (forge/neoforge), stamped;
    //   layer 2: parsed list per (loader, mcVersion) — carries the STAMP of
    //   the raw metadata it was parsed from. When layer 1 is refetched the
    //   stamp changes, so stale parsed results are discarded and recomputed
    //   locally (zero extra network) instead of outliving their source.
    private static readonly TimeSpan MemoryTtl = TimeSpan.FromMinutes(30);
    private sealed class RawMetaEntry { public string Body = ""; public DateTimeOffset At; }
    private sealed class ListEntry { public DateTimeOffset At; public long SourceStamp = -1; public List<LoaderVersionInfo> List = new(); }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ModLoaderType, RawMetaEntry> _mavenMemory = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(ModLoaderType, string), ListEntry> _listMemory = new();

    /// <summary>True when a parsed list entry is still usable: within TTL AND
    /// derived from the raw metadata currently in memory (fingerprint match).
    /// Fabric/Quilt lists are self-contained (SourceStamp -1, no raw layer).
    /// Logs the remaining TTL on every hit for "why don't I see the new
    /// version" diagnosis.</summary>
    private bool TryTakeListFromMemory((ModLoaderType, string) key, ModLoaderType type, out List<LoaderVersionInfo> list)
    {
        list = new List<LoaderVersionInfo>();
        if (!_listMemory.TryGetValue(key, out var e) || DateTimeOffset.UtcNow - e.At >= MemoryTtl) return false;
        if (e.SourceStamp >= 0 &&
            (!_mavenMemory.TryGetValue(type, out var raw) || raw.At.UtcTicks != e.SourceStamp))
            return false; // raw metadata was refetched → derived list is stale
        var remaining = (int)(MemoryTtl - (DateTimeOffset.UtcNow - e.At)).TotalMinutes;
        _log.Info("ModLoader", $"List cache hit: {type} {key.Item2} (剩余 TTL: {remaining} 分钟)");
        list = e.List;
        return true;
    }

    /// <summary>Fetches the (large) full maven-metadata.xml for Forge/NeoForge
    /// through the memory cache; falls back to network when stale/missing.</summary>
    private async Task<RawMetaEntry> GetMavenMetadataAsync(ModLoaderType type, string url, CancellationToken ct)
    {
        if (_mavenMemory.TryGetValue(type, out var hit) && DateTimeOffset.UtcNow - hit.At < MemoryTtl)
            return hit;
        var xml = await GetStringWithFallbackAsync(url, ct).ConfigureAwait(false);
        var entry = new RawMetaEntry { Body = xml, At = DateTimeOffset.UtcNow };
        _mavenMemory[type] = entry;
        return entry;
    }

    private static string MetaCachePath(ModLoaderType type, string mcVersion)
        => Path.Combine(CacheDir, $"meta-{type}-{mcVersion}.json");

    private static void CacheMeta(ModLoaderType type, string mcVersion, string body)
    {
        try
        {
            PathEx.EnsureDirectory(CacheDir);
            File.WriteAllText(MetaCachePath(type, mcVersion), body);
        }
        catch { /* best effort */ }
    }

    /// <summary>Last-resort fallback: version list cached from a previous
    /// successful fetch, so a temporarily dead network does not block
    /// installing a loader that was browsable minutes ago.</summary>
    private List<LoaderVersionInfo>? TryReadCachedMeta(ModLoaderType type, string mcVersion)
    {
        try
        {
            var path = MetaCachePath(type, mcVersion);
            if (!File.Exists(path)) return null;
            var body = File.ReadAllText(path);
            return type switch
            {
                ModLoaderType.NeoForge => ParseMavenStable(body, NeoForgePrefix(mcVersion)),
                ModLoaderType.Forge => ParseMavenStable(body, mcVersion),
                _ => ParseMetaStable(body)
            };
        }
        catch { return null; }
    }

    /// <summary>Lists loader versions with a stable/beta marker, for the
    /// version-picker dropdown. Successful responses are cached; when every
    /// route fails the cache is used as a fallback.</summary>
    public async Task<List<LoaderVersionInfo>> ListVersionInfosAsync(ModLoaderType type, string mcVersion, CancellationToken ct = default)
    {
        // Fresh in-memory result → zero network, instant (repeated selections).
        var key = (type, mcVersion);
        if (TryTakeListFromMemory(key, type, out var cached))
            return cached;
        try
        {
            List<LoaderVersionInfo> list;
            long sourceStamp = -1;
            switch (type)
            {
                case ModLoaderType.Fabric:
                {
                    var json = await GetStringWithFallbackAsync(
                        $"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}", ct).ConfigureAwait(false);
                    CacheMeta(type, mcVersion, json);
                    list = ParseMetaStable(json);
                    break;
                }
                case ModLoaderType.Quilt:
                {
                    var json = await GetStringWithFallbackAsync(
                        $"https://meta.quiltmc.org/v3/versions/loader/{mcVersion}", ct).ConfigureAwait(false);
                    CacheMeta(type, mcVersion, json);
                    list = ParseMetaStable(json);
                    break;
                }
                case ModLoaderType.NeoForge:
                {
                    var raw = await GetMavenMetadataAsync(type,
                        "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml", ct).ConfigureAwait(false);
                    CacheMeta(type, mcVersion, raw.Body);
                    list = ParseMavenStable(raw.Body, NeoForgePrefix(mcVersion));
                    sourceStamp = raw.At.UtcTicks; // fingerprint of the raw metadata this list was derived from
                    break;
                }
                case ModLoaderType.Forge:
                {
                    var raw = await GetMavenMetadataAsync(type,
                        "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml", ct).ConfigureAwait(false);
                    CacheMeta(type, mcVersion, raw.Body);
                    list = ParseMavenStable(raw.Body, mcVersion);
                    sourceStamp = raw.At.UtcTicks;
                    break;
                }
                default:
                    return new List<LoaderVersionInfo>();
            }
            _listMemory[key] = new ListEntry { At = DateTimeOffset.UtcNow, SourceStamp = sourceStamp, List = list };
            return list;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn("ModLoader", $"Failed to list {type} versions for {mcVersion}: {ex.Message}");
            var diskCached = TryReadCachedMeta(type, mcVersion);
            if (diskCached is { Count: > 0 })
            {
                _log.Info("ModLoader", $"Using cached {type} version list for {mcVersion} ({diskCached.Count} entries).");
                return diskCached;
            }
            return new List<LoaderVersionInfo>();
        }
    }

    private static List<LoaderVersionInfo> ParseMavenStable(string xml, string mcPrefix)
    {
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        return doc.Descendants("version")
            .Select(e => e.Value)
            .Where(v => v.StartsWith(mcPrefix))
            .Select(v => new LoaderVersionInfo(v, Stable: true))
            .ToList();
    }

    private static List<LoaderVersionInfo> ParseMetaStable(string json)
    {
        var list = new List<LoaderVersionInfo>();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            // Accept both shapes: installer meta has a top-level "version",
            // loader meta nests it under "loader": {"version": ..., "stable": ...}.
            string? v = null;
            bool stable = false;
            if (e.TryGetProperty("version", out var ver) && ver.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                v = ver.GetString();
                stable = e.TryGetProperty("stable", out var st) && st.ValueKind == System.Text.Json.JsonValueKind.True;
            }
            else if (e.TryGetProperty("loader", out var l) && l.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (l.TryGetProperty("version", out var lv) && lv.ValueKind == System.Text.Json.JsonValueKind.String)
                    v = lv.GetString();
                stable = l.TryGetProperty("stable", out var st) && st.ValueKind == System.Text.Json.JsonValueKind.True;
            }
            if (string.IsNullOrEmpty(v)) continue;
            list.Add(new LoaderVersionInfo(v, stable));
        }
        return list;
    }

    public async Task<List<string>> ListVersionsAsync(ModLoaderType type, string mcVersion, CancellationToken ct = default)
        => (await ListVersionInfosAsync(type, mcVersion, ct).ConfigureAwait(false)).Select(v => v.Version).ToList();

    public async Task<ModLoaderInstallResult> InstallAsync(ModLoaderType type, string mcVersion, string? loaderVersion,
        string gameRoot, JavaInstallation java, Action<string>? log = null, CancellationToken ct = default, Action<string>? onStage = null)
    {
        _log.Info("ModLoader", $"Installing {type} for MC {mcVersion} (loader {(loaderVersion ?? "latest")})");
        var result = new ModLoaderInstallResult();
        try
        {
            // ---- Fabric / Quilt: installer-free direct install ----
            // The official JVM installers download their dependencies from
            // maven.fabricmc.net themselves and fail (exit 1) on networks
            // that cannot reach it. Their client profile is a single JSON
            // from the meta API though — we can install it directly and let
            // the standard GameInstaller pipeline (with BMCLAPI fallback)
            // fetch the libraries.
            if (type is ModLoaderType.Fabric or ModLoaderType.Quilt)
            {
                onStage?.Invoke("installer-download");
                var prefix = type == ModLoaderType.Fabric ? "fabric-loader" : "quilt-loader";
                var loaderPrefix = prefix + "-";

                // Legacy instances may hold POISONED values here: a full version
                // id ("fabric-loader-0.20.1-1.21") or a placeholder text
                // ("（自动选择最新）"). Sanitize before building any URL.
                string? versionId = null;
                var loader = loaderVersion;
                if (!string.IsNullOrWhiteSpace(loader))
                {
                    loader = loader.Trim();
                    if (loader.StartsWith(loaderPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        versionId = loader; // a full version id was stored
                        loader = ExtractBareLoader(loader, mcVersion);
                    }
                    else if (loader.Contains("自动") || loader.Contains("暂无") || loader.Contains('（'))
                    {
                        loader = null; // placeholder text → resolve latest
                    }
                }

                if (versionId == null)
                    versionId = $"{prefix}-{loader}-{mcVersion}";

                // Already installed? Reuse the existing profile without refetch.
                var existingJson = Path.Combine(gameRoot, "versions", versionId, versionId + ".json");
                if (File.Exists(existingJson))
                {
                    onStage?.Invoke("installer-run");
                    log?.Invoke($"[ModLoader] {versionId} 已存在，直接复用。");
                    loader ??= ExtractBareLoader(versionId, mcVersion);
                    result.VersionId = versionId;
                    result.LoaderVersion = loader;
                    result.Success = true;
                    return result;
                }

                if (string.IsNullOrEmpty(loader))
                {
                    var loaderMetaUrl = type == ModLoaderType.Fabric
                        ? $"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}"
                        : $"https://meta.quiltmc.org/v3/versions/loader/{mcVersion}";
                    loader = await LatestFromMetaAsync(loaderMetaUrl, ct).ConfigureAwait(false);
                }
                versionId = $"{prefix}-{loader}-{mcVersion}";
                var profileUrl = type == ModLoaderType.Fabric
                    ? $"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}/{loader}/profile/json"
                    : $"https://meta.quiltmc.org/v3/versions/loader/{mcVersion}/{loader}/profile/json";
                log?.Invoke($"[ModLoader] 下载 {type} 客户端版本清单：{profileUrl}");
                var profileCache = Path.Combine(CacheDir, $"profile-{versionId}.json");
                string profileJson;
                try
                {
                    profileJson = await GetStringWithFallbackAsync(profileUrl, ct).ConfigureAwait(false);
                    try { PathEx.EnsureDirectory(CacheDir); File.WriteAllText(profileCache, profileJson); } catch { }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (File.Exists(profileCache))
                    {
                        _log.Warn("ModLoader", $"All routes failed for profile; using cached copy ({profileCache}).");
                        log?.Invoke($"[ModLoader] 所有线路失败，使用本地缓存的版本清单。");
                        profileJson = File.ReadAllText(profileCache);
                    }
                    else throw;
                }

                var dir = Path.Combine(gameRoot, "versions", versionId);
                PathEx.EnsureDirectory(dir);
                await File.WriteAllTextAsync(Path.Combine(dir, versionId + ".json"), profileJson).ConfigureAwait(false);
                onStage?.Invoke("installer-run");
                log?.Invoke($"[ModLoader] {type} {loader} 直装完成：{versionId}");
                result.VersionId = versionId;
                result.LoaderVersion = loader;
                result.Success = true;
                return result;
            }

            // Mark the stage BEFORE resolving (metadata lookups can take a few
            // seconds on slow networks) so the UI reflects what is happening.
            onStage?.Invoke("installer-download");
            var (installerUrl, installerFile, args, versionIdHint) = await ResolveInstallerAsync(type, mcVersion, loaderVersion, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(installerUrl)) throw new InvalidOperationException("Could not resolve installer download URL.");

            PathEx.EnsureDirectory(CacheDir);
            var jarPath = Path.Combine(CacheDir, installerFile);
            try
            {
                await _engine.DownloadAsync(new[]
                {
                    new DownloadEntry { Url = installerUrl, Destination = jarPath, DisplayName = installerFile, Category = "installer" }
                }, null, ct).ConfigureAwait(false);
            }
            catch (DownloadException)
            {
                var mirrorUrl = LauncherConfig.ToBmclapiUrl(installerUrl);
                _log.Warn("ModLoader", $"Installer official route failed; retrying via BMCLAPI: {mirrorUrl}");
                await _engine.DownloadAsync(new[]
                {
                    new DownloadEntry { Url = mirrorUrl, Destination = jarPath, DisplayName = installerFile, Category = "installer" }
                }, null, ct).ConfigureAwait(false);
            }

            var existing = Directory.Exists(Path.Combine(gameRoot, "versions"))
                ? Directory.GetDirectories(Path.Combine(gameRoot, "versions")).Select(Path.GetFileName).Where(n => n != null).Select(n => n!).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            onStage?.Invoke("installer-run");
            await RunJavaAsync(java.JavaExecutable, $"-jar \"{jarPath}\" {args}", gameRoot, log, ct).ConfigureAwait(false);

            // Detect the newly created loader version directory.
            var created = Directory.Exists(Path.Combine(gameRoot, "versions"))
                ? Directory.GetDirectories(Path.Combine(gameRoot, "versions")).Select(Path.GetFileName).Where(n => n != null && !existing.Contains(n)).ToList()
                : new List<string?>();
            var detected = created.FirstOrDefault(n =>
                n!.Contains(type.ToString(), StringComparison.OrdinalIgnoreCase) ||
                (versionIdHint != null && n!.Contains(versionIdHint, StringComparison.OrdinalIgnoreCase)));
            result.VersionId = detected ?? versionIdHint;
            result.Success = !string.IsNullOrEmpty(result.VersionId);
            if (!result.Success) result.Error = "Installer ran but no loader version directory was detected.";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _log.Error("ModLoader", $"{type} install failed: {ex.Message}");
        }
        return result;
    }

    // ---------- resolution ----------
    private async Task<(string Url, string File, string Args, string? VersionIdHint)> ResolveInstallerAsync(ModLoaderType type, string mc, string? loaderVer, CancellationToken ct)
    {
        return type switch
        {
            ModLoaderType.Fabric => await ResolveFabricAsync(mc, loaderVer, ct).ConfigureAwait(false),
            ModLoaderType.Quilt => await ResolveQuiltAsync(mc, loaderVer, ct).ConfigureAwait(false),
            ModLoaderType.NeoForge => await ResolveMavenLoaderAsync(mc, loaderVer, ct, "https://maven.neoforged.net/releases/net/neoforged/neoforge", "neoforge", "neoforge-").ConfigureAwait(false),
            ModLoaderType.Forge => await ResolveMavenLoaderAsync(mc, loaderVer, ct, "https://maven.minecraftforge.net/net/minecraftforge/forge", "forge", "forge-").ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }

    private async Task<(string, string, string, string?)> ResolveFabricAsync(string mc, string? loaderVer, CancellationToken ct)
    {
        var instVer = (await LatestFromMetaAsync("https://meta.fabricmc.net/v2/versions/installer", ct).ConfigureAwait(false));
        var loader = loaderVer ?? (await LatestFromMetaAsync($"https://meta.fabricmc.net/v2/versions/loader/{mc}", ct).ConfigureAwait(false));
        var url = $"https://maven.fabricmc.net/net/fabricmc/fabric-installer/{instVer}/fabric-installer-{instVer}.jar";
        var file = $"fabric-installer-{instVer}.jar";
        var args = $"client -mcversion {mc} -loader {loader} -dir \"{LauncherConfig.Instance.RootDirectory}\"";
        return (url, file, args, $"fabric-loader-{loader}-{mc}");
    }

    private async Task<(string, string, string, string?)> ResolveQuiltAsync(string mc, string? loaderVer, CancellationToken ct)
    {
        var instVer = await LatestFromMetaAsync("https://meta.quiltmc.org/v3/versions/installer", ct).ConfigureAwait(false);
        var loader = loaderVer ?? (await LatestFromMetaAsync($"https://meta.quiltmc.org/v3/versions/loader/{mc}", ct).ConfigureAwait(false));
        var url = $"https://maven.quiltmc.org/repository/release/org/quiltmc/quilt-installer/{instVer}/quilt-installer-{instVer}.jar";
        var file = $"quilt-installer-{instVer}.jar";
        var args = $"install client {mc} {loader} -d \"{LauncherConfig.Instance.RootDirectory}\"";
        return (url, file, args, $"quilt-loader-{loader}-{mc}");
    }

    /// <summary>Resolves NeoForge / Forge installer URLs. When no explicit
    /// loader version is given ("自动选择最新"), the newest real version is
    /// taken from the maven metadata (the old "…-latest" filename 404'd).</summary>
    private async Task<(string, string, string, string?)> ResolveMavenLoaderAsync(
        string mc, string? loaderVer, CancellationToken ct, string mavenBase, string artifact, string hintPrefix)
    {
        var ver = loaderVer;
        if (string.IsNullOrEmpty(ver))
        {
            var type = artifact == "neoforge" ? ModLoaderType.NeoForge : ModLoaderType.Forge;
            var infos = await ListVersionInfosAsync(type, mc, ct).ConfigureAwait(false);
            ver = infos.FirstOrDefault()?.Version
                  ?? throw new InvalidOperationException($"{artifact} 暂无 MC {mc} 的可用版本");
        }
        var url = $"{mavenBase}/{ver}/{artifact}-{ver}-installer.jar";
        var file = $"{artifact}-{ver}-installer.jar";
        var args = $"--installClient \"{LauncherConfig.Instance.RootDirectory}\"";
        return (url, file, args, hintPrefix + ver);
    }

    // ---------- helpers ----------
    private async Task<string> LatestFromMetaAsync(string url, CancellationToken ct)
    {
        var cacheFile = Path.Combine(CacheDir, "meta-" + UrlKey(url) + ".json");
        string json;
        try
        {
            json = await GetStringWithFallbackAsync(url, ct).ConfigureAwait(false);
            try { PathEx.EnsureDirectory(CacheDir); File.WriteAllText(cacheFile, json); } catch { }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (File.Exists(cacheFile))
            {
                _log.Warn("ModLoader", $"All routes failed for {url}; using cached meta ({cacheFile}).");
                json = File.ReadAllText(cacheFile);
            }
            else throw;
        }

        using var doc = JsonDocument.Parse(json);
        string? best = null;
        string? bestNonStable = null;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            // Fabric loader meta nests the version under "loader": {"loader":
            // {"version":"0.20.1","stable":true}, ...} while installer meta has
            // a top-level "version". Accept both shapes.
            string? ver = null;
            bool stable = false;
            if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                {
                    ver = v.GetString();
                    stable = e.TryGetProperty("stable", out var s) && s.ValueKind == JsonValueKind.True;
                }
                else if (e.TryGetProperty("loader", out var l) && l.ValueKind == JsonValueKind.Object)
                {
                    if (l.TryGetProperty("version", out var lv) && lv.ValueKind == JsonValueKind.String)
                        ver = lv.GetString();
                    stable = l.TryGetProperty("stable", out var s) && s.ValueKind == JsonValueKind.True;
                }
            }
            if (string.IsNullOrEmpty(ver)) continue;
            if (stable) { best = ver; break; }
            bestNonStable ??= ver;
        }
        return best ?? bestNonStable
            ?? throw new InvalidOperationException($"No version found at {url}（元数据返回 {doc.RootElement.ValueKind}，请检查网络/代理）");
    }

    private static string UrlKey(string url)
        => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(url)))[..12];

    /// <summary>"fabric-loader-0.20.1-1.21" + mc "1.21" → "0.20.1";
    /// falls back to the full remainder when the MC suffix is absent.</summary>
    private static string ExtractBareLoader(string versionId, string mcVersion)
    {
        var prefix = "fabric-loader-";
        if (versionId.StartsWith("quilt-loader-", StringComparison.OrdinalIgnoreCase))
            prefix = "quilt-loader-";
        var rest = versionId[prefix.Length..];
        var suffix = "-" + mcVersion;
        if (rest.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            rest = rest[..^suffix.Length];
        return rest;
    }

    private async Task<int> RunJavaAsync(string java, string arguments, string workingDir, Action<string>? log, CancellationToken ct)
    {
        PathEx.EnsureDirectory(workingDir);
        // Installer JVMs download their own dependencies (Forge/NeoForge maven
        // etc.) — they know nothing about our proxy/mirror logic, so hand them
        // the proxy explicitly (or let them try the system proxy).
        string proxyArgs;
        var proxyUrl = LauncherConfig.Instance.ProxyUrl;
        if (!string.IsNullOrWhiteSpace(proxyUrl) &&
            Uri.TryCreate(proxyUrl, UriKind.Absolute, out var u) &&
            !string.IsNullOrEmpty(u.Host))
        {
            var port = u.Port > 0 ? u.Port : (string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
            proxyArgs = $"-Dhttp.proxyHost={u.Host} -Dhttp.proxyPort={port} -Dhttps.proxyHost={u.Host} -Dhttps.proxyPort={port} ";
        }
        else
        {
            proxyArgs = "-Djava.net.useSystemProxies=true ";
        }

        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = java,
                Arguments = proxyArgs + arguments,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) { sb.AppendLine(e.Data); log?.Invoke(e.Data); } };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) { sb.AppendLine(e.Data); log?.Invoke(e.Data); } };
        if (!p.Start()) return -1;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        // Overall cap: an installer stuck (e.g. internal downloads hanging on
        // an unreachable maven) must not block the install stage forever.
        using var capCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        capCts.CancelAfter(TimeSpan.FromMinutes(15));
        using (ct.Register(() => { try { if (!p.HasExited) p.Kill(); } catch { } }))
        using (capCts.Token.Register(() => { try { if (!p.HasExited) p.Kill(); } catch { } }))
        {
            try
            {
                await p.WaitForExitAsync(capCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("加载器安装器运行超过 15 分钟，已中止。请检查网络/代理后重试（详见日志页）。");
            }
        }
        if (p.ExitCode != 0)
        {
            var tail = string.Join(" | ", sb.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.Length > 0)
                .TakeLast(6));
            throw new InvalidOperationException($"Installer exited with code {p.ExitCode}. 输出末尾: {tail}");
        }
        return p.ExitCode;
    }
}
