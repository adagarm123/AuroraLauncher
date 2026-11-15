using System.Collections.Generic;
using System.Linq;

namespace AuroraCore.Utils;

/// <summary>Round-85: which route(s) the launcher may use.
/// <see cref="Auto"/> = race both, faster one first; the two forced modes are
/// for people whose network makes one route useless (or who don't trust the
/// mirror).</summary>
public enum DownloadPolicy
{
    Auto = 0,
    Official = 1,
    Mirror = 2
}

public enum RoutePreference { Official, Mirror }

/// <summary>
/// Round-85: the single place that decides route ORDER and route SET.
///
/// Round-71 established that the route set must not accidentally collapse to two
/// dead mirror routes; the fix was "normalize to official first, then derive the
/// mirror". That invariant stays: every input is normalized before anything
/// else. What is new here is that the USER may now deliberately restrict the
/// set — and that the ordering comes from a real measurement instead of a
/// guess.
/// </summary>
public static class DownloadRoutePolicy
{
    /// <summary>Anything slower than this is treated as "not usable" — a route
    /// that answers after 3 s would only waste the user's patience.</summary>
    public const int UnreachableMs = 3000;

    /// <summary>A route must be at least this much faster to be preferred when
    /// both work. Without a margin, two 240/250 ms results would flip the
    /// preference on every probe for no benefit.</summary>
    public const int SwitchMarginMs = 40;

    public static bool IsReachable(long? ms) => ms is > 0 and <= UnreachableMs;

    /// <summary>Pure decision. `fallback` is what to return when neither route
    /// answered at all (keep the last known preference — probing again later is
    /// cheaper than guessing).</summary>
    public static RoutePreference Decide(long? officialMs, long? mirrorMs, RoutePreference fallback)
    {
        var offOk = IsReachable(officialMs);
        var mirOk = IsReachable(mirrorMs);
        if (offOk && !mirOk) return RoutePreference.Official;
        if (mirOk && !offOk) return RoutePreference.Mirror;
        if (!offOk && !mirOk) return fallback;

        var off = officialMs!.Value;
        var mir = mirrorMs!.Value;
        if (off + SwitchMarginMs <= mir) return RoutePreference.Official;
        if (mir + SwitchMarginMs <= off) return RoutePreference.Mirror;
        return fallback; // statistically a tie → don't flap
    }

    /// <summary>Route kinds in the order they should be tried, for a policy.
    /// Auto always contains BOTH routes: if the probe was wrong (captive portal,
    /// VPN that came up later), the second route still saves the download.</summary>
    public static IReadOnlyList<string> Order(DownloadPolicy policy, RoutePreference preference)
        => policy switch
        {
            DownloadPolicy.Official => new[] { "官方" },
            DownloadPolicy.Mirror => new[] { "镜像" },
            _ => preference == RoutePreference.Official
                ? new[] { "官方", "镜像" }
                : new[] { "镜像", "官方" }
        };

    /// <summary>
    /// Route set for a URL. `allowFallback` is false for CORE files (client jar,
    /// version JSON, libraries, asset index, loader installer): when the user
    /// forced a route, a core download must fail loudly instead of silently
    /// pulling bytes from a host they told us not to use. Icons / sounds /
    /// language files may fall back silently.
    /// </summary>
    public static List<(string Url, string Kind)> BuildHosts(string url, string? mirrorUrl,
        DownloadPolicy policy, RoutePreference preference, bool allowFallback)
    {
        var official = LauncherConfig.ToOfficialUrl(url);
        var mirror = LauncherConfig.ToBmclapiUrl(official);
        if (!string.IsNullOrWhiteSpace(mirrorUrl))
            mirror = LauncherConfig.ToBmclapiUrl(LauncherConfig.ToOfficialUrl(mirrorUrl));

        var result = new List<(string Url, string Kind)>();
        void Add(string? u, string kind)
        {
            if (string.IsNullOrWhiteSpace(u)) return;
            if (result.Any(h => string.Equals(h.Url, u, StringComparison.OrdinalIgnoreCase))) return;
            result.Add((u, kind));
        }

        switch (policy)
        {
            case DownloadPolicy.Official:
                Add(official, "官方");
                if (allowFallback) Add(mirror, "镜像");
                break;
            case DownloadPolicy.Mirror:
                Add(mirror, "镜像");
                if (allowFallback) Add(official, "官方");
                break;
            default:
                foreach (var kind in Order(DownloadPolicy.Auto, preference))
                    Add(kind == "官方" ? official : mirror, kind);
                break;
        }

        // If the mirror rewrite was a no-op (a non-Mojang host), the set must
        // still contain the URL itself rather than ending up empty.
        if (result.Count == 0) Add(url, "直连");
        return result;
    }

    /// <summary>Core = the file the game cannot start without. Used only to
    /// decide whether a forced route may silently fall back.</summary>
    public static bool IsCoreAsset(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;
        var u = url.ToLowerInvariant();
        // Libraries / client jar / version json / indexes / launcher meta.
        if (u.Contains(".jar")) return true;
        if (u.Contains("/versions/")) return true;
        if (u.Contains("/libraries/")) return true;
        if (u.Contains("version_manifest")) return true;
        if (u.Contains("/indexes/")) return true;
        // Non-core: asset objects (sounds, textures, languages) and images.
        return false;
    }

    public static string DisplayName(DownloadPolicy policy) => policy switch
    {
        DownloadPolicy.Official => "仅使用官方线路",
        DownloadPolicy.Mirror => "仅使用镜像线路（BMCLAPI）",
        _ => "自动选择（推荐，测速后择优）"
    };

    public static DownloadPolicy Parse(string? display) => display switch
    {
        "仅使用官方线路" => DownloadPolicy.Official,
        "仅使用镜像线路（BMCLAPI）" => DownloadPolicy.Mirror,
        _ => DownloadPolicy.Auto
    };

    /// <summary>The grey line under the dropdown. Says what is happening AND why,
    /// because "为什么它自己选了这个" is the first question a user asks.</summary>
    public static string Describe(DownloadPolicy policy, RoutePreference preference,
        long? officialMs, long? mirrorMs, bool probed)
    {
        var numbers = probed
            ? $"当前网络：官方 {(IsReachable(officialMs) ? $"{officialMs}ms" : "不可达")} / " +
              $"BMCLAPI {(IsReachable(mirrorMs) ? $"{mirrorMs}ms" : "不可达")}"
            : "当前网络：尚未测速（进入下载页会自动测一次，结果缓存 5 分钟）";

        var why = policy switch
        {
            DownloadPolicy.Official =>
                "策略：强制官方线路（核心文件失败会直接报错，不会偷偷走镜像）",
            DownloadPolicy.Mirror =>
                "策略：强制镜像线路（官方被墙或在国外访问慢时用这个）",
            _ => preference == RoutePreference.Official
                ? "策略：自动 —— 当前优先官方，官方失败自动回退镜像"
                : "策略：自动 —— 当前优先镜像，镜像失败自动回退官方"
        };
        return numbers + " · " + why;
    }
}
