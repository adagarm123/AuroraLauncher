using System.Collections.Concurrent;
using System.IO.Compression;
using AuroraCore.Download;
using AuroraCore.Models;
using AuroraCore.Utils;

namespace AuroraCore.Services;

/// <summary>
/// Mod management: scanning, enable/disable, Modrinth search &amp; download, and
/// .mrpack / CurseForge-style pack import. Mods live in each instance's mods/
/// folder so instances stay isolated.
/// </summary>
public sealed class ModManager
{
    private readonly DownloadEngine _engine;
    private readonly Logger _log = Logger.Instance;

    public ModManager(DownloadEngine engine) => _engine = engine;

    public List<ModInfo> Scan(string instanceDir)
    {
        var modsDir = Path.Combine(instanceDir, "mods");
        var result = new List<ModInfo>();
        if (!Directory.Exists(modsDir)) return result;
        foreach (var file in Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly))
            result.Add(ReadModInfo(file));
        foreach (var file in Directory.EnumerateFiles(modsDir, "*.jar.disabled", SearchOption.TopDirectoryOnly))
            result.Add(ReadModInfo(file, disabled: true));
        return result.OrderBy(m => m.Name).ToList();
    }

    private ModInfo ReadModInfo(string file, bool disabled = false)
    {
        var name = Path.GetFileName(file);
        var info = new ModInfo
        {
            FileName = name,
            FilePath = file,
            Name = name,
            Enabled = !disabled,
            Size = new FileInfo(file).Length
        };
        try
        {
            using var zip = ZipFile.OpenRead(file);
            // Fabric / Quilt
            foreach (var entryName in new[] { "fabric.mod.json", "quilt.mod.json" })
            {
                var e = zip.GetEntry(entryName);
                if (e != null)
                {
                    using var sr = new StreamReader(e.Open());
                    using var doc = JsonDocument.Parse(sr.ReadToEnd());
                    var root = doc.RootElement;
                    info.Loader = entryName.Contains("quilt") ? "quilt" : "fabric";
                    if (root.TryGetProperty("name", out var n)) info.Name = n.GetString()!;
                    if (root.TryGetProperty("version", out var v)) info.Version = v.GetString()!;
                    if (root.TryGetProperty("id", out var id)) info.ModId = id.GetString()!;
                    return info;
                }
            }
            // Forge / NeoForge: META-INF/mods.toml
            var toml = zip.GetEntry("META-INF/mods.toml");
            if (toml != null)
            {
                info.Loader = "forge";
                using var sr = new StreamReader(toml.Open());
                var text = sr.ReadToEnd();
                info.ModId = TOMLFirstValue(text, "modId") ?? info.ModId;
                info.Version = TOMLFirstValue(text, "version") ?? info.Version;
                info.Name = TOMLFirstValue(text, "displayName") ?? info.ModId;
            }
        }
        catch { /* unreadable jar -> keep filename as name */ }
        return info;
    }

    private static string? TOMLFirstValue(string toml, string key)
    {
        // Minimal TOML key = "value" extraction (good enough for mods.toml).
        var pattern = $"{key}\\s*=\\s*\"([^\"]*)\"";
        var m = System.Text.RegularExpressions.Regex.Match(toml, pattern);
        return m.Success ? m.Groups[1].Value : null;
    }

    public void SetEnabled(ModInfo mod, bool enabled)
    {
        var dir = Path.GetDirectoryName(mod.FilePath)!;
        if (enabled)
        {
            if (mod.FilePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            {
                var target = Path.Combine(dir, Path.GetFileNameWithoutExtension(mod.FilePath));
                if (File.Exists(target)) File.Delete(target);
                File.Move(mod.FilePath, target);
                mod.FilePath = target;
            }
            mod.Enabled = true;
        }
        else
        {
            if (!mod.FilePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            {
                var target = mod.FilePath + ".disabled";
                if (File.Exists(target)) File.Delete(target);
                File.Move(mod.FilePath, target);
                mod.FilePath = target;
            }
            mod.Enabled = false;
        }
        _log.Info("Mod", $"{(enabled ? "Enabled" : "Disabled")} mod {mod.Name}");
    }

    /// <summary>Maps official Modrinth URLs onto the MCIM community mirror
    /// (mod.mcimirror.top) used by Chinese launchers when the official
    /// API/CDN is unreachable.</summary>
    internal static string ToMirrorUrl(string url) => url
        .Replace("https://api.modrinth.com", "https://mod.mcimirror.top/modrinth", StringComparison.OrdinalIgnoreCase)
        .Replace("https://cdn.modrinth.com", "https://mod.mcimirror.top", StringComparison.OrdinalIgnoreCase);

    /// <summary>GETs a URL via hedged multi-route (primary proxy / direct /
    /// MCIM mirror fired simultaneously, first success wins) — a dead route no
    /// longer stalls the request.</summary>
    private static Task<string> GetStringWithFallbackAsync(string url, CancellationToken ct)
        => NetRouting.GetStringAsync(url, ModManager.ToMirrorUrl(url), ct, perRouteTimeoutSec: 8, logTag: "Net");

    /// <summary>Downloads entries via the engine; on failure of required files,
    /// retries ONLY the failed destinations through the MCIM mirror.</summary>
    private async Task DownloadWithMirrorRetryAsync(IEnumerable<DownloadEntry> entries, Action<DownloadProgress>? progress, string category, CancellationToken ct)
    {
        var list = entries.ToList();
        try
        {
            await DownloadRouteAsync(list, progress, category, ct).ConfigureAwait(false);
        }
        catch (DownloadException)
        {
            var mirrored = list.Select(e => new DownloadEntry
            {
                Url = ToMirrorUrl(e.Url),
                Destination = e.Destination,
                ExpectedSha1 = e.ExpectedSha1,
                ExpectedSize = e.ExpectedSize,
                Required = e.Required,
                DisplayName = e.DisplayName,
                Category = e.Category
            }).ToList();
            _log.Warn("Content", $"Official route failed for some {category} files; retrying via MCIM mirror...");
            await DownloadRouteAsync(mirrored, progress, category, ct).ConfigureAwait(false);
            _log.Info("Content", $"{category} download succeeded via MCIM mirror.");
        }
    }

    /// <summary>Downloads one route wrapped in the aggregate slow-transfer
    /// watchdog (round-52): a slow-but-alive CDN route (4.9 KB/s from
    /// cdn.modrinth.com) used to crawl forever because failover only fired on
    /// FULL failures — the progress bar looked frozen at 0%. Now, when the
    /// aggregate speed stays below the configured threshold (default 16 KB/s,
    /// round-56: configurable via config.json SlowTransferKBsPerSec; 0 =
    /// disabled — very slow networks can lower it) for ~20s, the route is
    /// aborted and converted into a DownloadException so the mirror retry
    /// takes over; partial .part files are kept, so the mirror route resumes
    /// via Range. Round-56: the abort is also announced on the progress
    /// channel ("官方线路过慢，已切换镜像线路") so the user sees WHY the
    /// speed dipped and recovered instead of guessing.</summary>
    private async Task DownloadRouteAsync(List<DownloadEntry> route, Action<DownloadProgress>? progress, string category, CancellationToken ct)
    {
        var thresholdBytes = Math.Max(0, LauncherConfig.Instance.SlowTransferKBsPerSec) * 1024.0;
        using var slowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watcher = new SlowTransferWatcher(minBytesPerSec: thresholdBytes);
        var stalled = false;

        void Watch(DownloadProgress p)
        {
            if (stalled || thresholdBytes <= 0 || !watcher.OnSample(p.SpeedBytesPerSec)) return;
            stalled = true;
            _log.Warn("Content", $"{category}: 线路被判定为爬行并中止（聚合速度 {watcher.LastWindowAverage / 1024.0:0.0} KB/s，" +
                                 $"阈值 {thresholdBytes / 1024.0:0} KB/s 持续约 20 秒）——切换线路。");
            p.Message = "当前下载线路过慢，已自动切换到更快的线路...";
            progress?.Invoke(p);
            slowCts.Cancel();
        }

        Action<DownloadProgress>? wrapped = progress == null
            ? Watch
            : p => { Watch(p); progress(p); };

        try
        {
            await _engine.DownloadAsync(route, wrapped, slowCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && stalled)
        {
            // Watchdog abort (NOT a user cancel): surface as a download
            // failure so the caller's mirror failover takes over.
            throw new DownloadException($"{category} transfer aborted by slow-transfer watchdog");
        }
    }

    // ---------- Modrinth content (mods / modpacks / datapacks / shaders / resource packs) ----------
    public async Task<ModrinthSearchResult> SearchAsync(string query, ContentType type, string? mcVersion, string? loader,
        string index = "relevance", int pageSize = 20, int page = 0, string? category = null, CancellationToken ct = default)
    {
        var url = $"https://api.modrinth.com/v2/search?limit={Math.Clamp(pageSize, 1, 100)}&offset={Math.Max(0, page) * pageSize}" +
                  "&index=" + Uri.EscapeDataString(index) +
                  "&query=" + Uri.EscapeDataString(query ?? "") +
                  "&facets=" + Uri.EscapeDataString(BuildFacets(type, mcVersion, loader, category));
        var json = await GetStringWithFallbackAsync(url, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var list = new List<ModrinthProject>();
        foreach (var h in doc.RootElement.GetProperty("hits").EnumerateArray())
        {
            list.Add(new ModrinthProject
            {
                ProjectId = h.GetProperty("project_id").GetString()!,
                Slug = h.TryGetProperty("slug", out var s) ? s.GetString()! : "",
                Title = h.GetProperty("title").GetString()!,
                Description = h.TryGetProperty("description", out var d) ? d.GetString()! : "",
                IconUrl = h.TryGetProperty("icon_url", out var i) ? i.GetString()! : "",
                ProjectType = h.TryGetProperty("project_type", out var pt) ? pt.GetString()! : "",
                Downloads = h.TryGetProperty("downloads", out var dl) && dl.ValueKind == JsonValueKind.Number ? dl.GetInt64() : 0,
                DateModified = h.TryGetProperty("date_modified", out var dm) && DateTime.TryParse(dm.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var dt) ? dt : null,
                Categories = h.TryGetProperty("display_categories", out var dc) && dc.ValueKind == JsonValueKind.Array
                    ? dc.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s != "").ToList() : new List<string>()
            });
        }
        long total = doc.RootElement.TryGetProperty("total_hits", out var th) && th.ValueKind == JsonValueKind.Number ? th.GetInt64() : list.Count;
        return new ModrinthSearchResult(list, total);
    }

    public async Task<List<ModrinthProject>> SearchAsync(string query, string mcVersion, string loader, CancellationToken ct = default)
        => (await SearchAsync(query, ContentType.Mod, mcVersion, loader, ct: ct).ConfigureAwait(false)).Projects;

    /// <summary>Builds a Modrinth search facet JSON. Pure function (unit tested).</summary>
    public static string BuildFacets(ContentType type, string? mcVersion, string? loader, string? category = null)
    {
        var parts = new List<string> { $"[\"project_type:{ProjectTypeFacet(type)}\"]" };
        if (!string.IsNullOrEmpty(mcVersion)) parts.Add($"[\"versions:{mcVersion}\"]");
        if (type == ContentType.Mod && !string.IsNullOrEmpty(loader)) parts.Add($"[\"categories:{loader}\"]");
        if (!string.IsNullOrEmpty(category) && category != "全部") parts.Add($"[\"categories:{category}\"]");
        return "[" + string.Join(",", parts) + "]";
    }

    private static readonly ConcurrentDictionary<ContentType, List<string>> _categoryCache = new();

    /// <summary>Fetches Modrinth's tag/category list for a project type (cached).</summary>
    public async Task<List<string>> GetCategoriesAsync(ContentType type, CancellationToken ct = default)
    {
        if (_categoryCache.TryGetValue(type, out var cached)) return cached;
        var json = await GetStringWithFallbackAsync("https://api.modrinth.com/v2/tag/category", ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var facet = ProjectTypeFacet(type);
        var names = new List<string>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var pt = e.TryGetProperty("project_type", out var p) ? p.GetString() : null;
            var n = e.TryGetProperty("name", out var nm) ? nm.GetString() : null;
            if (pt == facet && !string.IsNullOrEmpty(n)) names.Add(n);
        }
        _categoryCache[type] = names;
        return names;
    }

    /// <summary>Fetches full project info (adds the long description body).</summary>
    public async Task<ModrinthProject> GetProjectDetailAsync(string projectId, CancellationToken ct = default)
    {
        var json = await GetStringWithFallbackAsync($"https://api.modrinth.com/v2/project/{projectId}", ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return new ModrinthProject
        {
            ProjectId = r.GetProperty("id").GetString() ?? projectId,
            Slug = r.TryGetProperty("slug", out var s) ? s.GetString() ?? "" : "",
            Title = r.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
            Description = r.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
            IconUrl = r.TryGetProperty("icon_url", out var i) ? i.GetString() ?? "" : "",
            ProjectType = r.TryGetProperty("project_type", out var pt) ? pt.GetString() ?? "" : "",
            Downloads = r.TryGetProperty("downloads", out var dl) && dl.ValueKind == JsonValueKind.Number ? dl.GetInt64() : 0,
            Body = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : ""
        };
    }

    public static string ProjectTypeFacet(ContentType t) => t switch
    {
        ContentType.Mod => "mod",
        ContentType.Modpack => "modpack",
        ContentType.Datapack => "datapack",
        ContentType.Shader => "shader",
        _ => "resourcepack"
    };

    /// <summary>Lists a project's versions that match the given MC version
    /// (and loader for mods), newest first. mcVersion/loader may be null to
    /// skip that filter.</summary>
    public async Task<List<ModrinthVersion>> GetVersionsAsync(string projectId, ContentType type, string? mcVersion, string? loader, CancellationToken ct = default)
    {
        var json = await GetStringWithFallbackAsync($"https://api.modrinth.com/v2/project/{projectId}/version", ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var result = new List<ModrinthVersion>();
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            var gameVersions = v.GetProperty("game_versions").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
            var loaders = v.GetProperty("loaders").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
            if (mcVersion != null && !gameVersions.Contains(mcVersion)) continue;
            if (type == ContentType.Mod && loader != null && !loaders.Contains(loader.ToLowerInvariant())) continue;

            string? url = null, name = null; long size = 0; string? sha1 = null;
            foreach (var f in v.GetProperty("files").EnumerateArray())
            {
                var primary = f.TryGetProperty("primary", out var p) && p.GetBoolean();
                var u = f.GetProperty("url").GetString()!;
                var n = f.GetProperty("filename").GetString()!;
                var s = f.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0;
                string? h = f.TryGetProperty("hashes", out var hs) && hs.TryGetProperty("sha1", out var s1) ? s1.GetString() : null;
                if (primary) { url = u; name = n; size = s; sha1 = h; break; }
                url ??= u; name ??= n; size = size == 0 ? s : size; sha1 ??= h;
            }
            if (url == null || name == null) continue;
            result.Add(new ModrinthVersion(
                v.GetProperty("id").GetString() ?? "",
                v.TryGetProperty("version_number", out var vn) ? vn.GetString()! : "",
                v.TryGetProperty("name", out var nm) ? nm.GetString()! : "",
                v.TryGetProperty("version_type", out var vt) ? vt.GetString()! : "",
                gameVersions, loaders, url, name, size, sha1,
                v.TryGetProperty("date_published", out var dp) && DateTime.TryParse(dp.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var pdt) ? pdt : null));
        }
        return result;
    }

    /// <summary>Downloads a content file into the correct per-instance folder
    /// (mods/, shaderpacks/, resourcepacks/, or a world's datapacks/ folder).</summary>
    public async Task<string> DownloadContentAsync(ModrinthProject project, ModrinthVersion version, ContentType type,
        string instanceDir, string? worldName = null, Action<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        string relFolder = type switch
        {
            ContentType.Mod => "mods",
            ContentType.Shader => "shaderpacks",
            ContentType.ResourcePack => "resourcepacks",
            ContentType.Datapack => string.IsNullOrEmpty(worldName)
                ? "datapacks"
                : Path.Combine("saves", worldName, "datapacks"),
            _ => throw new NotSupportedException("Modpacks must be installed via InstallMrpackAsync.")
        };
        var dest = Path.Combine(instanceDir, relFolder, version.FileName);
        PathEx.EnsureDirectory(Path.GetDirectoryName(dest)!);
        await DownloadWithMirrorRetryAsync(new[]
        {
            new DownloadEntry { Url = version.FileUrl, Destination = dest, ExpectedSha1 = version.Sha1,
                                DisplayName = version.FileName, Category = ProjectTypeFacet(type) }
        }, progress, ProjectTypeFacet(type), ct).ConfigureAwait(false);
        _log.Info("Content", $"Installed {ProjectTypeFacet(type)} '{project.Title}' ({version.VersionNumber}) -> {dest}");
        return dest;
    }

    public Task DownloadMrpackAsync(ModrinthProject project, ModrinthVersion version, string destPath,
        Action<DownloadProgress>? progress = null, CancellationToken ct = default)
        => DownloadWithMirrorRetryAsync(new[]
        {
            new DownloadEntry { Url = version.FileUrl, Destination = destPath, ExpectedSha1 = version.Sha1,
                                DisplayName = version.FileName, Category = "modpack" }
        }, progress, "modpack", ct);

    /// <summary>Parses modrinth.index.json from a .mrpack to learn the target
    /// MC version and mod loader (so a matching instance can be created).</summary>
    public static MrpackInfo ReadMrpackInfo(string mrpackPath)
    {
        using var zip = ZipFile.OpenRead(mrpackPath);
        var entry = zip.GetEntry("modrinth.index.json")
                    ?? throw new InvalidOperationException("Not a valid .mrpack (modrinth.index.json missing).");
        using var sr = new StreamReader(entry.Open());
        using var doc = JsonDocument.Parse(sr.ReadToEnd());
        var root = doc.RootElement;
        string mc = "";
        var loader = ModLoaderType.Vanilla;
        string? loaderVer = null;
        if (root.TryGetProperty("dependencies", out var deps))
        {
            foreach (var prop in deps.EnumerateObject())
            {
                switch (prop.Name.ToLowerInvariant())
                {
                    case "minecraft": mc = prop.Value.GetString() ?? ""; break;
                    case "fabric-loader": loader = ModLoaderType.Fabric; loaderVer = prop.Value.GetString(); break;
                    case "quilt-loader": loader = ModLoaderType.Quilt; loaderVer = prop.Value.GetString(); break;
                    case "forge": loader = ModLoaderType.Forge; loaderVer = prop.Value.GetString(); break;
                    case "neoforge": loader = ModLoaderType.NeoForge; loaderVer = prop.Value.GetString(); break;
                }
            }
        }
        int files = root.TryGetProperty("files", out var fl) && fl.ValueKind == JsonValueKind.Array ? fl.GetArrayLength() : 0;
        return new MrpackInfo(mc, loader, loaderVer, files);
    }

    /// <summary>Installs a .mrpack into an existing instance: extracts overrides/
    /// and downloads every client file declared in modrinth.index.json
    /// (with SHA-1 verification via the download engine).</summary>
    public async Task InstallMrpackAsync(string mrpackPath, string instanceDir,
        Action<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        List<DownloadEntry> entries = new();
        using (var zip = ZipFile.OpenRead(mrpackPath))
        {
            ExtractOverrides(zip, instanceDir);
            var index = zip.GetEntry("modrinth.index.json");
            if (index != null)
            {
                using var sr = new StreamReader(index.Open());
                using var doc = JsonDocument.Parse(sr.ReadToEnd());
                if (doc.RootElement.TryGetProperty("files", out var files))
                {
                    foreach (var f in files.EnumerateArray())
                    {
                        // Respect the pack's client environment: skip files that
                        // explicitly declare client=unsupported.
                        if (f.TryGetProperty("env", out var env) &&
                            env.TryGetProperty("client", out var client) &&
                            client.GetString() == "unsupported") continue;

                        var rel = SafeRelPath(f.GetProperty("path").GetString() ?? "");
                        if (rel == null) continue;

                        var url = f.GetProperty("downloads").EnumerateArray().FirstOrDefault().GetString();
                        if (url == null) continue;

                        var sha1 = f.TryGetProperty("hashes", out var hs) && hs.TryGetProperty("sha1", out var s1) ? s1.GetString() : null;
                        var size = f.TryGetProperty("fileSize", out var fsz) && fsz.ValueKind == JsonValueKind.Number ? fsz.GetInt64() : 0;
                        var dest = Path.Combine(instanceDir, rel);
                        PathEx.EnsureDirectory(Path.GetDirectoryName(dest)!);
                        entries.Add(new DownloadEntry
                        {
                            Url = url, Destination = dest, ExpectedSha1 = sha1,
                            ExpectedSize = size > 0 ? size : null,
                            DisplayName = rel, Category = "modpack-file"
                        });
                    }
                }
            }
        }
        if (entries.Count > 0)
            await _engine.DownloadAsync(entries, progress, ct).ConfigureAwait(false);
        _log.Info("Content", $"Installed modpack '{Path.GetFileName(mrpackPath)}' into instance ({entries.Count} files).");
    }

    private static string? SafeRelPath(string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return null;
        var parts = rel.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        if (parts.Any(p => p == ".." || p == ".")) return null;
        return Path.Combine(parts);
    }

    /// <summary>Lists world names under &lt;instance&gt;/saves (used to place datapacks).</summary>
    public List<string> ListWorlds(string instanceDir)
    {
        var saves = Path.Combine(instanceDir, "saves");
        if (!Directory.Exists(saves)) return new List<string>();
        return Directory.GetDirectories(saves)
            .Select(Path.GetFileName).Where(n => n != null).Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<bool> DownloadAndInstallAsync(ModrinthProject project, string mcVersion, string loader, string instanceDir, CancellationToken ct = default)
    {
        var versions = await GetVersionsAsync(project.ProjectId, ContentType.Mod, mcVersion, loader, ct).ConfigureAwait(false);
        if (versions.Count == 0) return false;
        await DownloadContentAsync(project, versions[0], ContentType.Mod, instanceDir, null, progress: null, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Imports a .mrpack (Modrinth pack): extracts the overrides/ folder
    /// into the instance directory.</summary>
    public void ImportMrpack(string mrpackPath, string instanceDir)
    {
        using var zip = ZipFile.OpenRead(mrpackPath);
        ExtractOverrides(zip, instanceDir);
        _log.Info("Mod", $"Imported mrpack {Path.GetFileName(mrpackPath)} into instance.");
    }

    private static void ExtractOverrides(ZipArchive zip, string instanceDir)
    {
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase)) continue;
            var rel = entry.FullName["overrides/".Length..];
            var safe = SafeRelPath(rel);
            if (safe == null) continue;
            var dest = Path.Combine(instanceDir, safe);
            if (entry.FullName.EndsWith("/")) { PathEx.EnsureDirectory(dest); continue; }
            PathEx.EnsureDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, true);
        }
    }
}

/// <summary>Kind of downloadable content the UI can search/install.</summary>
public enum ContentType { Mod, Modpack, Datapack, Shader, ResourcePack }

/// <summary>A page of Modrinth search results.</summary>
public sealed record ModrinthSearchResult(List<ModrinthProject> Projects, long TotalHits);

/// <summary>A concrete downloadable version of a Modrinth project.</summary>
public sealed record ModrinthVersion(
    string Id, string VersionNumber, string Name, string VersionType,
    IReadOnlyList<string> GameVersions, IReadOnlyList<string> Loaders,
    string FileUrl, string FileName, long FileSize, string? Sha1, DateTime? DatePublished = null);

/// <summary>Metadata parsed from a .mrpack's modrinth.index.json.</summary>
public sealed record MrpackInfo(string GameVersion, ModLoaderType Loader, string? LoaderVersion, int ClientFiles);

public class ModrinthProject
{
    public string ProjectId { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string IconUrl { get; set; } = "";
    public string ProjectType { get; set; } = "";
    public long Downloads { get; set; }
    public DateTime? DateModified { get; set; }
    public List<string> Categories { get; set; } = new();
    public string Body { get; set; } = "";

    public string DownloadsText => Downloads >= 100_000_000 ? $"{Downloads / 1e8:0.##} 亿"
        : Downloads >= 10_000 ? $"{Downloads / 1e4:0.#} 万"
        : Downloads.ToString("0");

    public string UpdatedText => DateModified == null ? "" : HumanizeAge(DateTime.UtcNow - DateModified.Value);

    public string CategoriesText => string.Join(" · ", Categories.Take(3));

    private static string HumanizeAge(TimeSpan age) =>
        age.TotalDays >= 365 ? $"{(int)(age.TotalDays / 365)} 年前"
        : age.TotalDays >= 30 ? $"{(int)(age.TotalDays / 30)} 个月前"
        : age.TotalDays >= 1 ? $"{(int)age.TotalDays} 天前"
        : age.TotalHours >= 1 ? $"{(int)age.TotalHours} 小时前"
        : "刚刚";
}
