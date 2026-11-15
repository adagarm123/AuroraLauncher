namespace AuroraCore.Utils;

/// <summary>Round-70: how to react to a failed download route.
///
/// The user's log was full of `Route '镜像(直连)' failed (round 1): ... 404 (Not Found)`
/// followed by `All 4 routes failed on round 1; retrying once...`. Those 404s
/// are PERMANENT — the mirror simply does not host that endpoint (Forge/NeoForge
/// maven, some meta endpoints). Retrying costs a full extra round of HTTP +
/// parsing + allocation for every loader on every refresh, and the resulting
/// CPU/GC pressure is what the UI thread was measured waiting for.
///
/// Rules:
/// - 400/401/403/404/410 mean "this route will never serve this URL" → permanent;
/// - timeout / reset / 5xx / DNS are transient → worth the retry budget;
/// - retry only when at least one failure was transient.
/// </summary>
public static class RouteFailurePolicy
{
    private static readonly string[] PermanentMarkers =
    {
        "400", "401", "403", "404", "410", "not found", "forbidden", "unauthorized", "bad request",
        // Round-71: DNS resolution failures. A dead hostname (the deprecated
        // bmclapi.cn) does not start resolving 800 ms later, and neither does a
        // typo'd endpoint — retrying only delays the error the user needs to see.
        "no such host", "such host is known", "不知道这样的主机", "getaddrinfo",
        "name or service not known", "nodename nor servname", "无法解析", "dns"
    };

    public static bool IsPermanent(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return false;
        foreach (var marker in PermanentMarkers)
            if (error.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>A cancellation means the CALLER gave up (user navigated away) —
    /// never spend the retry budget on it.</summary>
    public static bool IsCancellation(string? error) =>
        !string.IsNullOrWhiteSpace(error) &&
        (error.Contains("cancel", StringComparison.OrdinalIgnoreCase) ||
         error.Contains("取消", StringComparison.OrdinalIgnoreCase));

    /// <summary>Retry only when the round budget allows AND some failure could
    /// plausibly succeed later (transient error, not a 404 and not a cancel).</summary>
    public static bool ShouldRetry(IReadOnlyCollection<string> failures, int round, int rounds)
    {
        if (round >= rounds) return false;
        foreach (var f in failures)
        {
            if (IsCancellation(f)) continue;
            if (!IsPermanent(f)) return true;
        }
        return false;
    }

    /// <summary>Wording for one failed attempt: a permanent failure is expected
    /// noise (mirror does not host this path) and must not be logged as a warning.</summary>
    public static (bool Warn, string Text) DescribeFailure(string label, int round, string? error)
    {
        if (IsPermanent(error))
            return (false, $"Route '{label}' not available for this path (permanent): {error}");
        return (true, $"Route '{label}' failed (round {round}): {error}");
    }
}
