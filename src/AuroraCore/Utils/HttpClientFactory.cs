namespace AuroraCore.Utils;

/// <summary>
/// Shared HttpClients with configurable timeout and proxy. No external
/// dependencies; proxy is read from <see cref="LauncherConfig"/>. TLS
/// verification is NEVER disabled — network problems are handled by retry,
/// dual-route fallback and the download engine's backoff, not by weakening
/// security.
/// </summary>
public sealed class HttpClientFactory
{
    private static readonly HttpClientFactory _instance = new();
    public static HttpClientFactory Instance => _instance;

    private HttpClient _client;
    private HttpClient? _directClient;
    private readonly object _lock = new();

    private HttpClientFactory()
    {
        _client = BuildClient(ConfigureHandler(null));
    }

    /// <summary>Primary client: honors the configured proxy (if any), otherwise
    /// the system proxy.</summary>
    public HttpClient Client => _client;

    /// <summary>Fallback client that BYPASSES all proxies (direct connection).
    /// Used when the primary route fails — e.g. a dead system proxy or an
    /// unreachable proxy for specific domains.</summary>
    public HttpClient DirectClient => _directClient ??= BuildClient(ConfigureHandler(null, direct: true));

    /// <summary>Route candidates in try order: primary, then direct.</summary>
    public IEnumerable<HttpClient> AllClients
    {
        get { yield return _client; yield return DirectClient; }
    }

    /// <summary>Apply a proxy (http/https) or clear it. Rebuilds the primary
    /// client — but ONLY when the proxy actually changed: this is called at
    /// every config load, and blindly disposing the old client here used to
    /// break services that had already captured a reference ("Cannot access
    /// a disposed object" on Microsoft login). The replaced client is left
    /// for the GC so in-flight requests can finish.</summary>
    public void ConfigureProxy(string? proxyUrl)
    {
        proxyUrl = string.IsNullOrWhiteSpace(proxyUrl) ? null : proxyUrl!.Trim();
        lock (_lock)
        {
            if (_proxyUrl == proxyUrl && _client != null!) return;
            _proxyUrl = proxyUrl;
            _client = BuildClient(ConfigureHandler(proxyUrl));
        }
    }

    private string? _proxyUrl;

    private static HttpClientHandler ConfigureHandler(string? proxyUrl, bool direct = false)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        if (!direct && !string.IsNullOrWhiteSpace(proxyUrl) && Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri))
        {
            handler.Proxy = new WebProxy(uri) { UseDefaultCredentials = true };
            handler.UseProxy = true;
        }
        else if (direct)
        {
            handler.UseProxy = false;
        }
        return handler;
    }

    private static HttpClient BuildClient(HttpClientHandler handler) => new(handler)
    {
        Timeout = TimeSpan.FromSeconds(60),
        DefaultRequestHeaders = { { "User-Agent", "AuroraLauncher/1.0 (+https://github.com/aurora-launcher)" } }
    };
}
