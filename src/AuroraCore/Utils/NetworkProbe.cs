using System.Diagnostics;
using AuroraCore.Services;

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

    /// <summary>Round-110: one row of the explicit network diagnostic shown in
    /// Settings. Timing is a HEAD (or 1-byte GET) round trip — no file body is
    /// ever downloaded just to measure a host.</summary>
    public sealed record HostDiagnostic(string Name, string Host, bool Ok, long? Ms,
        string? Error = null, double? BytesPerSec = null)
    {
        public string DisplayLatency => Ok && Ms.HasValue ? $"{Ms.Value} ms" : "不可达";
        /// <summary>Round-111: measured download throughput, or a reason when
        /// the host cannot serve bytes at all.</summary>
        public string DisplaySpeed => BytesPerSec.HasValue && BytesPerSec.Value > 0
            ? JobProgressPolicy.FormatSpeed(BytesPerSec.Value)
            : Ok ? "测速失败" : "—";
        /// <summary>Bar length in 0..100, relative to the slowest reachable host
        /// in this run. Unreachable hosts get 0 so the bar stays honest.</summary>
        public double BarPercent { get; private set; }
        /// <summary>Ready-to-bind pixel width (max 240) — avoids a converter in XAML.</summary>
        public double BarWidth => Math.Max(0, BarPercent) * 2.4;
        /// <summary>Round-111: separate width for the throughput bar.</summary>
        public double SpeedBarWidth { get; private set; }

        public HostDiagnostic WithBar(double percent) => this with { BarPercent = percent };
        public HostDiagnostic WithSpeedBar(double percent) => this with { SpeedBarWidth = percent * 2.4 };
        public HostDiagnostic WithSpeed(double bytesPerSec) => this with { BytesPerSec = bytesPerSec };
    }

    /// <summary>Normalizes raw measurements to bar widths: the fastest host is
    /// always 100% wide, others scale by fastest/slowest. Failures stay at 0.
    /// Pure function so the UI rule can be unit-tested without network.</summary>
    public static IReadOnlyList<HostDiagnostic> NormalizeBars(IEnumerable<HostDiagnostic> rows)
    {
        var list = rows.ToList();
        var ok = list.Where(r => r.Ok && r.Ms.HasValue && r.Ms.Value > 0).Select(r => r.Ms!.Value).ToList();
        if (ok.Count == 0) return list.Select(r => r.WithBar(0)).ToList();
        var fastest = ok.Min();
        return list.Select(r => !r.Ok || !r.Ms.HasValue || r.Ms.Value <= 0
            ? r.WithBar(0)
            : r.WithBar(Math.Clamp(100.0 * fastest / Math.Max(fastest, r.Ms.Value), 4, 100))).ToList();
    }

    /// <summary>Round-111: normalize throughput to bar widths (fastest = 100).
    /// Pure function, unit-testable without network.</summary>
    public static IReadOnlyList<HostDiagnostic> NormalizeSpeedBars(IEnumerable<HostDiagnostic> rows)
    {
        var list = rows.ToList();
        var speeds = list.Where(r => r.BytesPerSec.HasValue && r.BytesPerSec.Value > 0)
                         .Select(r => r.BytesPerSec!.Value).ToList();
        if (speeds.Count == 0) return list;
        var fastest = speeds.Max();
        return list.Select(r => !r.BytesPerSec.HasValue || r.BytesPerSec.Value <= 0
            ? r.WithSpeedBar(0)
            : r.WithSpeedBar(Math.Clamp(100.0 * r.BytesPerSec.Value / Math.Max(fastest, 1), 4, 100))).ToList();
    }

    /// <summary>Round-110: measures the three hosts that matter to this launcher
    /// (CNB update mirror, GitHub fallback/release host, Mojang version manifest)
    /// in parallel. Unlike <see cref="ProbeCachedAsync"/> this is an explicit
    /// user-triggered diagnostic: it NEVER returns a cached result and never
    /// changes the download route preference.</summary>
    public static async Task<IReadOnlyList<HostDiagnostic>> ProbeDiagnosticsAsync(
        CancellationToken ct = default, int timeoutSec = 5)
    {
        var cnbRepo = Environment.GetEnvironmentVariable("AURORA_CNB_REPO") ?? "AuroraLauncher-2026/AuroraLauncher";
        // Resolve current asset URLs from the signed feed so the throughput
        // sample never points at a stale tag (the feed is ~2.8 KB).
        var (primaryExe, fallbackExe) = await ResolveFeedUrlsAsync(ct).ConfigureAwait(false);
        // Latency targets (light HEAD) and throughput targets (a real file,
        // sampled for at most 2 s / 512 KB) are separate: a releases landing
        // page is fine for ping but says nothing about download speed.
        var hosts = new (string Name, string PingUrl, string SpeedUrl)[]
        {
            ("CNB 更新源", $"https://cnb.cool/{cnbRepo}/-/releases", primaryExe),
            ("GitHub 更新源", "https://github.com/adagarm123/AuroraLauncher/releases", fallbackExe),
            ("Mojang 官方源", LauncherConfig.OfficialVersionManifestUrl,
                LauncherConfig.OfficialVersionManifestUrl)
        };
        var tasks = hosts.Select(async h =>
        {
            var row = await MeasureAsync(h.Name, h.PingUrl, timeoutSec, ct).ConfigureAwait(false);
            if (!row.Ok) return row;
            var speed = await MeasureThroughputAsync(h.SpeedUrl, ct).ConfigureAwait(false);
            return speed.HasValue ? row.WithSpeed(speed.Value) : row;
        }).ToArray();
        var rows = await Task.WhenAll(tasks).ConfigureAwait(false);
        return NormalizeSpeedBars(rows);
    }

    /// <summary>Reads the small signed feed to learn the current EXE URLs on
    /// both hosts. Any failure degrades to version-tagged defaults — diagnostics
    /// must never throw just because the feed host is down.</summary>
    private static async Task<(string Primary, string Fallback)> ResolveFeedUrlsAsync(CancellationToken ct)
    {
        var cnbRepo = Environment.GetEnvironmentVariable("AURORA_CNB_REPO") ?? "AuroraLauncher-2026/AuroraLauncher";
        var tag = "v1.12.24";
        var fallbackPrimary = $"https://cnb.cool/{cnbRepo}/-/releases/download/{tag}/AuroraLauncher.exe";
        var fallbackBackup = $"https://github.com/adagarm123/AuroraLauncher/releases/download/{tag}/AuroraLauncher.exe";
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            using var resp = await HttpClientFactory.Instance.Client.GetAsync(
                "https://adagarm123.github.io/AuroraLauncher/latest.json",
                HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (fallbackPrimary, fallbackBackup);
            using var doc = System.Text.Json.JsonDocument.Parse(
                await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
            var root = doc.RootElement;
            string? url = null, fb = null;
            if (root.TryGetProperty("url", out var u)) url = u.GetString();
            if (root.TryGetProperty("fallbackUrl", out var f)) fb = f.GetString();
            return (string.IsNullOrWhiteSpace(url) ? fallbackPrimary : url!,
                    string.IsNullOrWhiteSpace(fb) ? fallbackBackup : fb!);
        }
        catch { return (fallbackPrimary, fallbackBackup); }
    }

    /// <summary>Downloads at most 512 KB / 2 seconds from one host and derives
    /// bytes-per-second from the bytes actually received. Range requests are
    /// used when available so CDN nodes serve from the start; the sample is
    /// bounded in both time and size, so this never downloads a whole EXE.</summary>
    private static async Task<double?> MeasureThroughputAsync(string url, CancellationToken ct)
    {
        const int maxBytes = 512 * 1024;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, maxBytes - 1);
            using var resp = await HttpClientFactory.Instance.Client.SendAsync(req,
                HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
                return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var buffer = new byte[32 * 1024];
            var sw = Stopwatch.StartNew();
            long total = 0;
            while (total < maxBytes)
            {
                var want = (int)Math.Min(buffer.Length, maxBytes - total);
                var read = await stream.ReadAsync(buffer.AsMemory(0, want), cts.Token).ConfigureAwait(false);
                if (read <= 0) break;
                total += read;
            }
            sw.Stop();
            return sw.Elapsed.TotalSeconds > 0 && total > 0 ? total / sw.Elapsed.TotalSeconds : null;
        }
        catch { return null; }
    }

    private static async Task<HostDiagnostic> MeasureAsync(string name, string url,
        int timeoutSec, CancellationToken ct)
    {
        string host;
        try { host = new Uri(url).Host; }
        catch { host = url; }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            var sw = Stopwatch.StartNew();
            using (var head = new HttpRequestMessage(HttpMethod.Head, url))
            using (var resp = await HttpClientFactory.Instance.Client.SendAsync(head,
                       HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
            {
                if (resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
                {
                    sw.Stop();
                    return new HostDiagnostic(name, host, true, sw.ElapsedMilliseconds);
                }
            }
            // Some hosts reject HEAD; a 1-byte GET is still cheap.
            using var range = new HttpRequestMessage(HttpMethod.Get, url);
            range.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var resp2 = await HttpClientFactory.Instance.Client.SendAsync(range,
                HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            sw.Stop();
            return resp2.IsSuccessStatusCode
                ? new HostDiagnostic(name, host, true, sw.ElapsedMilliseconds)
                : new HostDiagnostic(name, host, false, null, $"HTTP {(int)resp2.StatusCode}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new HostDiagnostic(name, host, false, null,
                ex is OperationCanceledException ? "连接超时" : ex.Message);
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
