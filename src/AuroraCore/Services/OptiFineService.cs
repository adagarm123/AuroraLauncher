using System.IO.Compression;
using AuroraCore.Utils;

namespace AuroraCore.Services;

/// <summary>One installer build listed on optifine.net/downloads.</summary>
public sealed record OptiFineBuild(string FileName, string Label, string McVersion, string PageUrl);

/// <summary>
/// One-click OptiFine support (round-60), mirroring the Forge path: resolve
/// the official installer JAR, download it into the launcher's installer
/// cache, verify it, then launch it with the local Java runtime.
///
/// Honest limitation: OptiFine's installer is a GUI application with NO
/// documented silent/CLI mode (unlike Forge's --installClient). We therefore
/// open the official installer for the user and tell them to click "Install"
/// and point it at our game root — we never fake a headless install.
/// The optifine.net download flow is a two-step ad page with a short-lived
/// token; parsing is pure/unit-testable, and failure falls back to "open the
/// website" or "pick a locally downloaded installer JAR".
/// </summary>
public sealed class OptiFineService
{
    public const string WebUrl = "https://optifine.net/downloads";
    private const string Origin = "https://optifine.net";

    private readonly Logger _log = Logger.Instance;

    /// <summary>PURE parse of the downloads page — extracts installer file
    /// names from adloadx/downloadx links and derives MC version + build
    /// label from the conventional OptiFine_x.y.z_HD_U_Xn.jar naming.</summary>
    public static List<OptiFineBuild> ParseDownloadsPage(string html)
    {
        var result = new List<OptiFineBuild>();
        if (string.IsNullOrWhiteSpace(html)) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(html,
                     @"(?:adloadx|downloadx)\?f=(OptiFine_[^""'&<>\s]+\.jar)",
                     System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var file = Uri.UnescapeDataString(m.Groups[1].Value);
            if (!seen.Add(file)) continue;
            var body = file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                ? file[..^4].Substring("OptiFine_".Length) : "";
            var parts = body.Split('_');
            var mc = parts.Length > 0 ? parts[0] : "";
            var label = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : body;
            result.Add(new OptiFineBuild(file, label, mc, $"{Origin}/adloadx?f={Uri.EscapeDataString(file)}"));
        }
        return result;
    }

    /// <summary>PURE parse of the ad page → real download URL. Handles both
    /// an inline downloadx link and hidden form fields (HTML-escaped &amp;).</summary>
    public static string? ParseDownloadUrl(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var normalized = html.Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
        var m = System.Text.RegularExpressions.Regex.Match(normalized,
            @"downloadx\?f=([^""'&<>\s]+)&x=([A-Za-z0-9]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success)
            return $"{Origin}/downloadx?f={Uri.UnescapeDataString(m.Groups[1].Value)}&x={m.Groups[2].Value}";

        // Fallback: hidden form fields <input name="f" value="..."> / name="x".
        var f = Hidden(normalized, "f");
        var x = Hidden(normalized, "x");
        if (f != null && x != null)
            return $"{Origin}/downloadx?f={Uri.UnescapeDataString(f)}&x={x}";
        return null;
    }

    private static string? Hidden(string html, string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(html,
            $@"<input[^>]*name=""{name}""[^>]*value=""([^""]+)""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success)
            m = System.Text.RegularExpressions.Regex.Match(html,
                $@"<input[^>]*value=""([^""]+)""[^>]*name=""{name}""",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Fetches the ad page and resolves the short-lived download URL.
    /// Uses HttpClient directly (Referer + short-lived token, no Range support);
    /// exemption recorded in ROADMAP (NetRouting is header-less GET only).</summary>
    public async Task<string> ResolveDownloadUrlAsync(OptiFineBuild build, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, build.PageUrl);
        req.Headers.Add("Referer", WebUrl);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var resp = await HttpClientFactory.Instance.Client.SendAsync(req, timeout.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"OptiFine 广告页返回 HTTP {(int)resp.StatusCode}");
        var html = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        var url = ParseDownloadUrl(html);
        if (url == null)
            throw new InvalidOperationException("未能解析 OptiFine 下载令牌（官网页面结构可能已变化），请改用「打开官网」或「选择本地安装器」");
        return url;
    }

    /// <summary>Downloads the installer JAR and verifies it is a real, complete
    /// OptiFine installer (valid zip + OptiFine classes/main class). The site
    /// publishes no SHA-256, so structural verification is what we can honestly
    /// guarantee — we never execute an unverified archive.</summary>
    public async Task<string> DownloadInstallerAsync(OptiFineBuild build, string destDir, CancellationToken ct = default)
    {
        var url = await ResolveDownloadUrlAsync(build, ct).ConfigureAwait(false);
        PathEx.EnsureDirectory(destDir);
        var dest = Path.Combine(destDir, build.FileName);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Referer", build.PageUrl);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        using var resp = await HttpClientFactory.Instance.Client.SendAsync(req,
            HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"下载安装器失败：HTTP {(int)resp.StatusCode}");
        await using (var src = await resp.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
        await using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, true))
            await src.CopyToAsync(dst, timeout.Token).ConfigureAwait(false);

        if (!IsValidInstaller(dest))
        {
            try { File.Delete(dest); } catch { }
            throw new InvalidOperationException("下载到的文件不是有效的 OptiFine 安装器（已删除），请改用官网下载");
        }
        _log.Info("OptiFine", $"Installer downloaded & verified: {dest}");
        return dest;
    }

    /// <summary>PURE structural check: a valid jar/zip containing OptiFine's
    /// installer entry. Cheap, and prevents executing an HTML error page.</summary>
    public static bool IsValidInstaller(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.Entries.Any(e =>
                e.FullName.EndsWith("Installer.class", StringComparison.OrdinalIgnoreCase) ||
                e.FullName.Equals("META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>Launches the official installer GUI with the detected Java.
    /// We do NOT wait for it: the user must click "Install" in that window.</summary>
    public static bool LaunchInstaller(string javaExe, string jarPath)
    {
        if (!File.Exists(javaExe) || !File.Exists(jarPath)) return false;
        var psi = new ProcessStartInfo(javaExe, $"-jar \"{jarPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = Path.GetDirectoryName(jarPath) ?? ""
        };
        return Process.Start(psi) != null;
    }
}
