using System.Text.Json;

namespace AuroraCore.Services;

/// <summary>Round-98: the settings page now offers a second JDK vendor.
/// Azul Zulu builds of OpenJDK are hosted on Azul's own CDN, which is often
/// reachable when api.adoptium.net / GitHub release assets are not.</summary>
public enum JavaDistribution
{
    Temurin = 1,
    Zulu = 2,
}

/// <summary>
/// Pure, offline-testable rules for JDK distribution selection.
/// Zulu's community API returns MANY bundles for one java_version (plain,
/// -fx-, -crac-, plus older patch builds). Only the plain JDK zip with a
/// published SHA-256 from the official CDN may be installed — a JRE, a
/// JavaFX build or an unverifiable archive must never be handed to the
/// extractor. Host allowlisting prevents a poisoned API response from
/// turning the launcher into an arbitrary-URL downloader.
/// </summary>
public static class JavaDistributionPolicy
{
    public const string TemurinApiBase = "https://api.adoptium.net/v3/assets/latest/";
    public const string ZuluApiBase = "https://api.azul.com/zulu/download/community/v1.0/bundles/";
    public const string ZuluCdnHost = "cdn.azul.com";

    public static string DisplayText(JavaDistribution d) => d switch
    {
        JavaDistribution.Zulu => "Azul Zulu",
        _ => "Eclipse Temurin (Adoptium)",
    };

    public static JavaDistribution FromDisplayText(string? text) => text switch
    {
        "Azul Zulu" => JavaDistribution.Zulu,
        _ => JavaDistribution.Temurin,
    };

    public static IReadOnlyList<string> Options { get; } = new[]
    {
        DisplayText(JavaDistribution.Temurin),
        DisplayText(JavaDistribution.Zulu),
    };

    /// <summary>include_fields is REQUIRED: without it the API omits both
    /// sha256_hash and size, and the install path refuses unverified zips.</summary>
    public static string BuildZuluApiUrl(int major) =>
        $"{ZuluApiBase}?java_version={Math.Max(8, major)}&os=windows&arch=x86_64" +
        "&archive_type=zip&bundle_type=jdk&latest=true&include_fields=sha256_hash,size";

    public static bool IsAllowedZuluHost(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
           uri.Scheme == Uri.UriSchemeHttps &&
           string.Equals(uri.Host, ZuluCdnHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>Plain JDK only. -fx- carries JavaFX and is ~100 MB larger;
    /// -crac- is a Coordinated Restore build, not a normal runtime.</summary>
    public static bool IsPlainJdkBundle(string name)
        => name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
           !name.Contains("-fx-", StringComparison.OrdinalIgnoreCase) &&
           !name.Contains("-crac-", StringComparison.OrdinalIgnoreCase) &&
           !name.Contains("-jre", StringComparison.OrdinalIgnoreCase);

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int[] VersionOf(JsonElement e)
    {
        if (!e.TryGetProperty("java_version", out var v) || v.ValueKind != JsonValueKind.Array) return Array.Empty<int>();
        return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).ToArray();
    }

    /// <summary>Dictionary-style comparison, missing segments count as 0.</summary>
    public static int CompareVersions(int[] a, int[] b)
    {
        var n = Math.Max(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    public static JavaPackage? ParseZuluLatest(string json, int major)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        JavaPackage? best = null;
        int[] bestVersion = Array.Empty<int>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var name = Str(item, "name");
            var url = Str(item, "url");
            var sha = Str(item, "sha256_hash")?.Trim();
            if (name == null || url == null || sha == null) continue;
            if (!IsPlainJdkBundle(name) || !IsAllowedZuluHost(url)) continue;
            if (sha.Length != 64 || !sha.All(Uri.IsHexDigit)) continue;

            long size = 0;
            if (item.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number)
                size = sz.GetInt64();
            if (size <= 0) continue;

            var version = VersionOf(item);
            if (best != null && CompareVersions(version, bestVersion) <= 0) continue;

            var zulu = item.TryGetProperty("zulu_version", out var zv) && zv.ValueKind == JsonValueKind.Array
                ? string.Join(".", zv.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()))
                : "latest";
            best = new JavaPackage(name, url, sha, size, $"zulu{major}-{zulu}", major)
            {
                Vendor = "Zulu",
            };
            bestVersion = version;
        }
        return best;
    }
}
