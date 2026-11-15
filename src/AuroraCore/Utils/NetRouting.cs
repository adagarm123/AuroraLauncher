using AuroraCore.Download;

namespace AuroraCore.Utils;

/// <summary>Hedged multi-route HTTP GET: fires all routes (primary proxy /
/// direct / mirror × both channels) simultaneously and takes the first success.
/// Eliminates the "stuck for tens of seconds" effect of sequential fallbacks
/// when earlier routes are silently blackholed by the network.</summary>
public static class NetRouting
{
    /// <summary>Round-71: pure route builder, exposed for tests.
    ///
    /// The rule that broke in round 70: the route SET must never depend on the
    /// configured source in a way that can collapse it. A URL that is already a
    /// mirror URL used to produce mirror-derived mirror (same string),
    /// deduplicated to two dead routes, and the official host was never tried.
    ///
    /// Round-85: the user may now FORCE a single route (Official / Mirror), so
    /// the set DOES depend on the policy — but normalization still happens first,
    /// and Auto always keeps both routes. Only core files honour a forced route
    /// strictly (see <see cref="DownloadRoutePolicy.BuildHosts"/>).</summary>
    public static List<(string Url, string Kind)> BuildHosts(string url, string? mirrorUrl,
        DownloadPolicy policy, RoutePreference preference, bool allowFallback)
        => DownloadRoutePolicy.BuildHosts(url, mirrorUrl, policy, preference, allowFallback);

    /// <summary>Auto-mode overload kept for tests / callers that only have a
    /// boolean preference: both routes are always present, order follows it.</summary>
    public static List<(string Url, string Kind)> BuildHosts(string url, string? mirrorUrl, bool preferMirror)
        => DownloadRoutePolicy.BuildHosts(url, mirrorUrl, DownloadPolicy.Auto,
            preferMirror ? RoutePreference.Mirror : RoutePreference.Official, allowFallback: true);

    /// <summary>Fetches a string. <paramref name="mirrorUrl"/> may be null (no
    /// mirror route). Each route has its own timeout; the whole hedge is
    /// retried once, and the aggregated per-route errors are reported.</summary>
    public static async Task<string> GetStringAsync(string url, string? mirrorUrl, CancellationToken ct,
        int perRouteTimeoutSec = 15, int rounds = 2, string logTag = "Net")
    {
        var f = HttpClientFactory.Instance;
        var cfg = LauncherConfig.Instance;
        // Round-85: policy from settings, order from the (cached) probe, and a
        // forced route is honoured strictly for core files only — a missing
        // texture must never turn into "the launcher is broken".
        var hosts = BuildHosts(url, mirrorUrl, cfg.RoutePolicy, NetworkProbe.Preference,
            allowFallback: !DownloadRoutePolicy.IsCoreAsset(url));

        var routes = new List<(string Url, HttpClient Client, string Label)>();
        foreach (var (hostUrl, kind) in hosts)
        {
            routes.Add((hostUrl, f.Client, $"{kind}(默认通道)"));
            routes.Add((hostUrl, f.DirectClient, $"{kind}(直连)"));
        }

        var log = Logger.Instance;
        var errors = new List<string>();

        for (int round = 1; round <= Math.Max(1, rounds); round++)
        {
            // Round-70: ONE token for the whole hedge, cancelled the moment a
            // route wins. Before this, the losing three kept downloading the
            // same file for up to 15 s each, discarded their bytes, and their
            // parsing/allocation ran on the thread pool while the user was
            // clicking around — measured as 48–218 ms of "waiting for the UI
            // thread" even though no page did anything slow.
            using var hedge = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pending = new List<Task<(bool Ok, string? Data, string Label, string? Err)>>();
            foreach (var (u, client, label) in routes)
            {
                pending.Add(Task.Run(async () =>
                {
                    using var perRoute = CancellationTokenSource.CreateLinkedTokenSource(hedge.Token);
                    perRoute.CancelAfter(TimeSpan.FromSeconds(perRouteTimeoutSec));
                    try
                    {
                        var data = await client.GetStringAsync(u, perRoute.Token).ConfigureAwait(false);
                        return (true, (string?)data, label, (string?)null);
                    }
                    catch (Exception ex)
                    {
                        return (false, (string?)null, label,
                            ct.IsCancellationRequested
                                ? "cancelled"
                                : hedge.IsCancellationRequested ? "superseded" : ex.Message);
                    }
                }, CancellationToken.None));
            }

            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(done);
                var (ok, data, label, err) = await done.ConfigureAwait(false);
                if (ok && data != null)
                {
                    // Stop the also-rans immediately: their bytes will never be
                    // used, and letting them finish is pure CPU/GC pressure.
                    try { hedge.Cancel(); } catch { }
                    if (round > 1) log.Info(logTag, $"Route '{label}' succeeded on retry round {round}.");
                    else log.Info(logTag, $"Route '{label}' succeeded.");
                    return data;
                }
                var (warn, text) = RouteFailurePolicy.DescribeFailure(label, round, err);
                if (warn) log.Warn(logTag, text);
                else log.Info(logTag, text);
                errors.Add($"{label}: {err}");
            }

            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            if (RouteFailurePolicy.ShouldRetry(errors.TakeLast(routes.Count).ToList(), round, rounds))
            {
                log.Warn(logTag, $"All {routes.Count} routes failed on round {round}; retrying once...");
                await Task.Delay(800, ct).ConfigureAwait(false);
            }
            else
            {
                break; // permanent failures (404 etc.) cannot get better by retrying
            }
        }

        var detail = string.Join("; ", errors.TakeLast(routes.Count));
        throw new HttpRequestException($"所有 {routes.Count} 条线路均失败（已重试）：{detail}");
    }
}
