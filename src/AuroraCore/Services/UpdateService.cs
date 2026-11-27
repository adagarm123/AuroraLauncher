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
    // Round-108: independently signed backup host. The primary Url is CNB
    // (fast in CN); this stays the GitHub Release URL as a reliability hedge.
    public string FallbackUrl { get; set; } = "";
    public string FallbackSignature { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string Signature { get; set; } = "";

    // ---- round-74: optional binary delta ----
    /// <summary>URL of a patch that turns round <see cref="PatchFromRound"/> into
    /// THIS build. Empty = full package only (the always-available fallback).</summary>
    public string PatchUrl { get; set; } = "";
    public string PatchSha256 { get; set; } = "";
    public long PatchSize { get; set; }
    /// <summary>The launcher round the patch applies to. Only that round may use
    /// it; everyone else downloads the full package.</summary>
    public int PatchFromRound { get; set; }
    /// <summary>RSA signature over <see cref="PatchSignedPayload"/>.</summary>
    public string PatchSignature { get; set; } = "";

    /// <summary>The exact byte string the RSA signature covers. UNCHANGED from
    /// round-72 on purpose: a client on an older build must still be able to
    /// verify (and accept) this manifest, otherwise shipping patches would lock
    /// every existing installation out of auto-update.</summary>
    public string SignedPayload => $"{Round}|{Version}|{Url}|{Sha256}|{Size}";

    /// <summary>Signed separately because appending fields to
    /// <see cref="SignedPayload"/> would break older clients. The end-to-end
    /// guarantee still comes from the produced EXE having to match
    /// <see cref="Sha256"/> — this signature only pins the patch routing.</summary>
    public string PatchSignedPayload => $"{Round}|{PatchUrl}|{PatchSha256}|{PatchSize}";

    /// <summary>Round-108: separate signature for the backup host so a
    /// tampered feed cannot redirect the fallback to an attacker's server.</summary>
    public string FallbackSignedPayload => $"{Round}|{FallbackUrl}|{Sha256}|{Size}";
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
/// root/update/ (staged, SHA-256 verified) → on the NEXT startup a VISIBLE
/// updater host (a temporary copy of the staged exe, so it does not lock the
/// file it must replace) shows a progress window while it waits for the old
/// process to exit, re-verifies the package, swaps the exe and relaunches.
/// The old hidden cmd script remains only as a fallback if the host fails.
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
        @"<RSAKeyValue><Modulus>7lVCxWC8FsfAJAUd4GaT3IP8QnoYyJkCAOmUrakrDDwKOj8vfcRq+4zuuOrOh/U3clz5lSftJL0tU4HhzTOK7uBbEFFxY6KcVe7hxA0DdCRfK6DPBA59FZ+lcxshLKcEDhsQ28hgwrEBCXr6opSZXLI2OZ2r3jOpJfWGtPftWpAJizqT/VNUgQmEFn/IciEiz4Umm2qv7CD2XT7mTmQfYECzJq1swKypBCAhLr43bcMGR+X+YQJI0WEt0OUZ/Dcpnmp8qZ1aozH93wCEXMOuT9qBylCVYOjDOwi90jG36MXOqTZ3IogOy6mcHHZOZfqufuAQqUtttoX1Y/PcQ5x4MCRbRNSMgqfzjuM0Emyd/tbeuPWRYdelgxP0Tt6gm27CEwRqzRn6YXS2S7ydDi2iBqIck9edfULEGNxrgv35Y3Dx0+iXHbWCoHKPUkwuO09tDDgfWx61bV5gtNnofSsiWl3PNal/I/ZUJqRoHY79Y3hRh5JBfG0aPNe2LsdLQwK7cEVI17sIaUoU/2XTrD+92jsvjEAeRZJfz1+jbWt6WNOr8FL5kQJ3tGPBTCXJPClAqcgJRECociPPh69aPWvbiYJzyeYcsP92GWYsoJZV3G+EIf+eaK6K3EF1quxWWpRA2QXjkwwcBbxqPY8URLp3B28VEjI6ntL7c6hFI145HN0=</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

    /// <summary>Author: set this to your hosted latest.json URL once, before
    /// publishing (any static HTTPS host works — see docs/auto-update.md).
    /// Empty = update checks disabled; LauncherConfig.UpdateFeedUrl overrides
    /// this for testing.</summary>
    public const string DefaultFeedUrl = "https://adagarm123.github.io/AuroraLauncher/latest.json";

    private readonly DownloadEngine _engine = new();
    private readonly Logger _log = Logger.Instance;

    public static string UpdateDir => Path.Combine(LauncherConfig.Instance.RootDirectory, "update");
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

    /// <summary>Verifies the patch routing signature when the manifest has one.
    /// A manifest without patch fields needs no extra check.</summary>
    public static bool VerifyPatchSignature(UpdateManifest manifest, string? publicKeyXmlOverride = null)
    {
        if (string.IsNullOrWhiteSpace(manifest.PatchUrl)) return true; // nothing claimed
        if (string.IsNullOrWhiteSpace(manifest.PatchSignature)) return false;
        var keyXml = publicKeyXmlOverride ?? PublicKeyXml;
        if (string.IsNullOrWhiteSpace(keyXml)) return false;
        try
        {
            using var rsa = RSA.Create();
            rsa.FromXmlString(keyXml);
            return rsa.VerifyData(Encoding.UTF8.GetBytes(manifest.PatchSignedPayload),
                Convert.FromBase64String(manifest.PatchSignature),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch { return false; }
    }

    /// <summary>Round-108: the backup URL is optional; a manifest without one
    /// needs no extra check, but a claimed fallback with a bad signature is
    /// rejected so it can never be used as a download target.</summary>
    public static bool VerifyFallbackSignature(UpdateManifest manifest, string? publicKeyXmlOverride = null)
    {
        if (string.IsNullOrWhiteSpace(manifest.FallbackUrl)) return true; // nothing claimed
        if (string.IsNullOrWhiteSpace(manifest.FallbackSignature)) return false;
        var keyXml = publicKeyXmlOverride ?? PublicKeyXml;
        if (string.IsNullOrWhiteSpace(keyXml)) return false;
        try
        {
            using var rsa = RSA.Create();
            rsa.FromXmlString(keyXml);
            return rsa.VerifyData(Encoding.UTF8.GetBytes(manifest.FallbackSignedPayload),
                Convert.FromBase64String(manifest.FallbackSignature),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch { return false; }
    }

    /// <summary>Is the patch usable by THIS installation? Requires: the author
    /// published one, it targets our exact round, its routing signature checks
    /// out, and it really is smaller than the package.</summary>
    public static bool ShouldUsePatch(UpdateManifest manifest, int currentRound, string? publicKeyXmlOverride = null)
    {
        if (string.IsNullOrWhiteSpace(manifest.PatchUrl)) return false;
        if (manifest.PatchFromRound != currentRound) return false;
        if (manifest.PatchSize <= 0 || manifest.Size <= 0) return false;
        if (!BinaryDelta.ShouldUsePatch(manifest.PatchSize, manifest.Size)) return false;
        return VerifyPatchSignature(manifest, publicKeyXmlOverride);
    }

    /// <summary>防降级: only a STRICTLY higher round is an update.</summary>
    public static bool IsNewer(UpdateManifest manifest, int currentRound) => manifest.Round > currentRound;

    /// <summary>Human-readable result of the LAST check — displayed in
    /// 设置 → 更新 next to the progress bar (round-58). Silent startup checks
    /// ignore it; manual 「检查更新」 always shows it.</summary>
    public string LastCheckMessage { get; private set; } = "";

    /// <summary>Fetches and verifies the feed manifest. Returns null for every
    /// negative result; <see cref="LastCheckMessage"/> explains which one.</summary>
    public async Task<UpdateManifest?> CheckAsync(CancellationToken ct = default)
    {
        // Round-87: an interrupted update leaves megabytes in update/ that nothing
        // ever removed. Every check is a free opportunity to drop anything older
        // than a day that is NOT the package waiting to be installed.
        try
        {
            UpdateStaging.Clean(UpdateDir, DateTime.UtcNow, new[] { StagedExePath, StagedInfoPath });
        }
        catch (Exception ex)
        {
            _log.Trace("Update", "清理更新暂存目录失败（忽略）: " + ex.Message);
        }

        var feed = ResolveFeedUrl();
        if (string.IsNullOrWhiteSpace(feed))
        {
            _log.Info("Update", "未配置更新源（UpdateFeedUrl），跳过检查。");
            LastCheckMessage = "未配置更新源：请在设置中填写更新源地址";
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
            LastCheckMessage = "暂时无法连接更新源，可稍后重试";
            return null;
        }

        UpdateManifest? manifest;
        try { manifest = JsonHelper.Deserialize<UpdateManifest>(json); }
        catch (Exception ex)
        {
            _log.Warn("Update", "更新清单解析失败，忽略本次检查: " + ex.Message);
            LastCheckMessage = "更新清单格式异常，请反馈给发布者";
            return null;
        }
        if (manifest == null || manifest.Round <= 0 || string.IsNullOrWhiteSpace(manifest.Url))
        {
            LastCheckMessage = "更新清单内容为空";
            return null;
        }

        if (!VerifySignature(manifest))
        {
            _log.Warn("Update", $"更新清单签名校验失败（Round {manifest.Round}）——可能被篡改或密钥不匹配，忽略本次更新。");
            LastCheckMessage = "更新清单签名校验失败，已忽略（防篡改保护）";
            return null;
        }
        if (!IsNewer(manifest, BuildInfo.CurrentRound))
        {
            _log.Info("Update", $"已运行最新版本（当前第 {BuildInfo.CurrentRound} 轮，清单第 {manifest.Round} 轮）。");
            LastCheckMessage = $"已是最新版本（第 {BuildInfo.CurrentRound} 轮）";
            return null;
        }
        _log.Info("Update", $"发现新版本：第 {manifest.Round} 轮（当前第 {BuildInfo.CurrentRound} 轮）。");
        LastCheckMessage = $"发现新版本（第 {manifest.Round} 轮）";
        return manifest;
    }

    /// <summary>Downloads the new exe into the staging folder and verifies it
    /// against the manifest SHA-256. On success the update applies on the
    /// NEXT startup (see <see cref="ApplyStagedAndRestart"/>).</summary>
    /// <summary>"增量更新（补丁 1.2 MB）" / "完整更新（62.2 MB）" — shown in 设置 →
    /// 更新 after a download so the saving is visible, not assumed.</summary>
    public string LastDownloadMode { get; private set; } = "";

    public static string PatchPath => Path.Combine(LauncherConfig.Instance.RootDirectory, "update", "AuroraLauncher.patch");
    public static string MergedPath => Path.Combine(LauncherConfig.Instance.RootDirectory, "update", "AuroraLauncher.merged.exe");

    /// <summary>Round-104: build the next update into a temp file and only
    /// replace the previously verified staged EXE after the new one passes its
    /// SHA-256 check. Otherwise a failed download destroys a ready update.</summary>
    public static string StagingTempPath => Path.Combine(LauncherConfig.Instance.RootDirectory,
        "update", "AuroraLauncher.staging.exe");

    private static void CommitStaged()
    {
        if (!File.Exists(StagingTempPath)) return;
        TryDelete(StagedExePath);
        File.Move(StagingTempPath, StagedExePath, overwrite: true);
    }

    /// <summary>Round-101: clean up after a user-cancelled download. Only the
    /// caller knows whether a *previous* ready update existed, so it decides
    /// when to call this — never delete someone else's staged build.</summary>
    public static void CancelStagedDownload()
    {
        TryDelete(PatchPath);
        TryDelete(MergedPath);
        TryDelete(StagingTempPath);
        TryDelete(StagedExePath);
        TryDelete(StagedInfoPath);
    }

    /// <summary>Round-104: cancellation must not destroy an update that was
    /// already verified and ready before this attempt started.</summary>
    public static void CancelStagingOnly()
    {
        TryDelete(PatchPath);
        TryDelete(MergedPath);
        TryDelete(StagingTempPath);
    }

    public async Task DownloadStagedAsync(UpdateManifest manifest, Action<DownloadProgress>? progress, CancellationToken ct)
    {
        PathEx.EnsureDirectory(Path.GetDirectoryName(StagedExePath)!);

        // Round-74: try the delta first. Every failure path falls through to the
        // full package — a patch is an optimisation, never the only way in.
        if (ShouldUsePatch(manifest, BuildInfo.CurrentRound) &&
            await TryDownloadAndApplyPatchAsync(manifest, progress, ct).ConfigureAwait(false))
        {
            CommitStaged();
            WriteStagedInfo(manifest);
            return;
        }

        // Round-108/109: CNB is primary (fast in CN), GitHub is the signed
        // backup. Each host gets at least 3 attempts before we move on, and
        // the UI is told which host/attempt is active instead of showing a
        // bare percentage.
        const int hostAttempts = 3;
        LastDownloadMode = $"完整更新 · 主源（{FormatMb(manifest.Size)}）";
        var fullOk = await TryDownloadFullAsync(manifest, manifest.Url, progress, ct,
            "主源 CNB", hostAttempts).ConfigureAwait(false);
        if (!fullOk && !string.IsNullOrWhiteSpace(manifest.FallbackUrl) &&
            VerifyFallbackSignature(manifest))
        {
            _log.Warn("Update", $"主源重试 {hostAttempts} 次仍失败，切换到备用源: {manifest.FallbackUrl}");
            LastDownloadMode = $"完整更新 · 备用源 GitHub（{FormatMb(manifest.Size)}）";
            fullOk = await TryDownloadFullAsync(manifest, manifest.FallbackUrl, progress, ct,
                "备用源 GitHub", hostAttempts).ConfigureAwait(false);
        }
        if (!fullOk)
            throw new IOException($"更新包下载失败（主源与备用源各重试 {hostAttempts} 次均不可用，或校验不通过）。");

        CommitStaged();
        WriteStagedInfo(manifest);
    }

    /// <summary>Round-108: downloads the full package from one host and checks
    /// its SHA-256. Returns false (instead of throwing) so the caller can try
    /// the backup host. A hash mismatch is a hard failure for BOTH hosts, so
    /// a poisoned primary can never silently fall through to an unchecked one.
    /// Round-109: <paramref name="label"/> names the host in UI status lines;
    /// <paramref name="attempts"/> is the per-host retry budget (3 for CNB).</summary>
    private async Task<bool> TryDownloadFullAsync(UpdateManifest manifest, string url,
        Action<DownloadProgress>? progress, CancellationToken ct, string label, int attempts)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        TryDelete(StagingTempPath);
        try
        {
            await _engine.DownloadAsync(new[]
            {
                new DownloadEntry
                {
                    Url = url,
                    Destination = StagingTempPath,
                    ExpectedSize = manifest.Size,
                    Required = true,
                    DisplayName = $"启动器更新包（{label}）",
                    MaxAttempts = attempts
                }
            }, p =>
            {
                // Engine retry/route messages must reach the UI verbatim —
                // otherwise "正在下载 x%" hides that we are on attempt 3 of 3.
                if (progress is null) return;
                if (!string.IsNullOrWhiteSpace(p.Message))
                    p.Message = $"{label}：{p.Message}";
                progress(p);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.Warn("Update", $"Download from {url} failed: {ex.Message}");
            TryDelete(StagingTempPath);
            return false;
        }
        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);

        // Round-106: stream-hash instead of buffering ~63 MB in memory.
        var actual = await Task.Run(() => Sha256OfFile(StagingTempPath), ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            TryDelete(StagingTempPath);
            throw new IOException("更新清单缺少 SHA-256，拒绝安装未校验的更新包。");
        }
        if (!string.Equals(actual, manifest.Sha256.Trim().Replace("-", ""), StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(StagingTempPath);
            _log.Warn("Update", $"SHA-256 mismatch from {url} ({actual} != {manifest.Sha256})");
            return false;
        }
        return true;
    }

    /// <summary>Downloads the patch, merges it with the RUNNING exe and only
    /// accepts the result when its SHA-256 equals the signed manifest hash.
    /// Any problem (wrong base build, truncated patch, merge error) returns
    /// false so the caller can fall back to the full package.</summary>
    private async Task<bool> TryDownloadAndApplyPatchAsync(UpdateManifest manifest, Action<DownloadProgress>? progress, CancellationToken ct)
    {
        var patchPath = PatchPath;
        var mergedPath = MergedPath;
        try
        {
            TryDelete(patchPath);
            TryDelete(mergedPath);

            // Round-109: same 3-attempt budget and honest host label as the
            // full package; a failed patch then falls through to the full
            // package (which has its own signed CNB->GitHub chain).
            await _engine.DownloadAsync(new[]
            {
                new DownloadEntry
                {
                    Url = manifest.PatchUrl,
                    Destination = patchPath,
                    ExpectedSize = manifest.PatchSize,
                    Required = true,
                    DisplayName = "增量更新补丁",
                    MaxAttempts = 3
                }
            }, p =>
            {
                if (progress is null) return;
                if (!string.IsNullOrWhiteSpace(p.Message))
                    p.Message = $"增量补丁：{p.Message}";
                progress(p);
            }, ct).ConfigureAwait(false);

            var patchHash = await Task.Run(() => Sha256OfFile(patchPath), ct).ConfigureAwait(false);
            // Round-106 (L1): fail closed on a missing hash too — an unchecked
            // delta applied to the running EXE is worse than an unchecked full
            // package. Fall back to the full download below.
            if (string.IsNullOrWhiteSpace(manifest.PatchSha256) ||
                !string.Equals(patchHash, manifest.PatchSha256.Trim().Replace("-", ""), StringComparison.OrdinalIgnoreCase))
                throw new IOException("补丁文件校验失败（SHA-256 缺失或不匹配）。");

            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
                throw new IOException("无法定位当前启动器文件，无法合并补丁。");

            // Merge on a worker thread: it reads ~63 MB and hashes ~63 MB, which
            // is tens of milliseconds of pure CPU the UI thread should not pay.
            await Task.Run(() =>
            {
                BinaryDelta.Apply(currentExe, patchPath, mergedPath);
                var mergedHash = Sha256OfFile(mergedPath);
                if (string.IsNullOrWhiteSpace(manifest.Sha256) ||
                    !string.Equals(mergedHash, manifest.Sha256.Trim().Replace("-", ""), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("合并结果校验失败（清单 SHA-256 缺失或不一致）。");
                File.Move(mergedPath, StagingTempPath, overwrite: true);
            }, ct).ConfigureAwait(false);

            LastDownloadMode = $"增量更新（补丁 {FormatMb(manifest.PatchSize)}，比完整包 {FormatMb(manifest.Size)} 少下 " +
                               $"{Math.Max(0, 100 - (int)Math.Round(manifest.PatchSize * 100.0 / Math.Max(1, manifest.Size)))}%）";
            _log.Info("Update", $"第 {manifest.Round} 轮已用增量补丁合并完成（{manifest.PatchSize:n0} B），SHA-256 与清单一致。");
            TryDelete(patchPath);
            return true;
        }
        catch (OperationCanceledException)
        {
            TryDelete(patchPath);
            TryDelete(mergedPath);
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn("Update", "增量更新不可用，回退到完整包: " + ex.Message);
            TryDelete(patchPath);
            TryDelete(mergedPath);
            return false;
        }
    }

    private static string Sha256OfFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string FormatMb(long bytes) => bytes > 0 ? $"{bytes / (1024.0 * 1024.0):0.0} MB" : "未知大小";

    private void WriteStagedInfo(UpdateManifest manifest)
    {
        File.WriteAllText(StagedInfoPath,
            "build-time : " + manifest.ReleaseTime + "\r\n" +
            "git-commit : (see release notes)\r\n" +
            "exe-sha256 : " + manifest.Sha256 + "\r\n" +
            "round      : " + manifest.Round + "\r\n" +
            "notes      : " + manifest.Notes.Replace("\r", " ").Replace("\n", " ") + "\r\n");
        _log.Info("Update", $"第 {manifest.Round} 轮更新包已下载并校验通过，下次启动时自动换装。");
    }

    /// <summary>Temporary copy the updater host runs from. A running exe is
    /// locked on Windows, so the host must NOT be the file it replaces.</summary>
    public static string HostExePath => Path.Combine(LauncherConfig.Instance.RootDirectory, "update", "updater.exe");

    /// <summary>Round-61: launches a VISIBLE updater host and returns; the
    /// caller exits right away. The host shows a borderless progress window
    /// for the whole wait → verify → copy → relaunch sequence, so a 63 MB
    /// swap no longer looks like a crash. Falls back to the old hidden cmd
    /// script only when the host cannot be started at all.</summary>
    public static void ApplyStagedAndRestart()
    {
        if (!HasStagedUpdate()) return;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        var plan = new UpdateApplyPlan
        {
            OldPid = Environment.ProcessId,
            TargetExe = exe,
            StagedExe = StagedExePath,
            StagedInfo = StagedInfoPath
        };
        try
        {
            TryDelete(HostExePath);
            File.Copy(StagedExePath, HostExePath, overwrite: true);
            var psi = new ProcessStartInfo(HostExePath) { UseShellExecute = false };
            psi.ArgumentList.Add(UpdateApplyArgs.Build(plan));
            Process.Start(psi);
            Logger.Instance.Info("Update", "已启动可见的更新器窗口，等待旧进程退出后换装。");
            return;
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn("Update", "可见更新器启动失败，回退到隐藏脚本: " + ex.Message);
            ApplyStagedAndRestartLegacy(plan);
        }
    }

    /// <summary>Best-effort sweep of last run's updater copy on a normal
    /// startup (it can no longer be locked once the host has exited).</summary>
    public static void CleanupUpdaterHost()
    {
        TryDelete(HostExePath);
    }

    private static void ApplyStagedAndRestartLegacy(UpdateApplyPlan plan)
    {
        var stageDir = Path.GetDirectoryName(plan.StagedExe)!;
        var script = Path.Combine(stageDir, "apply-update.cmd");
        var infoDst = Path.Combine(Path.GetDirectoryName(plan.TargetExe)!, "build-info.txt");
        var bat =
            "@echo off\r\n" +
            "setlocal enabledelayedexpansion\r\n" +
            "timeout /t 2 /nobreak >nul\r\n" +
            "set /a n=0\r\n" +
            ":retry\r\n" +
            "copy /y \"" + plan.StagedExe + "\" \"" + plan.TargetExe + "\" >nul 2>&1\r\n" +
            "if errorlevel 1 (\r\n" +
            "  set /a n+=1\r\n" +
            "  if !n! lss 10 (timeout /t 2 /nobreak >nul & goto retry)\r\n" +
            "  exit /b 1\r\n" +
            ")\r\n" +
            "if exist \"" + plan.StagedInfo + "\" copy /y \"" + plan.StagedInfo + "\" \"" + infoDst + "\" >nul 2>&1\r\n" +
            "del \"" + plan.StagedExe + "\" 2>nul\r\n" +
            "start \"\" \"" + plan.TargetExe + "\"\r\n";
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
