using System.Text.Json;

namespace AuroraCore.Services;

/// <summary>One Maven artifact an official Forge / NeoForge installer expects
/// to find under &lt;gameRoot&gt;/libraries before it runs.</summary>
public sealed record InstallerLibrary(
    string Name, string RelativePath, string? Url, string? Sha1, long Size);

/// <summary>
/// Round-95: the official installers download their OWN dependencies (from
/// maven.creeperhost.net / Forge maven) with a bare JVM HTTP client that knows
/// nothing about our route policy. On a flaky network they die with
/// "These libraries failed to download. Try again. com.google.code.findbugs:jsr305:3.0.2".
///
/// Rather than asking the user to hand-place a jar, we read the library list
/// from inside the installer jar (installer_profile.json + version.json) and
/// pre-fetch missing artifacts through our own DownloadEngine. All parsing is
/// pure so it can be unit-tested without a network or a real jar.
/// </summary>
public static class InstallerDependencies
{
    public const string MirrorMaven = "https://bmclapi2.bangbang93.com/maven/";
    public const string CentralMaven = "https://repo1.maven.org/maven2/";

    public static readonly string[] ProfileFiles = { "installer_profile.json", "version.json" };

    public static IReadOnlyList<InstallerLibrary> Parse(string? json)
    {
        var result = new List<InstallerLibrary>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { return result; } // a truncated / non-JSON profile must never break the install
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("libraries", out var libs) ||
                libs.ValueKind != JsonValueKind.Array) return result;

            foreach (var lib in libs.EnumerateArray())
            {
                if (lib.ValueKind != JsonValueKind.Object) continue;
                var name = GetString(lib, "name");
                string? path = null, url = null, sha1 = null;
                long size = 0;
                if (lib.TryGetProperty("downloads", out var downloads) &&
                    downloads.ValueKind == JsonValueKind.Object &&
                    downloads.TryGetProperty("artifact", out var art) &&
                    art.ValueKind == JsonValueKind.Object)
                {
                    path = GetString(art, "path");
                    url = GetString(art, "url");
                    sha1 = GetString(art, "sha1");
                    size = GetLong(art, "size");
                }
                path ??= MavenPath(name);
                if (string.IsNullOrWhiteSpace(path)) continue;

                path = path.Replace('\\', '/').TrimStart('/');
                // Zip-slip / absolute-path guard: an installer jar is untrusted input.
                if (path.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(path) ||
                    path.StartsWith("http:", StringComparison.OrdinalIgnoreCase)) continue;

                result.Add(new InstallerLibrary(name ?? path, path, url, sha1, size));
            }
        }
        return result;
    }

    /// <summary>"com.google.code.findbugs:jsr305:3.0.2" →
    /// "com/google/code/findbugs/jsr305/3.0.2/jsr305-3.0.2.jar".
    /// Supports an optional classifier and an "@ext" suffix.</summary>
    public static string? MavenPath(string? coordinates)
    {
        if (string.IsNullOrWhiteSpace(coordinates)) return null;
        var s = coordinates.Trim();
        var ext = "jar";
        var at = s.IndexOf('@');
        if (at > 0) { ext = s[(at + 1)..]; s = s[..at]; }
        var parts = s.Split(':');
        if (parts.Length < 3) return null;
        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        if (group.Length == 0 || artifact.Length == 0 || version.Length == 0) return null;
        var classifier = parts.Length >= 4 ? "-" + parts[3] : "";
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.{ext}";
    }

    /// <summary>Tried in order. The mirror is fastest for Forge/MC libraries,
    /// Maven Central is the universal fallback (jsr305 lives there), and the
    /// installer's own URL is kept as a last resort. All are optional: a dead
    /// host must never abort the install before the installer even runs.</summary>
    public static IReadOnlyList<string> CandidateUrls(InstallerLibrary lib)
    {
        var list = new List<string>
        {
            MirrorMaven + lib.RelativePath,
            CentralMaven + lib.RelativePath
        };
        if (!string.IsNullOrWhiteSpace(lib.Url) &&
            Uri.TryCreate(lib.Url, UriKind.Absolute, out var u) &&
            (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
            list.Add(lib.Url);
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Maven coordinates the installer explicitly reported as failed.
    /// Only the text AFTER "failed to download" is considered, so the
    /// "Considering library trove:trove:1.0.2" line for a *successful* fetch
    /// is not mistaken for a failure.</summary>
    public static IReadOnlyList<string> FailedCoordinates(string? installerTail)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(installerTail)) return result;
        var marker = installerTail.IndexOf("failed to download", StringComparison.OrdinalIgnoreCase);
        var tail = marker >= 0 ? installerTail[marker..] : "";
        foreach (var token in tail.Split(new[] { '|', '\n', '\r' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var t = token.Trim().TrimEnd('.');
            if (t.Count(c => c == ':') < 2 || t.Contains(' ') || t.Length > 160) continue;
            if (t.Contains("http", StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(t);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Human-readable explanation for an installer that could not
    /// fetch its own dependencies. Names the exact artifacts when the output
    /// contains them.</summary>
    public static string? ExplainDependencyFailure(string? installerTail)
    {
        if (string.IsNullOrEmpty(installerTail)) return null;
        if (!installerTail.Contains("libraries failed to download", StringComparison.OrdinalIgnoreCase) &&
            !installerTail.Contains("failed to download", StringComparison.OrdinalIgnoreCase)) return null;

        var missing = FailedCoordinates(installerTail);
        var detail = missing.Count > 0
            ? "缺失：" + string.Join("、", missing.Take(5)) + (missing.Count > 5 ? " …" : "") + "。"
            : "";
        return "Forge/NeoForge 安装器在下载它自己的依赖库时被网络阻断了（它不走启动器的镜像线路）。" + detail +
               "启动器已尝试用镜像 / Maven Central 预先补齐这些库；若仍失败，请切换网络或代理后重试，或在「设置」里把线路改成镜像。";
    }

    private static string? GetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long GetLong(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        return 0;
    }
}
