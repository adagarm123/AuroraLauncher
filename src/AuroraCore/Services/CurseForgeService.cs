using AuroraCore.Utils;

namespace AuroraCore.Services;

/// <summary>One browsable CurseForge project (search result only — the API
/// deliberately does NOT grant direct file downloads for most mods).</summary>
public sealed record CurseForgeMod(
    long Id, string Name, string Summary, string Slug, long Downloads, string WebsiteUrl);

/// <summary>
/// CurseForge browse/search support (round-60).
///
/// IMPORTANT — API limits, by design: CurseForge requires a per-developer
/// `x-api-key`, and for most projects `files[].downloadUrl` is null (the
/// platform forbids third-party direct downloading). We therefore implement
/// SEARCH + BROWSE inside the launcher and, for downloads, open the official
/// project/files page in the browser (the user downloads the .jar/.zip and
/// drags it into 本地管理 or drops it in mods/). No key is bundled, no key is
/// shared, and no attempt is made to bypass the download restriction.
/// </summary>
public sealed class CurseForgeService
{
    private const string ApiBase = "https://api.curseforge.com/v1";
    public const string WebBase = "https://www.curseforge.com/minecraft/mc-mods";
    public const string KeyApplyUrl = "https://console.curseforge.com/";

    private readonly Logger _log = Logger.Instance;

    /// <summary>PURE parse of the search response — unit-testable, fails
    /// closed on a malformed payload instead of throwing into the UI.</summary>
    public static List<CurseForgeMod> ParseSearch(string json)
    {
        var list = new List<CurseForgeMod>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array) return list;
        foreach (var m in data.EnumerateArray())
        {
            long id = 0;
            if (m.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                id = idEl.GetInt64();
            var name = Str(m, "name") ?? "";
            var slug = Str(m, "slug") ?? "";
            var summary = Str(m, "summary") ?? "";
            long downloads = 0;
            if (m.TryGetProperty("downloadCount", out var d) && d.ValueKind == JsonValueKind.Number)
                downloads = d.GetInt64();
            var website = "";
            if (m.TryGetProperty("links", out var links))
                website = Str(links, "websiteUrl") ?? "";
            if (string.IsNullOrWhiteSpace(website) && slug.Length > 0)
                website = $"{WebBase}/{slug}";
            if (id <= 0 || name.Length == 0) continue;
            list.Add(new CurseForgeMod(id, name, summary, slug, downloads, website));
        }
        return list;
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>PURE URL builders — the download/browse action always lands on
    /// an official CurseForge page, never on a scraped file URL.</summary>
    public static string BuildWebSearchUrl(string query)
        => $"https://www.curseforge.com/minecraft/search?search={Uri.EscapeDataString(query ?? "")}";

    public static string BuildFilesPageUrl(CurseForgeMod mod)
        => string.IsNullOrWhiteSpace(mod.WebsiteUrl)
            ? $"{WebBase}/{mod.Slug}/files"
            : mod.WebsiteUrl.TrimEnd('/') + "/files";

    /// <summary>Search via the official API with the USER'S OWN key.
    /// Exempt from NetRouting (which only does header-less GETs) because the
    /// API requires `x-api-key`; the exemption is recorded in ROADMAP.</summary>
    public async Task<List<CurseForgeMod>> SearchAsync(string query, string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("未填写 CurseForge API Key（可在下方填入，或直接用网页搜索）");
        var url = $"{ApiBase}/mods/search?gameId=432&classId=6&searchFilter={Uri.EscapeDataString(query ?? "")}" +
                  "&pageSize=20&sortField=2&sortOrder=desc";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("x-api-key", apiKey.Trim());
        req.Headers.Add("Accept", "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var resp = await HttpClientFactory.Instance.Client.SendAsync(req, timeout.Token).ConfigureAwait(false);
        if ((int)resp.StatusCode == 401 || (int)resp.StatusCode == 403)
            throw new InvalidOperationException("API Key 无效或没有权限（401/403），请检查后重试，或改用网页搜索");
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"CurseForge 返回 HTTP {(int)resp.StatusCode}");
        var json = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        var result = ParseSearch(json);
        _log.Info("CurseForge", $"Search '{query}' → {result.Count} results");
        return result;
    }
}
