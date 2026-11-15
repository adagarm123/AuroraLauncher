using AuroraCore.Models;
using AuroraCore.Utils;

namespace AuroraCore.Services;

/// <summary>Fetches the official version manifest and individual version JSON.</summary>
public sealed class VersionManifestService
{
    private readonly Logger _log = Logger.Instance;

    /// <summary>Round-71: true when the last <see cref="GetManifestAsync"/> fell
    /// back to the on-disk copy because every route failed. The UI shows it so a
    /// user understands why the list looks stale instead of seeing an empty page.</summary>
    public bool LastManifestFromCache { get; private set; }

    public async Task<VersionManifest> GetManifestAsync(CancellationToken ct = default)
    {
        LastManifestFromCache = false;
        var url = LauncherConfig.Instance.VersionManifestUrl; // canonical official URL (round-71)
        _log.Info("Manifest", $"Fetching version manifest from {url}");
        // Unified component: hedged routes + BMCLAPI mirror + retry (all
        // external GETs go through NetRouting so timeout/retry/mirror policy
        // lives in exactly one place).
        try
        {
            var json = await NetRouting.GetStringAsync(url, LauncherConfig.ToBmclapiUrl(url), ct,
                perRouteTimeoutSec: 15, logTag: "Manifest").ConfigureAwait(false);
            var manifest = JsonHelper.Deserialize<VersionManifest>(json);
            ManifestCache.Save(LauncherConfig.Instance.RootDirectory, json);
            return manifest;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the user navigated away — not an error, and no fallback
        }
        catch (Exception ex)
        {
            var cached = ManifestCache.TryLoad(LauncherConfig.Instance.RootDirectory);
            if (cached == null) throw;
            LastManifestFromCache = true;
            _log.Warn("Manifest",
                $"版本清单获取失败（{ex.Message}），已改用本地缓存（{cached.Versions.Count} 个版本）；" +
                "安装 / 启动所需的版本 JSON 仍需要网络");
            return cached;
        }
    }

    public async Task<VersionManifestEntry> GetEntryAsync(string versionId, CancellationToken ct = default)
    {
        var manifest = await GetManifestAsync(ct).ConfigureAwait(false);
        var entry = manifest.Versions.FirstOrDefault(v => v.Id == versionId);
        if (entry == null) throw new InvalidOperationException($"Version '{versionId}' not found in manifest.");
        return entry;
    }

    /// <summary>Round-95: local version JSON is authoritative and costs zero
    /// network. A third-party loader id is NEVER in the Mojang manifest —
    /// asking for it produced "Version 'forge-1.20.1-47.4.22' not found in
    /// manifest." after a failed install, which told the user nothing.
    /// The order (local → loader guard → manifest) is locked by a test.</summary>
    public async Task<string> GetVersionJsonRawAsync(string versionId, CancellationToken ct = default)
    {
        var localPath = Path.Combine(LauncherConfig.Instance.VersionsDir, versionId, versionId + ".json");
        if (File.Exists(localPath))
        {
            var local = File.ReadAllText(localPath);
            if (!string.IsNullOrWhiteSpace(local))
            {
                _log.Info("Manifest", $"Using local version JSON for {versionId} (no network)");
                return local;
            }
        }

        if (VersionSourcePolicy.IsThirdPartyLoader(versionId))
            throw new InvalidOperationException(VersionSourcePolicy.LoaderMissingHint(versionId));

        var entry = await GetEntryAsync(versionId, ct).ConfigureAwait(false);
        // Round-71: entry.Url is the OFFICIAL URL. Rewriting it to the mirror here
        // made NetRouting collapse both routes onto the same dead host.
        var url = entry.Url;
        _log.Info("Manifest", $"Fetching version JSON for {versionId} ({entry.Type})");
        return await NetRouting.GetStringAsync(url, LauncherConfig.ToBmclapiUrl(url), ct,
            perRouteTimeoutSec: 15, logTag: "Manifest").ConfigureAwait(false);
    }

    public IReadOnlyList<VersionManifestEntry> FilterVersions(VersionManifest manifest, string typeFilter)
    {
        if (typeFilter == "all") return manifest.Versions;
        return manifest.Versions.Where(v => v.Type == typeFilter).ToList();
    }
}
