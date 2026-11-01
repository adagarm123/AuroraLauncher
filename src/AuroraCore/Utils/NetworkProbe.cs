using System.Diagnostics;

namespace AuroraCore.Utils;

/// <summary>Startup network-environment probe: measures official vs mirror
/// latency with tiny HEAD-style requests and DECIDES the default download
/// source from data — not guesses. In CN networks the official route is
/// usually blackholed; without the probe the user waits a full timeout
/// round before seeing mirror results.</summary>
public static class NetworkProbe
{
    public sealed record ProbeResult(bool OfficialOk, long? OfficialMs, bool MirrorOk, long? MirrorMs)
    {
        /// <summary>Decision rule (pure): prefer the route that actually
        /// works; prefer the faster one when both work; keep the user's
        /// explicit choice only when both are equally dead.</summary>
        public DownloadSource Decide(DownloadSource current)
        {
            if (OfficialOk && MirrorOk)
                return (OfficialMs ?? long.MaxValue) <= (MirrorMs ?? long.MaxValue)
                    ? DownloadSource.Official : DownloadSource.BMCLAPI;
            if (MirrorOk && !OfficialOk) return DownloadSource.BMCLAPI;
            if (OfficialOk && !MirrorOk) return DownloadSource.Official;
            return current; // both dead: keep whatever the user had
        }

        public override string ToString()
        {
            var off = OfficialOk ? $"{OfficialMs}ms" : "不可达";
            var mir = MirrorOk ? $"{MirrorMs}ms" : "不可达";
            return $"官方 {off} / BMCLAPI {mir}";
        }
    }

    /// <summary>Probes both routes in parallel. Each probe = GET with a tiny
    /// timeout (headers only matter; the manifest URL is small).</summary>
    public static async Task<ProbeResult> ProbeAsync(CancellationToken ct = default,
        int perProbeTimeoutSec = 4, string? mirrorUrl = null)
    {
        var cfg = LauncherConfig.Instance;
        var official = cfg.VersionManifestUrl;
        mirrorUrl ??= LauncherConfig.ToBmclapiUrl(official);
        var f = HttpClientFactory.Instance;

        async Task<(bool Ok, long? Ms)> Timed(HttpClient client, string url)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(perProbeTimeoutSec));
                var sw = Stopwatch.StartNew();
                using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
                sw.Stop();
                return (resp.IsSuccessStatusCode, sw.ElapsedMilliseconds);
            }
            catch { return (false, null); }
        }

        var offTask = Timed(f.Client, official);
        var mirTask = Timed(f.Client, mirrorUrl);
        await Task.WhenAll(offTask, mirTask).ConfigureAwait(false);
        var (offOk, offMs) = offTask.Result;
        var (mirOk, mirMs) = mirTask.Result;
        return new ProbeResult(offOk, offMs, mirOk, mirMs);
    }
}
