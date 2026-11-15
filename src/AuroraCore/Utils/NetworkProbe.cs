using System.Diagnostics;

namespace AuroraCore.Utils;

/// <summary>Network-environment probe: measures official vs mirror latency and
/// records which one to prefer. In CN networks the official route is usually
/// blackholed; without the probe the user waits a full timeout round before
/// anything appears.
///
/// Round-85 changes, driven by three failure modes we hit:
/// • The probe used GET. Even with ResponseHeadersRead a 200 could stream; a
///   HEAD (or a 1-byte range GET when the host refuses HEAD) costs nothing.
/// • Every probe was a real round trip on the UI's path. Results are now cached
///   for 5 minutes (`ProbeCachedAsync`), and the download path only ever reads
///   `Preference` — a static field, no IO, no awaiting.
/// • The decision rule lived here AND in the VM. It now lives in
///   <see cref="DownloadRoutePolicy"/> so the policy and the UI cannot disagree.</summary>
public static class NetworkProbe
{
    /// <summary>How long a measurement stays valid: long enough to cover a
    /// browsing session, short enough to notice "my VPN came up".</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public sealed record ProbeResult(bool OfficialOk, long? OfficialMs, bool MirrorOk, long? MirrorMs)
    {
        public RoutePreference Decide(RoutePreference fallback)
            => DownloadRoutePolicy.Decide(OfficialLatency, MirrorLatency, fallback);

        public long? OfficialLatency => OfficialOk ? OfficialMs : null;
        public long? MirrorLatency => MirrorOk ? MirrorMs : null;

        public override string ToString()
        {
            var off = OfficialOk ? $"{OfficialMs}ms" : "不可达";
            var mir = MirrorOk ? $"{MirrorMs}ms" : "不可达";
            return $"官方 {off} / BMCLAPI {mir}";
        }
    }

    private static readonly object Gate = new();
    private static ProbeResult? _latest;
    private static DateTime _latestAt = DateTime.MinValue;
    private static Task<ProbeResult?>? _inFlight;

    /// <summary>Last measurement of this run (null = never probed).</summary>
    public static ProbeResult? Latest
    {
        get { lock (Gate) return _latest; }
    }

    public static bool HasFreshResult
    {
        get { lock (Gate) return _latest != null && DateTime.UtcNow - _latestAt < CacheTtl; }
    }

    /// <summary>The route the download path should try first. Safe to read from
    /// any thread: it never awaits and never touches the network.</summary>
    public static RoutePreference Preference { get; private set; } = RoutePreference.Official;

    private static void Publish(ProbeResult result)
    {
        lock (Gate)
        {
            _latest = result;
            _latestAt = DateTime.UtcNow;
        }
        Preference = result.Decide(Preference);
        Logger.Instance.Info("Net", $"Route preference → {Preference}（{result}）");
    }

    /// <summary>Cached probe: returns the last result while it is younger than
    /// <see cref="CacheTtl"/>, otherwise measures once. Concurrent callers share
    /// ONE in-flight probe — several pages ask for this at startup, and firing
    /// five probes at once looks like a flood to both hosts.</summary>
    public static async Task<ProbeResult?> ProbeCachedAsync(CancellationToken ct = default)
    {
        Task<ProbeResult?>? flight;
        lock (Gate)
        {
            if (_latest != null && DateTime.UtcNow - _latestAt < CacheTtl) return _latest;
            _inFlight ??= ProbeOnceAsync(ct);
            flight = _inFlight;
        }
        try
        {
            return await flight.ConfigureAwait(false);
        }
        finally
        {
            lock (Gate) _inFlight = null;
        }
    }

    private static async Task<ProbeResult?> ProbeOnceAsync(CancellationToken ct)
    {
        try
        {
            var result = await ProbeAsync(ct).ConfigureAwait(false);
            Publish(result);
            return result;
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn("Net", "网络测速失败（不影响下载，继续用当前线路）: " + ex.Message);
            return null;
        }
    }

    /// <summary>Probes both routes in parallel. HEAD first; if the host refuses
    /// HEAD, fall back to a GET that asks for exactly one byte — never a full
    /// download.</summary>
    public static async Task<ProbeResult> ProbeAsync(CancellationToken ct = default,
        int perProbeTimeoutSec = 4, string? mirrorUrl = null)
    {
        var cfg = LauncherConfig.Instance;
        var official = cfg.VersionManifestUrl;
        mirrorUrl ??= LauncherConfig.ToBmclapiUrl(official);
        var http = HttpClientFactory.Instance;

        static async Task<(bool Ok, long? Ms)> Timed(HttpClient client, string url,
            int timeoutSec, CancellationToken outer)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
                var sw = Stopwatch.StartNew();
                using (var head = new HttpRequestMessage(HttpMethod.Head, url))
                using (var resp = await client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                           .ConfigureAwait(false))
                {
                    if (resp.IsSuccessStatusCode)
                    {
                        sw.Stop();
                        return (true, sw.ElapsedMilliseconds);
                    }
                }

                // 405 / 501 / 403: some CDNs do not implement HEAD. Ask for one
                // byte instead of the whole file.
                using var range = new HttpRequestMessage(HttpMethod.Get, url);
                range.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                using var resp2 = await client.SendAsync(range, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
                sw.Stop();
                return (resp2.IsSuccessStatusCode, sw.ElapsedMilliseconds);
            }
            catch
            {
                return (false, null);
            }
        }

        var offTask = Timed(http.Client, official, perProbeTimeoutSec, ct);
        var mirTask = Timed(http.Client, mirrorUrl, perProbeTimeoutSec, ct);
        await Task.WhenAll(offTask, mirTask).ConfigureAwait(false);
        var (offOk, offMs) = await offTask.ConfigureAwait(false);
        var (mirOk, mirMs) = await mirTask.ConfigureAwait(false);
        return new ProbeResult(offOk, offMs, mirOk, mirMs);
    }
}
