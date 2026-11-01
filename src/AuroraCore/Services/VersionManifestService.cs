using AuroraCore.Models;
using AuroraCore.Utils;

namespace AuroraCore.Services;

/// <summary>Fetches the official version manifest and individual version JSON.</summary>
public sealed class VersionManifestService
{
    private readonly Logger _log = Logger.Instance;

    public async Task<VersionManifest> GetManifestAsync(CancellationToken ct = default)
    {
        var url = LauncherConfig.Instance.VersionManifestUrl;
        _log.Info("Manifest", $"Fetching version manifest from {url}");
        // Unified component: hedged routes + BMCLAPI mirror + retry (all
        // external GETs go through NetRouting so timeout/retry/mirror policy
        // lives in exactly one place).
        var json = await NetRouting.GetStringAsync(url, LauncherConfig.ToBmclapiUrl(url), ct,
            perRouteTimeoutSec: 15, logTag: "Manifest").ConfigureAwait(false);
        return JsonHelper.Deserialize<VersionManifest>(json);
    }

    public async Task<VersionManifestEntry> GetEntryAsync(string versionId, CancellationToken ct = default)
    {
        var manifest = await GetManifestAsync(ct).ConfigureAwait(false);
        var entry = manifest.Versions.FirstOrDefault(v => v.Id == versionId);
        if (entry == null) throw new InvalidOperationException($"Version '{versionId}' not found in manifest.");
        return entry;
    }

    public async Task<string> GetVersionJsonRawAsync(string versionId, CancellationToken ct = default)
    {
        var entry = await GetEntryAsync(versionId, ct).ConfigureAwait(false);
        var url = LauncherConfig.Instance.RewriteUrl(entry.Url);
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
