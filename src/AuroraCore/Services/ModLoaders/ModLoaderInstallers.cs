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
            var (installerUrl, installerFile, args, versionIdHint) =
                await ResolveInstallerAsync(type, mcVersion, loaderVersion, gameRoot, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(installerUrl)) throw new InvalidOperationException("Could not resolve installer download URL.");

            PathEx.EnsureDirectory(CacheDir);
            var jarPath = Path.Combine(CacheDir, installerFile);

            // Round-86: an installer jar carries neither a published hash nor a
            // size, so "already complete?" cannot be answered for it at all.
            // Round-87 made that explicit (DownloadIntegrity returns Unverifiable
            // instead of a false "complete"), but we still delete the leftovers
            // ourselves: an installer that failed halfway must never be handed to
            // java just because the file exists.
            foreach (var stale in new[] { jarPath, jarPath + ".part" })
            {
                try
                {
                    if (!File.Exists(stale)) continue;
                    File.Delete(stale);
                    _log.Info("ModLoader", $"清理上次残留的安装器文件：{Path.GetFileName(stale)}");
                }
                catch (Exception ex)
                {
                    _log.Warn("ModLoader", $"无法清理残留安装器 {stale}: {ex.Message}");
                }
            }

            var mirrorUrl = LauncherConfig.ToBmclapiUrl(installerUrl);
            var canUseMirror = LauncherConfig.Instance.RoutePolicy != DownloadPolicy.Official &&
                               !string.Equals(mirrorUrl, installerUrl, StringComparison.OrdinalIgnoreCase);
            try
            {
                log?.Invoke($"[ModLoader] 下载安装器：{installerUrl}");
                await _engine.DownloadAsync(new[]
                {
                    new DownloadEntry { Url = installerUrl, Destination = jarPath, DisplayName = installerFile, Category = "installer" }
                }, null, ct).ConfigureAwait(false);
            }
            catch (DownloadException first)
            {
                if (!canUseMirror)
                    throw new InvalidOperationException(
                        "安装器下载失败，且当前设置为「仅使用官方线路」，不会自动改用镜像：\n" +
                        $"· {installerUrl}\n· 原因：{first.Message}", first);

                _log.Warn("ModLoader", $"Installer route failed; retrying via BMCLAPI: {mirrorUrl}");
                log?.Invoke($"[ModLoader] 官方线路失败，改用镜像重试：{mirrorUrl}");
                try
                {
                    await _engine.DownloadAsync(new[]
                    {
                        new DownloadEntry { Url = mirrorUrl, Destination = jarPath, DisplayName = installerFile, Category = "installer" }
                    }, null, ct).ConfigureAwait(false);
                }
                catch (DownloadException second)
                {
                    // Both gone: the most likely cause is a wrong path, not a
                    // flaky network — say so instead of showing a bare 404.
                    throw new InvalidOperationException(
                        "安装器下载失败（官方与镜像都不可用）：\n" +
                        $"· {installerUrl}\n· {mirrorUrl}\n" +
                        $"常见原因：maven 上不存在这个 {type} 版本。Forge 的路径必须是「MC版本-Forge版本」" +
                        $"（例如 1.20.1-47.4.22），而整合包通常只给 Forge 版本号。\n" +
                        $"原始错误：{second.Message}", second);
                }
            }

            var existing = Directory.Exists(Path.Combine(gameRoot, "versions"))
                ? Directory.GetDirectories(Path.Combine(gameRoot, "versions")).Select(Path.GetFileName).Where(n => n != null).Select(n => n!).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            onStage?.Invoke("installer-run");
            // Round-94: Forge / NeoForge installers hard-require a launcher
            // profile file in the target directory — without it they exit 1 with
            // "There is no Minecraft launcher profile in …, you need to run the
            // launcher first!". Our data root never had one, so EVERY Forge
            // install failed at this exact step.
            var profiles = LauncherProfiles.Ensure(gameRoot);
            if (profiles.Created)
                log?.Invoke($"[ModLoader] 安装器需要 launcher_profiles.json，已自动补上：{profiles.Path}");
            else if (profiles.Repaired)
                log?.Invoke($"[ModLoader] launcher_profiles.json 无法识别（缺少 profiles），已重建（原文件备份为 .bak）：{profiles.Path}");

            // A JVM far newer than the game is a classic installer failure
            // ("UnsupportedClassVersionError"): warn before the wall of output.
            var recommendedMajor = JavaRequirement.RecommendMajor(mcVersion);
            if (java.Major > recommendedMajor + 2)
                log?.Invoke($"[ModLoader] 注意：安装器将使用 Java {java.Major}，而 MC {mcVersion} 通常用 Java {recommendedMajor}。" +
                            "若安装失败，请换用该版本的 Java 再试。");

            // Round-95: must happen BEFORE the installer JVM starts — it is the
            // only chance to put jsr305 / trove / forge-universal on disk with
            // our own mirror-aware downloader. Locked by a guard test.
            await PrefillInstallerLibrariesAsync(jarPath, gameRoot, log, ct).ConfigureAwait(false);

            await RunJavaAsync(java.JavaExecutable, $"-jar \"{jarPath}\" {args}", gameRoot, log, ct).ConfigureAwait(false);

            // Detect the newly created loader version directory.
            var created = Directory.Exists(Path.Combine(gameRoot, "versions"))
                ? Directory.GetDirectories(Path.Combine(gameRoot, "versions")).Select(Path.GetFileName).Where(n => n != null && !existing.Contains(n)).ToList()
                : new List<string?>();
            var detected = created.FirstOrDefault(n =>
                n!.Contains(type.ToString(), StringComparison.OrdinalIgnoreCase) ||
                (versionIdHint != null && n!.Contains(versionIdHint, StringComparison.OrdinalIgnoreCase)));

            string HasProfile(string d) => Path.Combine(gameRoot, "versions", d, d + ".json");
            bool HasUsableProfile(string d)
            {
                var f = HasProfile(d);
                if (!File.Exists(f) || new FileInfo(f).Length <= 128) return false;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(f));
                    return doc.RootElement.TryGetProperty("mainClass", out var mc) &&
                           mc.ValueKind == System.Text.Json.JsonValueKind.String &&
                           !string.IsNullOrWhiteSpace(mc.GetString());
                }
                catch { return false; }
            }

            // Round-96: `created` only contains directories that did NOT exist
            // before this run. When an earlier install already produced the
            // profile (very common for an instance that still holds a stale
            // hint id), we must ADOPT it instead of reporting a false failure.
            var versionsRoot = Path.Combine(gameRoot, "versions");
            var candidate = detected;
            if (string.IsNullOrEmpty(candidate) && Directory.Exists(versionsRoot))
            {
                var dirs = Directory.GetDirectories(versionsRoot)
                    .Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!);
                candidate = LoaderProfileLocator.Pick(dirs, type.ToString(), versionIdHint, mcVersion, HasUsableProfile);
                if (!string.IsNullOrEmpty(candidate))
                {
                    _log.Info("ModLoader", $"复用已安装的加载器版本 {candidate}（旧实例可能存着过时的版本 id）");
                    log?.Invoke($"[ModLoader] 检测到已装好的 {candidate}，直接认领（会更新实例里的版本 id）");
                }
            }

            // Round-95: success means a REAL, usable profile on disk. The URL
            // *hint* ("forge-1.20.1-47.4.22") is never accepted as a result.
            var profileOk = candidate is { Length: > 0 } d && HasUsableProfile(d);
            result.VersionId = profileOk ? candidate : null;
            result.Success = profileOk;
            result.LoaderVersion = result.LoaderVersion ?? loaderVersion;
            if (!profileOk)
                result.Error = $"安装器运行结束，但新建和已有目录里都没有找到可用的加载器版本文件（期望 versions/{versionIdHint}/…json 或同类 profile）。" +
                               (versionIdHint != null
                                   ? $"提示 id「{versionIdHint}」只是从下载地址推导出来的，不能当作已安装版本。"
                                   : "") +
                               "请查看日志页的安装器输出，检查网络/代理后重试。";
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
    private async Task<(string Url, string File, string Args, string? VersionIdHint)> ResolveInstallerAsync(
        ModLoaderType type, string mc, string? loaderVer, string gameRoot, CancellationToken ct)
    {
        return type switch
        {
            ModLoaderType.Fabric => await ResolveFabricAsync(mc, loaderVer, gameRoot, ct).ConfigureAwait(false),
            ModLoaderType.Quilt => await ResolveQuiltAsync(mc, loaderVer, gameRoot, ct).ConfigureAwait(false),
            ModLoaderType.NeoForge => await ResolveMavenLoaderAsync(mc, loaderVer, gameRoot, ct, "https://maven.neoforged.net/releases/net/neoforged/neoforge", "neoforge", "neoforge-").ConfigureAwait(false),
            ModLoaderType.Forge => await ResolveMavenLoaderAsync(mc, loaderVer, gameRoot, ct, "https://maven.minecraftforge.net/net/minecraftforge/forge", "forge", "forge-").ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }

    private async Task<(string, string, string, string?)> ResolveFabricAsync(string mc, string? loaderVer, string gameRoot, CancellationToken ct)
    {
        var instVer = (await LatestFromMetaAsync("https://meta.fabricmc.net/v2/versions/installer", ct).ConfigureAwait(false));
        var loader = loaderVer ?? (await LatestFromMetaAsync($"https://meta.fabricmc.net/v2/versions/loader/{mc}", ct).ConfigureAwait(false));
        var url = $"https://maven.fabricmc.net/net/fabricmc/fabric-installer/{instVer}/fabric-installer-{instVer}.jar";
        var file = $"fabric-installer-{instVer}.jar";
        // Round-94: the target directory comes from the parameter, not the
        // global config — otherwise a caller that installs into another root
        // (tests, portable mode) gets its files written to the wrong place.
        var args = $"client -mcversion {mc} -loader {loader} -dir \"{gameRoot}\"";
        return (url, file, args, $"fabric-loader-{loader}-{mc}");
    }

    private async Task<(string, string, string, string?)> ResolveQuiltAsync(string mc, string? loaderVer, string gameRoot, CancellationToken ct)
    {
        var instVer = await LatestFromMetaAsync("https://meta.quiltmc.org/v3/versions/installer", ct).ConfigureAwait(false);
        var loader = loaderVer ?? (await LatestFromMetaAsync($"https://meta.quiltmc.org/v3/versions/loader/{mc}", ct).ConfigureAwait(false));
        var url = $"https://maven.quiltmc.org/repository/release/org/quiltmc/quilt-installer/{instVer}/quilt-installer-{instVer}.jar";
        var file = $"quilt-installer-{instVer}.jar";
        var args = $"install client {mc} {loader} -d \"{gameRoot}\"";
        return (url, file, args, $"quilt-loader-{loader}-{mc}");
    }

    /// <summary>Resolves NeoForge / Forge installer URLs. When no explicit
    /// loader version is given ("自动选择最新"), the newest real version is
    /// taken from the maven metadata (the old "…-latest" filename 404'd).</summary>
    private async Task<(string, string, string, string?)> ResolveMavenLoaderAsync(
        string mc, string? loaderVer, string gameRoot, CancellationToken ct, string mavenBase, string artifact, string hintPrefix)
    {
        var ver = loaderVer;
        if (string.IsNullOrEmpty(ver))
        {
            var type = artifact == "neoforge" ? ModLoaderType.NeoForge : ModLoaderType.Forge;
            var infos = await ListVersionInfosAsync(type, mc, ct).ConfigureAwait(false);
            ver = infos.FirstOrDefault()?.Version
                  ?? throw new InvalidOperationException($"{artifact} 暂无 MC {mc} 的可用版本");
        }

        // Round-86: Forge's maven path is "{mc}-{forge}" for BOTH the directory
        // and the artifact. A modpack hands us the bare forge version
        // ("47.4.22"), and ".../forge/47.4.22/forge-47.4.22-installer.jar" is a
        // hard 404 on the official host AND on BMCLAPI (verified). NeoForge
        // versions already carry the MC prefix ("21.1.72"), so this rewrite is
        // Forge-only.
        if (artifact == "forge") ver = ForgeMavenVersion(mc, ver);

        var (url, file) = BuildMavenInstallerUrl(mavenBase, artifact, ver);
        var args = $"--installClient \"{gameRoot}\"";
        return (url, file, args, hintPrefix + ver);
    }

    /// <summary>Round-86 pure: normalize whatever we were told into Forge's
    /// "{mc}-{forge}" form. Accepts the bare version a modpack provides
    /// ("47.4.22"), an already-combined one ("1.20.1-47.4.22") and a legacy
    /// full version id ("forge-1.20.1-47.4.22").</summary>
    public static string ForgeMavenVersion(string mc, string version)
    {
        var v = (version ?? "").Trim();
        if (v.StartsWith("forge-", StringComparison.OrdinalIgnoreCase))
            v = v["forge-".Length..];
        if (v.Length == 0) return v;
        if (v.StartsWith(mc + "-", StringComparison.OrdinalIgnoreCase)) return v;
        return $"{mc}-{v}";
    }

    /// <summary>Round-86 pure: the two strings a maven installer download needs.
    /// Both are derived from the SAME version string — the 404 in the bug report
    /// came from a path that said "forge/47.4.22" while the file name also said
    /// "forge-47.4.22".</summary>
    public static (string Url, string File) BuildMavenInstallerUrl(string mavenBase, string artifact, string mavenVersion)
    {
        var file = $"{artifact}-{mavenVersion}-installer.jar";
        return ($"{mavenBase}/{mavenVersion}/{file}", file);
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

    /// <summary>Round-95: pre-fetch the libraries listed inside the installer
    /// jar through our route-aware engine. Every candidate is OPTIONAL
    /// (Required=false), so a dead overseas maven host can never abort the
    /// install before the installer even starts — the installer simply retries
    /// itself for whatever we could not get.</summary>
    private async Task PrefillInstallerLibrariesAsync(string jarPath, string gameRoot,
        Action<string>? log, CancellationToken ct)
    {
        var libs = new List<InstallerLibrary>();
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(jarPath);
            foreach (var name in InstallerDependencies.ProfileFiles)
            {
                var entry = zip.GetEntry(name);
                if (entry == null) continue;
                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                libs.AddRange(InstallerDependencies.Parse(await reader.ReadToEndAsync().ConfigureAwait(false)));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn("ModLoader", $"预填充：无法读取安装器内的依赖清单（{ex.Message}），交给安装器自行下载。");
            return;
        }

        // installer_profile.json and version.json overlap — one artifact per
        // path, preferring the entry that carries a checksum.
        var unique = libs.GroupBy(l => l.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(l => string.IsNullOrEmpty(l.Sha1) ? 0 : 1).First())
            .ToList();
        if (unique.Count == 0) return;

        var libRoot = Path.Combine(gameRoot, "libraries");
        try { Directory.CreateDirectory(libRoot); } catch { /* best effort */ }
        string Dest(InstallerLibrary l) =>
            Path.Combine(libRoot, l.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        var pending = unique.Where(l => !File.Exists(Dest(l))).ToList();
        if (pending.Count == 0)
        {
            _log.Info("ModLoader", $"预填充：{unique.Count} 个依赖已在本地");
            return;
        }

        for (int round = 0; round < 3 && pending.Count > 0; round++)
        {
            var entries = pending
                .Select(l => (Lib: l, Urls: InstallerDependencies.CandidateUrls(l)))
                .Where(x => round < x.Urls.Count)
                .Select(x => new DownloadEntry
                {
                    Url = x.Urls[round],
                    Destination = Dest(x.Lib),
                    ExpectedSha1 = string.IsNullOrWhiteSpace(x.Lib.Sha1) ? null : x.Lib.Sha1,
                    ExpectedSize = x.Lib.Size > 0 ? x.Lib.Size : null,
                    Required = false,
                    DisplayName = Path.GetFileName(x.Lib.RelativePath),
                    Category = "installer-lib"
                }).ToList();
            if (entries.Count == 0) break;
            try { await _engine.DownloadAsync(entries, null, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.Warn("ModLoader", $"预填充第 {round + 1} 轮失败（{ex.Message}），继续交给安装器。");
            }
            pending = pending.Where(l => !File.Exists(Dest(l))).ToList();
        }

        var filled = unique.Count - pending.Count;
        log?.Invoke($"[ModLoader] 预填充安装器依赖：{filled}/{unique.Count} 个已在本地" +
                    (pending.Count > 0 ? $"（还有 {pending.Count} 个没下到，安装器会再试一次）" : ""));
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
            // Round-94: translate the failures we recognise instead of dumping
            // the raw maven noise at the user.
            var explanation = LauncherProfiles.Explain(tail);
            throw new InvalidOperationException(
                $"Installer exited with code {p.ExitCode}." +
                (explanation != null ? $" {explanation}" : "") +
                $" 输出末尾: {tail}");
        }
        return p.ExitCode;
    }
}
