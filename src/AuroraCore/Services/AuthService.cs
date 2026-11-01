using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AuroraCore.Utils;

namespace AuroraCore.Services;

public class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Type { get; set; } = "offline"; // "microsoft" | "offline"
    public string Username { get; set; } = "";
    public string Uuid { get; set; } = "";
    // The Microsoft refresh token is persisted ENCRYPTED (see CredentialStore).
    // Access tokens live only in memory and are never written to disk.
    public string? EncryptedRefreshToken { get; set; }
    public DateTime? TokenExpiresAt { get; set; }

    // ---- Runtime-only fields (not serialized) ----
    [System.Text.Json.Serialization.JsonIgnore]
    public string? AccessToken { get; set; }
}

/// <summary>
/// Authentication provider. Supports:
///  - Microsoft account via the OAuth2 device-code flow (Xbox Live -> XSTS ->
///    Minecraft Services), with refresh-token persistence encrypted via DPAPI.
///  - Offline (username-only) accounts. These do NOT bypass online-mode server
///    verification; they simply play in offline mode locally.
/// No client secret is used (public client). The Azure client id is read from
/// the AURORA_MICROSOFT_CLIENT_ID environment variable if set, otherwise a
/// documented placeholder that the operator must replace with their own
/// registered Azure AD application id.
/// </summary>
public sealed class AuthService
{
    // IMPORTANT: register your own Azure AD "Public client" app and set the
    // id via AURORA_MICROSOFT_CLIENT_ID or LauncherConfig.MicrosoftClientId
    // (Settings page). The placeholder below will not authenticate.
    /// <summary>Actionable setup hint shown instead of a raw HTTP 400 when
    /// the Azure app id is missing or invalid.</summary>
    public const string MicrosoftSetupHint =
        "微软登录尚未配置 Azure 应用 ID。步骤：① 打开 portal.azure.com → 「应用注册」→ 新建注册，" +
        "账户类型选「个人 Microsoft 账户」，重定向平台选「移动和桌面应用程序」并添加 http://localhost；" +
        "② 在「身份验证」页启用「允许公共客户端流」（设备码登录必需）；③ 复制「应用程序(客户端) ID」；" +
        "④ 在启动器 设置 → 「微软登录 Client ID」填入并保存，或设置环境变量 AURORA_MICROSOFT_CLIENT_ID 后重启。";

    /// <summary>Resolution order: env var → config.json → null (未配置).</summary>
    public static string? ConfiguredClientId
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("AURORA_MICROSOFT_CLIENT_ID");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            var cfg = LauncherConfig.Instance.MicrosoftClientId;
            if (!string.IsNullOrWhiteSpace(cfg)) return cfg.Trim();
            return null;
        }
    }

    /// <summary>False when Microsoft login cannot work yet (no Azure app id).</summary>
    public static bool IsMicrosoftLoginConfigured => ConfiguredClientId != null;

    private static string ClientId =>
        ConfiguredClientId ?? throw new InvalidOperationException(MicrosoftSetupHint);

    private static readonly string[] Scopes = { "XboxLive.signin", "offline_access" };

    // ALWAYS resolve the current client per call: HttpClientFactory may swap
    // the instance when proxy settings change — a cached reference went stale
    // and broke Microsoft login with 'Cannot access a disposed object'.
    private HttpClient Http => HttpClientFactory.Instance.Client;
    private readonly Logger _log = Logger.Instance;
    private readonly string _accountsFile;

    public AuthService()
    {
        _accountsFile = Path.Combine(LauncherConfig.Instance.RootDirectory, "accounts.json");
    }

    // ---------- Account persistence ----------
    public List<Account> LoadAccounts()
    {
        if (!File.Exists(_accountsFile)) return new List<Account>();
        try { return JsonHelper.Deserialize<List<Account>>(File.ReadAllText(_accountsFile)); }
        catch { return new List<Account>(); }
    }

    public void SaveAccounts(List<Account> accounts)
    {
        PathEx.EnsureDirectory(LauncherConfig.Instance.RootDirectory);
        File.WriteAllText(_accountsFile, JsonHelper.SerializeIndented(accounts));
    }

    // ---------- Microsoft device-code flow ----------
    public record DeviceCodePrompt(string DeviceCode, string UserCode, string VerificationUrl, string Message, int Interval, int ExpiresIn);

    public async Task<DeviceCodePrompt> BeginMicrosoftLoginAsync(CancellationToken ct = default)
    {
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = string.Join(" ", Scopes)
        });
        using var resp = await Http.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode", body, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var r = doc.RootElement;
        return new DeviceCodePrompt(
            r.GetProperty("device_code").GetString() ?? "",
            r.GetProperty("user_code").GetString() ?? "",
            r.GetProperty("verification_uri").GetString() ?? "",
            r.GetProperty("message").GetString() ?? "",
            r.GetProperty("interval").GetInt32(),
            r.GetProperty("expires_in").GetInt32());
    }

    public async Task<Account> CompleteMicrosoftLoginAsync(string deviceCode, CancellationToken ct = default)
    {
        // Poll until the user authenticates.
        var token = await PollMicrosoftTokenAsync(deviceCode, ct).ConfigureAwait(false);
        var xbl = await XboxAuthenticateAsync(token.AccessToken, ct).ConfigureAwait(false);
        var xsts = await XstsAuthenticateAsync(xbl.Token, ct).ConfigureAwait(false);
        var mc = await MinecraftLoginAsync(xbl.UserHash, xsts, ct).ConfigureAwait(false);
        var profile = await MinecraftProfileAsync(mc.AccessToken, ct).ConfigureAwait(false);

        var account = new Account
        {
            Type = "microsoft",
            Username = profile.Name,
            Uuid = profile.Id,
            AccessToken = mc.AccessToken,
            TokenExpiresAt = DateTime.UtcNow.AddSeconds(mc.ExpiresIn),
            EncryptedRefreshToken = CredentialStore.Encrypt(token.RefreshToken)
        };
        _log.Info("Auth", $"Microsoft login success for {profile.Name} (uuid {profile.Id}).");
        return account;
    }

    private async Task<(string AccessToken, string RefreshToken, int ExpiresIn)> PollMicrosoftTokenAsync(string deviceCode, CancellationToken ct)
    {
        var interval = 5;
        while (!ct.IsCancellationRequested)
        {
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = deviceCode
            });
            using var resp = await Http.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token", body, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(text);
                var r = doc.RootElement;
                return (r.GetProperty("access_token").GetString()!,
                        r.GetProperty("refresh_token").GetString()!,
                        r.GetProperty("expires_in").GetInt32());
            }
            using var err = JsonDocument.Parse(text);
            var code = err.RootElement.GetProperty("error").GetString();
            if (code == "authorization_pending")
            {
                await Task.Delay(interval * 1000, ct).ConfigureAwait(false);
                continue;
            }
            if (code == "slow_down") { interval += 5; continue; }
            if (code == "expired_token") throw new UnauthorizedAccessException("Device code expired. Please retry login.");
            if (code == "access_denied") throw new UnauthorizedAccessException("User denied the login request.");
            throw new HttpRequestException($"Token endpoint error: {code}");
        }
        throw new OperationCanceledException(ct);
    }

    public async Task RefreshIfNeededAsync(Account account, CancellationToken ct = default)
    {
        if (account.Type != "microsoft") return;
        if (account.AccessToken != null && account.TokenExpiresAt.HasValue &&
            account.TokenExpiresAt.Value > DateTime.UtcNow.AddMinutes(5)) return;

        if (string.IsNullOrEmpty(account.EncryptedRefreshToken))
            throw new InvalidOperationException("No refresh token stored for this account.");

        var refresh = CredentialStore.Decrypt(account.EncryptedRefreshToken!);
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh
        });
        using var resp = await Http.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token", body, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var r = doc.RootElement;
        account.AccessToken = r.GetProperty("access_token").GetString();
        account.TokenExpiresAt = DateTime.UtcNow.AddSeconds(r.GetProperty("expires_in").GetInt32());
        if (r.TryGetProperty("refresh_token", out var rt))
            account.EncryptedRefreshToken = CredentialStore.Encrypt(rt.GetString()!);
        _log.Info("Auth", $"Refreshed Microsoft token for {account.Username}.");
    }

    // ---------- Xbox Live / XSTS / Minecraft ----------
    private async Task<(string Token, string UserHash)> XboxAuthenticateAsync(string msToken, CancellationToken ct)
    {
        var payload = JsonHelper.Serialize(new
        {
            Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = "d=" + msToken },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync("https://user.auth.xboxlive.com/user/authenticate", content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var r = doc.RootElement;
        return (r.GetProperty("Token").GetString()!, r.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString()!);
    }

    private async Task<string> XstsAuthenticateAsync(string xblToken, CancellationToken ct)
    {
        var payload = JsonHelper.Serialize(new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xblToken } },
            RelyingParty = "http://xsts.auth.xboxlive.com",
            TokenType = "JWT"
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync("https://xsts.auth.xboxlive.com/xsts/authorize", content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return doc.RootElement.GetProperty("Token").GetString()!;
    }

    private async Task<(string AccessToken, int ExpiresIn)> MinecraftLoginAsync(string userHash, string xstsToken, CancellationToken ct)
    {
        var payload = JsonHelper.Serialize(new { identityToken = $"XBL3.0 x={userHash};{xstsToken}" });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync("https://api.minecraftservices.com/authentication/login_with_xbox", content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var r = doc.RootElement;
        return (r.GetProperty("access_token").GetString()!, r.GetProperty("expires_in").GetInt32());
    }

    private async Task<(string Id, string Name)> MinecraftProfileAsync(string mcToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcToken);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var r = doc.RootElement;
        return (r.GetProperty("id").GetString()!, r.GetProperty("name").GetString()!);
    }

    // ---------- Offline ----------
    public Account CreateOfflineAccount(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) username = "Player";
        // Offline UUID: name-based UUID v3 of "OfflinePlayer:<name>".
        var uuid = OfflineUuid(username);
        return new Account { Type = "offline", Username = username, Uuid = uuid };
    }

    public static string OfflineUuid(string username)
    {
        // MD5 of "OfflinePlayer:" + name, formatted as UUID v3.
        using var md5 = MD5.Create();
        var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        var hex = CryptoHelper.ToHex(bytes);
        return $"{hex[..8]}-{hex.Substring(8, 4)}-{hex.Substring(12, 4)}-{hex.Substring(16, 4)}-{hex.Substring(20, 12)}";
    }

    // ---------- Launch credentials ----------
    public async Task<LaunchCredentials> GetLaunchCredentialsAsync(Account account, CancellationToken ct = default)
    {
        if (account.Type == "microsoft")
        {
            await RefreshIfNeededAsync(account, ct).ConfigureAwait(false);
            return new LaunchCredentials
            {
                PlayerName = account.Username,
                Uuid = account.Uuid,
                AccessToken = account.AccessToken ?? "",
                UserType = "mojang",
                Xuid = "",
                UserProperties = "{}"
            };
        }
        // Offline: does NOT bypass online-mode server auth; plays in local offline mode.
        return new LaunchCredentials
        {
            PlayerName = account.Username,
            Uuid = account.Uuid,
            AccessToken = Guid.NewGuid().ToString(),
            UserType = "mojang",
            Xuid = "",
            UserProperties = "{}"
        };
    }
}

public class LaunchCredentials
{
    public string PlayerName { get; set; } = "";
    public string Uuid { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string UserType { get; set; } = "mojang";
    public string Xuid { get; set; } = "";
    public string UserProperties { get; set; } = "{}";
}

/// <summary>Encrypts secrets with Windows DPAPI via P/Invoke (no NuGet dependency).</summary>
internal static class CredentialStore
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    public static string Encrypt(string plain)
    {
        var bytes = Encoding.UTF8.GetBytes(plain);
        var inBlob = new DATA_BLOB { cbData = bytes.Length, pbData = Marshal.AllocHGlobal(bytes.Length) };
        Marshal.Copy(bytes, 0, inBlob.pbData, bytes.Length);
        var outBlob = new DATA_BLOB();
        try
        {
            if (!CryptProtectData(ref inBlob, "AuroraLauncher", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var outBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, outBytes, 0, outBlob.cbData);
            return "DPAPI:" + Convert.ToBase64String(outBytes);
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    public static string Decrypt(string encrypted)
    {
        if (encrypted.StartsWith("RAW:"))
            return Encoding.UTF8.GetString(Convert.FromBase64String(encrypted.Substring(4)));
        if (!encrypted.StartsWith("DPAPI:"))
            throw new InvalidOperationException("Unrecognized credential format.");
        var bytes = Convert.FromBase64String(encrypted.Substring(6));
        var inBlob = new DATA_BLOB { cbData = bytes.Length, pbData = Marshal.AllocHGlobal(bytes.Length) };
        Marshal.Copy(bytes, 0, inBlob.pbData, bytes.Length);
        var outBlob = new DATA_BLOB();
        try
        {
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var outBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, outBytes, 0, outBlob.cbData);
            return Encoding.UTF8.GetString(outBytes);
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }
}
