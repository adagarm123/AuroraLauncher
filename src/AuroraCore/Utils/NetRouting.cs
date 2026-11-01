using AuroraCore.Download;

namespace AuroraCore.Utils;

/// <summary>Hedged multi-route HTTP GET: fires all routes (primary proxy /
/// direct / mirror × both channels) simultaneously and takes the first success.
/// Eliminates the "stuck for tens of seconds" effect of sequential fallbacks
/// when earlier routes are silently blackholed by the network.</summary>
public static class NetRouting
{
    /// <summary>Fetches a string. <paramref name="mirrorUrl"/> may be null (no
    /// mirror route). Each route has its own timeout; the whole hedge is
    /// retried once, and the aggregated per-route errors are reported.</summary>
    public static async Task<string> GetStringAsync(string url, string? mirrorUrl, CancellationToken ct,
        int perRouteTimeoutSec = 15, int rounds = 2, string logTag = "Net")
    {
        var f = HttpClientFactory.Instance;
        var routes = new List<(string Url, HttpClient Client, string Label)>
        {
            (url, f.Client, "官方(默认通道)"),
            (url, f.DirectClient, "官方(直连)"),
        };
        if (!string.IsNullOrEmpty(mirrorUrl) && !string.Equals(mirrorUrl, url, StringComparison.OrdinalIgnoreCase))
        {
            routes.Add((mirrorUrl, f.Client, "镜像(默认通道)"));
            routes.Add((mirrorUrl, f.DirectClient, "镜像(直连)"));
        }

        var log = Logger.Instance;
        var errors = new List<string>();

        for (int round = 1; round <= Math.Max(1, rounds); round++)
        {
            var pending = new List<Task<(bool Ok, string? Data, string Label, string? Err)>>();
            foreach (var (u, client, label) in routes)
            {
                pending.Add(Task.Run(async () =>
                {
                    using var perRoute = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    perRoute.CancelAfter(TimeSpan.FromSeconds(perRouteTimeoutSec));
                    try
                    {
                        var data = await client.GetStringAsync(u, perRoute.Token).ConfigureAwait(false);
                        return (true, (string?)data, label, (string?)null);
                    }
                    catch (Exception ex)
                    {
                        return (false, (string?)null, label,
                            ct.IsCancellationRequested ? "cancelled" : ex.Message);
                    }
                }, ct));
            }

            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(done);
                var (ok, data, label, err) = await done.ConfigureAwait(false);
                if (ok && data != null)
                {
                    if (round > 1) log.Info(logTag, $"Route '{label}' succeeded on retry round {round}.");
                    else log.Info(logTag, $"Route '{label}' succeeded.");
                    return data;
                }
                log.Warn(logTag, $"Route '{label}' failed (round {round}): {err}");
                errors.Add($"{label}: {err}");
            }

            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            if (round < rounds)
            {
                log.Warn(logTag, $"All {routes.Count} routes failed on round {round}; retrying once...");
                await Task.Delay(800, ct).ConfigureAwait(false);
            }
        }

        var detail = string.Join("; ", errors.TakeLast(routes.Count));
        throw new HttpRequestException($"所有 {routes.Count} 条线路均失败（已重试）：{detail}");
    }
}
