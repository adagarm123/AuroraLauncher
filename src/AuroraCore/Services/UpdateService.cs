using System.IO;
using System.Security.Cryptography;
using System.Text;
using AuroraCore.Download;
using AuroraCore.Utils;

namespace AuroraCore.Services;

/// <summary>One entry of the update feed manifest (latest.json). The RSA
/// signature covers <see cref="SignedPayload"/> — a canonical string of the
/// security-relevant fields, so neither the download URL nor the file hash
/// can be swapped without invalidating it.</summary>
public sealed class UpdateManifest
{
    public int Round { get; set; }
    public string Version { get; set; } = "";
    public string ReleaseTime { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string Signature { get; set; } = "";

    /// <summary>The exact byte string the RSA signature covers.</summary>
    public string SignedPayload => $"{Round}|{Version}|{Url}|{Sha256}|{Size}";
}

/// <summary>
/// Client side of the auto-update feed (round-57, "自动更新 + 签名验证" roadmap
/// item). Security model:
///  - 防篡改: latest.json carries an RSA-4096 signature over the manifest
///    payload; the matching public key is EMBEDDED below. A tampered
///    manifest (or man-in-the-middle) fails verification and the update is
///    silently ignored.
///  - 防降级: only manifests with a STRICTLY higher round number than
///    <see cref="BuildInfo.CurrentRound"/> count as updates.
///  - 通道隔离: single stable channel; the download URL comes only from a
///    signature-verified manifest.
///
/// Update flow: silent check on startup → background download into
/// root/update/ (staged, SHA-256 verified) → on the NEXT startup a detached
/// cmd script swaps the staged exe over the running one (the running exe is
/// locked, so the swap must happen after exit) and relaunches.
///
/// Author workflow: scripts/publish.ps1 → scripts/sign-release.ps1 → upload
/// publish/AuroraLauncher.exe + publish/latest.json to the feed host. See
/// docs/auto-update.md.
/// </summary>
public sealed class UpdateService
{
    /// <summary>Embedded public key (pairs with scripts/signing/private_key.xml
    /// used by sign-release.ps1). The private key NEVER ships with the app.</summary>
    internal const string PublicKeyXml =
        @"<RSAKeyValue><Modulus>y7ANKg6K+bkz7iVSKBE/VzRIQRMdbrc6EIS/JjT82pzj/1nu5Q85VYNNb+0JEMTR73cpQb7pRc/Di4z9T8QA1nb2F6WA/SdlOoBZo7P7nBu3QSatQp2QzN21xBw0c4isoHw1m+L6Pz6A7WxBBmJh1NOQ/anCgI0jM8Mo1f1BVwsfA3/L5hhezcx5dRtyYAEJk8F77Mtm1ujxo/UN2ilVzOwg29dJWyaQDsIg6agfQVyguothFIWVgeVVls70gyuF/mVnus90/wUIeyrGDvUlYqY4zQvl4+W/gPRSiryuI8RygvXbIhFBbIgc2yIBsMyJXL5bsIyfF8zvCu5GkOFSsM6H1hz0o213igPd8uaci1iU+ffLQzqhIGGKVoZVD9oQ0batjXhO0RhGEtcjPvh2QExSB8wxUFrFzQGBZvhxXvuakXNse+G4BqaxKJkunt50zRYyKNre4E4RH9cdqJ0VnkZ9OsMr6wALL+4LuF2JxCFRsLpSmVLwdywd1p48mbvIKhppakLOy4UHA/trcSRVJQZWSu9REGj5fwSZZQLOsWxUidoQenTf4AUcwvIp0/bEJ1DO8RKWBKRXEy6SsM5NAQODGCQ1KnKQmHkHO5jhm5uDSaMluXsz0H6h/gPTJD0MczZLlmYDDDlzLAUYPuPxAuJ4kqOUzoI3MkW++lBF20E=</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

    /// <summary>Author: set this to your hosted latest.json URL once, before
    /// publishing (any static HTTPS host works — see docs/auto-update.md).
    /// Empty = update checks disabled; LauncherConfig.UpdateFeedUrl overrides
    /// this for testing.</summary>
    public const string DefaultFeedUrl = "";

    private readonly DownloadEngine _engine = new();
    private readonly Logger _log = Logger.Instance;

    public static string StagedExePath => Path.Combine(LauncherConfig.Instance.RootDirectory, "update", "AuroraLauncher.new.exe");
    public static string StagedInfoPath => Path.Combine(LauncherConfig.Instance.RootDirectory, "update", "build-info.txt");
    public static bool HasStagedUpdate() => File.Exists(StagedExePath);

    /// <summary>Resolves the feed URL: config override beats the baked-in
    /// default; empty/missing means update checks are disabled.</summary>
    public static string ResolveFeedUrl() =>
        LauncherConfig.Instance.UpdateFeedUrl is { Length: > 0 } cfg ? cfg : DefaultFeedUrl;

    /// <summary>Verifies the RSA signature over the manifest payload against
    /// the embedded (or test-injected) public key. Fails closed.</summary>
    public static bool VerifySignature(UpdateManifest manifest, string? publicKeyXmlOverride = null)
    {
        var keyXml = publicKeyXmlOverride ?? PublicKeyXml;
        if (string.IsNullOrWhiteSpace(manifest.Signature) || string.IsNullOrWhiteSpace(keyXml)) return false;
        try
        {
            using var rsa = RSA.Create();
            rsa.FromXmlString(keyXml);
            return rsa.VerifyData(Encoding.UTF8.GetBytes(manifest.SignedPayload),
                Convert.FromBase64String(manifest.Signature),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch { return false; }
    }

    /// <summary>防降级: only a STRICTLY higher round is an update.</summary>
    public static bool IsNewer(UpdateManifest manifest, int currentRound) => manifest.Round > currentRound;

    /// <summary>Fetches and verifies the feed manifest. Returns null when
    /// updates are disabled, the feed is unreachable, the signature is bad,
    /// or the manifest is not newer than this build — every negative result
    /// is silent by design (an update check must never bother the user).</summary>
    public async Task<UpdateManifest?> CheckAsync(CancellationToken ct = default)
    {
        var feed = ResolveFeedUrl();
        if (string.IsNullOrWhiteSpace(feed))
        {
            _log.Info("Update", "未配置更新源（UpdateFeedUrl），跳过检查。");
            return null;
        }
        string json;
        try
        {
            json = await NetRouting.GetStringAsync(feed, null, ct, perRouteTimeoutSec: 8, rounds: 1, logTag: "Update").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Info("Update", $"更新源不可达（不影响使用）: {ex.Message}");
            return null;
        }

        UpdateManifest? manifest;
        try { manifest = JsonHelper.Deserialize<UpdateManifest>(json); }
        catch (Exception ex)
        {
            _log.Warn("Update", "更新清单解析失败，忽略本次检查: " + ex.Message);
            return null;
        }
        if (manifest == null || manifest.Round <= 0 || string.IsNullOrWhiteSpace(manifest.Url)) return null;

        if (!VerifySignature(manifest))
        {
            _log.Warn("Update", $"更新清单签名校验失败（Round {manifest.Round}）——可能被篡改或密钥不匹配，忽略本次更新。");
            return null;
        }
        if (!IsNewer(manifest, BuildInfo.CurrentRound))
        {
            _log.Info("Update", $"已运行最新版本（当前第 {BuildInfo.CurrentRound} 轮，清单第 {manifest.Round} 轮）。");
            return null;
        }
        _log.Info("Update", $"发现新版本：第 {manifest.Round} 轮（当前第 {BuildInfo.CurrentRound} 轮）。");
        return manifest;
    }

    /// <summary>Downloads the new exe into the staging folder and verifies it
    /// against the manifest SHA-256. On success the update applies on the
    /// NEXT startup (see <see cref="ApplyStagedAndRestart"/>).</summary>
    public async Task DownloadStagedAsync(UpdateManifest manifest, Action<DownloadProgress>? progress, CancellationToken ct)
    {
        PathEx.EnsureDirectory(Path.GetDirectoryName(StagedExePath)!);
        await _engine.DownloadAsync(new[]
        {
            new DownloadEntry
            {
                Url = manifest.Url,
                Destination = StagedExePath,
                ExpectedSize = manifest.Size,
                Required = true,
                DisplayName = "启动器更新包"
            }
        }, progress, ct).ConfigureAwait(false);

        var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(StagedExePath, ct).ConfigureAwait(false)));
        if (!string.IsNullOrWhiteSpace(manifest.Sha256) &&
            !string.Equals(actual, manifest.Sha256.Trim().Replace("-", ""), StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(StagedExePath);
            throw new IOException("更新包校验失败（SHA-256 不匹配），已删除下载文件。");
        }

        File.WriteAllText(StagedInfoPath,
            "build-time : " + manifest.ReleaseTime + "\r\n" +
            "git-commit : (see release notes)\r\n" +
            "exe-sha256 : " + manifest.Sha256 + "\r\n" +
            "round      : " + manifest.Round + "\r\n" +
            "notes      : " + manifest.Notes.Replace("\r", " ").Replace("\n", " ") + "\r\n");
        _log.Info("Update", $"第 {manifest.Round} 轮更新包已下载并校验通过，下次启动时自动换装。");
    }

    /// <summary>Swaps the staged exe over the running one via a detached cmd
    /// script (the running exe is locked — the swap happens after we exit)
    /// and relaunches. The caller exits immediately afterwards.</summary>
    public static void ApplyStagedAndRestart()
    {
        if (!HasStagedUpdate()) return;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        var stageDir = Path.GetDirectoryName(StagedExePath)!;
        var script = Path.Combine(stageDir, "apply-update.cmd");
        var infoDst = Path.Combine(Path.GetDirectoryName(exe)!, "build-info.txt");
        var bat =
            "@echo off\r\n" +
            "setlocal enabledelayedexpansion\r\n" +
            "timeout /t 2 /nobreak >nul\r\n" +
            "set /a n=0\r\n" +
            ":retry\r\n" +
            "copy /y \"" + StagedExePath + "\" \"" + exe + "\" >nul 2>&1\r\n" +
            "if errorlevel 1 (\r\n" +
            "  set /a n+=1\r\n" +
            "  if !n! lss 10 (timeout /t 2 /nobreak >nul & goto retry)\r\n" +
            "  exit /b 1\r\n" +
            ")\r\n" +
            "if exist \"" + StagedInfoPath + "\" copy /y \"" + StagedInfoPath + "\" \"" + infoDst + "\" >nul 2>&1\r\n" +
            "del \"" + StagedExePath + "\" 2>nul\r\n" +
            "start \"\" \"" + exe + "\"\r\n";
        File.WriteAllText(script, bat);
        Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
