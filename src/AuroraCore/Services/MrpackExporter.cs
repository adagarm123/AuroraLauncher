using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuroraCore.Models;
using AuroraCore.Utils;

namespace AuroraCore.Services;

/// <summary>A mod file Modrinth already hosts (so the pack can reference it).</summary>
public sealed record ModrinthFileMatch(
    string ProjectId, string VersionId, string FileName, string Url,
    long Size, string Sha1, string Sha512);

/// <summary>Caller-supplied hash→version lookup (lets tests run offline).</summary>
public delegate Task<IReadOnlyDictionary<string, ModrinthFileMatch>> ModrinthHashLookup(
    IReadOnlyList<string> sha1Hashes, CancellationToken ct);

public sealed class MrpackExportResult
{
    public string OutputPath { get; init; } = "";
    public int ModsFromModrinth { get; init; }
    public int ModsBundled { get; init; }
    public int OverrideFiles { get; init; }
    public long SizeBytes { get; init; }
    public IReadOnlyList<string> Unresolved { get; init; } = Array.Empty<string>();
    public bool HasUnresolved => Unresolved.Count > 0;

    public string Summary =>
        $"已导出 {ModsFromModrinth + ModsBundled} 个模组（Modrinth 直链 {ModsFromModrinth}，" +
        $"打包进 overrides {ModsBundled}）、{OverrideFiles} 个配置文件，共 {JobProgressPolicy.FormatBytes(SizeBytes)}";
}

/// <summary>
/// Round-77: export the current instance as a Modrinth pack (.mrpack).
///
/// Strategy: hash every enabled mod jar, ask Modrinth whether it already hosts
/// that exact file. Hosted mods become <c>files[]</c> entries pointing at
/// Modrinth's CDN (so the pack is tiny and the receiver gets a verified download);
/// everything else — unknown jars and all config — goes into <c>overrides/</c>, so
/// the pack still works without Modrinth.
///
/// What is deliberately NOT exported: <c>saves/</c> (the user's worlds),
/// <c>logs/</c>, and <c>servers.dat</c> (it contains private server addresses).
/// A pack is something you send to a friend, not a backup of your machine.
/// </summary>
public static class MrpackExporter
{
    /// <summary>Folders copied verbatim into overrides/.</summary>
    public static readonly string[] OverrideDirectories = { "config", "defaultconfigs", "kubejs", "scripts" };

    /// <summary>Root files worth carrying (settings), explicitly excluding
    /// servers.dat / saves / logs.</summary>
    public static readonly string[] OverrideRootFiles = { "options.txt", "optionsshaders.txt" };

    /// <summary>A single override file above this is skipped: a pack is meant to
    /// be small, and a 100 MB shader pack inside overrides surprises everyone.</summary>
    public const long MaxOverrideFileBytes = 16L * 1024 * 1024;

    public static string IndexFileName => "modrinth.index.json";

    /// <summary>Builds the pack. <paramref name="lookup"/> may be null to use the
    /// real Modrinth API; tests pass a fake to stay offline.</summary>
    public static async Task<MrpackExportResult> ExportAsync(
        GameInstance instance,
        string outputPath,
        string? packName = null,
        string? summary = null,
        ModrinthHashLookup? lookup = null,
        Action<string>? progress = null,
        CancellationToken ct = default)
    {
        var instanceDir = instance.Directory;
        var modsDir = Path.Combine(instanceDir, "mods");
        var enabledJars = Directory.Exists(modsDir)
            ? Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly)
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();

        progress?.Invoke($"正在计算 {enabledJars.Count} 个模组的校验值…");
        var hashes = new List<(string Path, string Sha1, string Sha512, long Size)>();
        foreach (var jar in enabledJars)
        {
            ct.ThrowIfCancellationRequested();
            var (sha1, sha512) = HashFile(jar);
            hashes.Add((jar, sha1, sha512, new FileInfo(jar).Length));
        }

        var matches = new Dictionary<string, ModrinthFileMatch>(StringComparer.OrdinalIgnoreCase);
        if (hashes.Count > 0)
        {
            progress?.Invoke("正在向 Modrinth 查询已有版本（未收录的会打包进 overrides）…");
            var lookupFn = lookup ?? LookupOnModrinthAsync;
            try
            {
                foreach (var chunk in hashes.Select(h => h.Sha1).Distinct(StringComparer.OrdinalIgnoreCase).Chunk(50))
                {
                    var found = await lookupFn(chunk, ct).ConfigureAwait(false);
                    foreach (var kv in found) matches[kv.Key] = kv.Value;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Export must still work offline: fall back to bundling everything.
                progress?.Invoke("Modrinth 查询失败，改为把所有模组打包进 overrides：" + ex.Message);
            }
        }

        var files = new List<object>();
        var bundled = new List<string>();
        var unresolved = new List<string>();
        foreach (var (path, sha1, sha512, size) in hashes)
        {
            var fileName = Path.GetFileName(path);
            if (matches.TryGetValue(sha1, out var match))
            {
                files.Add(new
                {
                    path = "mods/" + fileName,
                    hashes = new { sha1, sha512 },
                    env = new { client = "required", server = "required" },
                    downloads = new[] { match.Url },
                    fileSize = match.Size > 0 ? match.Size : size
                });
            }
            else
            {
                bundled.Add(path);
                if (matches.Count > 0) unresolved.Add(fileName);
            }
        }

        var index = new
        {
            formatVersion = 1,
            game = "minecraft",
            versionId = "1.0.0",
            name = string.IsNullOrWhiteSpace(packName) ? instance.Name : packName.Trim(),
            summary = string.IsNullOrWhiteSpace(summary)
                ? $"由 AuroraLauncher 从实例「{instance.Name}」导出"
                : summary.Trim(),
            files,
            dependencies = BuildDependencies(instance)
        };

        var overrideFiles = CollectOverrideFiles(instanceDir, ct);
        progress?.Invoke($"正在打包（{overrideFiles.Count} 个配置文件 + {bundled.Count} 个模组）…");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var temp = outputPath + ".part";
        try
        {
            TryDelete(temp);
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                var indexEntry = zip.CreateEntry(IndexFileName, CompressionLevel.Optimal);
                await using (var stream = indexEntry.Open())
                await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    await writer.WriteAsync(JsonSerializer.Serialize(index, JsonOptions)).ConfigureAwait(false);

                foreach (var modPath in bundled)
                    AddFile(zip, modPath, "overrides/mods/" + Path.GetFileName(modPath), ct);

                foreach (var (fullPath, relative) in overrideFiles)
                    AddFile(zip, fullPath, "overrides/" + relative, ct);
            }

            TryDelete(outputPath);
            var size = new FileInfo(temp).Length;
            File.Move(temp, outputPath);
            progress?.Invoke("导出完成");
            return new MrpackExportResult
            {
                OutputPath = outputPath,
                ModsFromModrinth = files.Count,
                ModsBundled = bundled.Count,
                OverrideFiles = overrideFiles.Count,
                SizeBytes = size,
                Unresolved = unresolved
            };
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Loader keys follow the mrpack spec ("fabric-loader", "forge",
    /// "neoforge", "quilt-loader").</summary>
    public static Dictionary<string, string> BuildDependencies(GameInstance instance)
    {
        var deps = new Dictionary<string, string> { ["minecraft"] = instance.MinecraftVersion };
        var version = instance.LoaderVersion;
        if (!string.IsNullOrWhiteSpace(version))
        {
            switch (instance.LoaderType)
            {
                case ModLoaderType.Fabric: deps["fabric-loader"] = version!; break;
                case ModLoaderType.Quilt: deps["quilt-loader"] = version!; break;
                case ModLoaderType.Forge: deps["forge"] = version!; break;
                case ModLoaderType.NeoForge: deps["neoforge"] = version!; break;
            }
        }
        return deps;
    }

    public static (string Sha1, string Sha512) HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        var sha1 = Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
        stream.Position = 0;
        var sha512 = Convert.ToHexString(SHA512.HashData(stream)).ToLowerInvariant();
        return (sha1, sha512);
    }

    /// <summary>Every file that should land in overrides/, as (absolute, relative)
    /// with forward slashes.</summary>
    public static List<(string FullPath, string Relative)> CollectOverrideFiles(string instanceDir, CancellationToken ct = default)
    {
        var result = new List<(string, string)>();
        foreach (var dirName in OverrideDirectories)
        {
            var dir = Path.Combine(instanceDir, dirName);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > MaxOverrideFileBytes) continue;
                    var relative = Path.GetRelativePath(instanceDir, file).Replace('\\', '/');
                    result.Add((file, relative));
                }
                catch { /* unreadable file → skip */ }
            }
        }
        foreach (var rootFile in OverrideRootFiles)
        {
            var path = Path.Combine(instanceDir, rootFile);
            if (!File.Exists(path)) continue;
            try
            {
                if (new FileInfo(path).Length <= MaxOverrideFileBytes) result.Add((path, rootFile));
            }
            catch { }
        }
        return result;
    }

    private static void AddFile(ZipArchive zip, string source, string entryName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            zip.CreateEntryFromFile(source, entryName, CompressionLevel.Optimal);
        }
        catch { /* locked / disappeared → the pack stays valid without it */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ---------------- default Modrinth lookup ----------------

    /// <summary>POST /v2/version_files — returns the version that owns each hash.
    /// Falls back to the MCIM mirror when the official API is unreachable, the
    /// same way content downloads do.</summary>
    public static async Task<IReadOnlyDictionary<string, ModrinthFileMatch>> LookupOnModrinthAsync(
        IReadOnlyList<string> sha1Hashes, CancellationToken ct)
    {
        var result = new Dictionary<string, ModrinthFileMatch>(StringComparer.OrdinalIgnoreCase);
        if (sha1Hashes.Count == 0) return result;

        var body = JsonSerializer.Serialize(new { hashes = sha1Hashes, algorithm = "sha1" });
        var hosts = new[]
        {
            "https://api.modrinth.com/v2/version_files",
            "https://mod.mcimirror.top/modrinth/v2/version_files"
        };

        foreach (var url in hosts)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                using var response = await HttpClientFactory.Instance.Client
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                ParseLookupResponse(json, result);
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger.Instance.Trace("Export", $"Modrinth 查询失败（{url}）：{ex.Message}");
            }
        }
        return result;
    }

    /// <summary>Public so the parser can be tested without a network call.</summary>
    public static void ParseLookupResponse(string json, Dictionary<string, ModrinthFileMatch> into)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var hash = prop.Name;
            var version = prop.Value;
            if (version.ValueKind != JsonValueKind.Object) continue;
            if (!version.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) continue;

            JsonElement? chosen = null;
            foreach (var file in files.EnumerateArray())
            {
                chosen ??= file;
                if (file.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True)
                {
                    chosen = file;
                    break;
                }
            }
            if (chosen == null) continue;

            var file0 = chosen.Value;
            var url = file0.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(url)) continue;

            var sha1 = hash;
            var sha512 = "";
            if (file0.TryGetProperty("hashes", out var hashesEl) && hashesEl.ValueKind == JsonValueKind.Object)
            {
                if (hashesEl.TryGetProperty("sha1", out var s1) && s1.GetString() is { Length: > 0 } v1) sha1 = v1;
                if (hashesEl.TryGetProperty("sha512", out var s5) && s5.GetString() is { Length: > 0 } v5) sha512 = v5;
            }
            into[hash] = new ModrinthFileMatch(
                version.TryGetProperty("project_id", out var pid) ? pid.GetString() ?? "" : "",
                version.TryGetProperty("id", out var vid) ? vid.GetString() ?? "" : "",
                file0.TryGetProperty("filename", out var fn) ? fn.GetString() ?? "" : "",
                url!,
                file0.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var size) ? size : 0,
                sha1,
                sha512);
        }
    }
}
