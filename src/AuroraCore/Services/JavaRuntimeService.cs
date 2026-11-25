using System.IO.Compression;
using System.Security.Cryptography;
using AuroraCore.Download;
using AuroraCore.Utils;

namespace AuroraCore.Services;

/// <summary>Metadata of one downloadable JDK package from the Adoptium API.</summary>
public sealed record JavaPackage(
    string Name, string Url, string Sha256, long Size, string ReleaseName, int Major)
{
    /// <summary>Round-98: "Temurin" or "Zulu" — used only for display/logging,
    /// never to decide whether a package may be installed.</summary>
    public string Vendor { get; init; } = "Temurin";
}

/// <summary>Result of a one-click Java install: the java.exe that is now the
/// launcher's default runtime.</summary>
public sealed record JavaInstallResult(string JavaExecutable, bool AlreadyInstalled);

/// <summary>
/// One-click Java 21 installation (round-59). Uses the EXISTING building
/// blocks — NetRouting for the Adoptium JSON API, DownloadEngine for the
/// archive (Range resume / bounded retries / idle timeout) and SHA-256 for
/// integrity — then silently extracts into the launcher's OWN data directory
/// and returns the java.exe to use as the default. No system environment
/// variables, no Program Files writes, no installer UI.
/// </summary>
public sealed class JavaRuntimeService
{
    public const int DefaultMajor = 21;

    /// <summary>Adoptium Temurin GA, Windows x64 JDK zip, latest build of the
    /// requested major (round-78: the crash repair engine may need 17, not
    /// just 21 — the URL shape is identical apart from the major).</summary>
    public static string BuildApiUrl(int major) =>
        $"https://api.adoptium.net/v3/assets/latest/{Math.Max(8, major)}/hotspot" +
        "?architecture=x64&image_type=jdk&os=windows&vendor=eclipse";

    /// <summary>Adoptium Temurin 21 GA, Windows x64 JDK zip.</summary>
    public static string ApiUrl => BuildApiUrl(DefaultMajor);

    private readonly DownloadEngine _engine = new();
    private readonly Logger _log = Logger.Instance;

    public static string JavaRoot => LauncherConfig.Instance.JavaRuntimesDir;

    /// <summary>PURE parse of the Adoptium "latest assets" response — no IO,
    /// unit-testable. Fails closed: requires a .zip package, an HTTPS link and
    /// a 64-char SHA-256 checksum, so a tampered/odd manifest cannot make the
    /// launcher download an arbitrary URL or skip verification.</summary>
    public static JavaPackage? ParseLatest(string json, int major = DefaultMajor)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        foreach (var asset in doc.RootElement.EnumerateArray())
        {
            if (!asset.TryGetProperty("binary", out var bin) ||
                !bin.TryGetProperty("package", out var pkg)) continue;

            var name = Str(pkg, "name");
            var link = Str(pkg, "link");
            var checksum = Str(pkg, "checksum");
            if (name == null || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            if (link == null || !Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps) continue;
            if (checksum == null || checksum.Trim().Length != 64) continue;

            long size = 0;
            if (pkg.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number)
                size = sz.GetInt64();
            var release = Str(asset, "release_name") ?? $"jdk-{major}";
            var parsedMajor = major;
            if (asset.TryGetProperty("version", out var ver) &&
                ver.TryGetProperty("major", out var mj) && mj.TryGetInt32(out var m))
                parsedMajor = m;
            return new JavaPackage(name, link, checksum.Trim(), size, release, parsedMajor);
        }
        return null;
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>PURE zip-slip guard — delegates to the shared PathEx helper so
    /// Java / natives / mrpack extraction all enforce the same rule.</summary>
    public static bool IsSafeEntryPath(string fullName) => PathEx.IsSafeEntryPath(fullName);

    /// <summary>Resolves the latest package via NetRouting (proxy + direct
    /// hedge, 8s per route). No mirror is hard-coded: Adoptium's response
    /// itself carries the authoritative download link.</summary>
    public async Task<JavaPackage?> ResolveAsync(CancellationToken ct = default)
        => await ResolveAsync(DefaultMajor, ct).ConfigureAwait(false);

    /// <summary>Same as above for an explicit major (crash repair: the log
    /// told us exactly which Java the game wants).</summary>
    public async Task<JavaPackage?> ResolveAsync(int major, CancellationToken ct = default)
        => await ResolveAsync(JavaDistribution.Temurin, major, ct).ConfigureAwait(false);

    /// <summary>Round-98: Temurin (Adoptium API) or Zulu (Azul community API +
    /// CDN). Both go through the same SHA-256 → extract → set-default path.</summary>
    public async Task<JavaPackage?> ResolveAsync(JavaDistribution distribution, int major,
        CancellationToken ct = default)
    {
        if (distribution == JavaDistribution.Zulu)
        {
            var zuluJson = await NetRouting.GetStringAsync(
                JavaDistributionPolicy.BuildZuluApiUrl(major), null, ct,
                perRouteTimeoutSec: 8, rounds: 1, logTag: "Java").ConfigureAwait(false);
            var zulu = JavaDistributionPolicy.ParseZuluLatest(zuluJson, major);
            if (zulu == null) _log.Warn("Java", "Azul Zulu 响应中没有可用的 Windows x64 普通 JDK zip 包。");
            else _log.Info("Java", $"Zulu: {zulu.ReleaseName} · {zulu.Name} · {zulu.Size / 1024 / 1024} MB");
            return zulu;
        }

        var json = await NetRouting.GetStringAsync(BuildApiUrl(major), null, ct,
            perRouteTimeoutSec: 8, rounds: 1, logTag: "Java").ConfigureAwait(false);
        var pkg = ParseLatest(json, major);
        if (pkg == null) _log.Warn("Java", "Adoptium 响应中没有可用的 Windows x64 JDK zip 包。");
        else _log.Info("Java", $"Temurin: {pkg.ReleaseName} · {pkg.Name} · {pkg.Size / 1024 / 1024} MB");
        return pkg;
    }

    /// <summary>If a valid Java 21 already sits in our own java/ folder, return
    /// it — no network needed (offline / API-down resilience).</summary>
    public string? FindAlreadyInstalled()
    {
        if (!Directory.Exists(JavaRoot)) return null;
        foreach (var dir in Directory.EnumerateDirectories(JavaRoot).OrderByDescending(d => d))
        {
            var exe = FindJavaExe(dir);
            if (exe != null) return exe;
        }
        return null;
    }

    /// <summary>Downloads (background), verifies SHA-256, silently extracts
    /// and returns the java.exe path. Idempotent: an existing valid install is
    /// reused. Throws with a clear message on every failure path — never
    /// installs an unverified archive.</summary>
    public async Task<JavaInstallResult> InstallAsync(JavaPackage pkg,
        Action<DownloadProgress>? progress, CancellationToken ct = default)
    {
        PathEx.EnsureDirectory(JavaRoot);
        var dirName = SanitizeDir(pkg.ReleaseName);
        var target = Path.Combine(JavaRoot, dirName);

        var existing = FindJavaExe(target);
        if (existing != null)
        {
            _log.Info("Java", $"Java {pkg.Major} 已安装，直接设为默认：{existing}");
            return new JavaInstallResult(existing, true);
        }
        if (Directory.Exists(target))
            throw new IOException($"目标目录已存在但不完整：{target}。请删除该目录后重试（启动器不会自动删除已有目录）。");

        var zipPath = Path.Combine(JavaRoot, pkg.Name);
        var staging = Path.Combine(JavaRoot, ".extracting-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await DownloadVerifiedAsync(pkg, zipPath, progress, ct).ConfigureAwait(false);
            // 205 MB / thousands of entries — keep blocking IO off the UI
            // thread (this service is awaited directly by the WPF command).
            await Task.Run(() => ExtractZipSafe(zipPath, staging), ct).ConfigureAwait(false);
            if (FindJavaExe(staging) == null)
                throw new IOException("下载包中未找到 bin\\java.exe，可能不是有效的 JDK 压缩包。");
            Directory.Move(staging, target);
            var finalExe = FindJavaExe(target)
                ?? throw new IOException("解压后未找到 bin\\java.exe。");
            _log.Info("Java", $"Java {pkg.Major} 安装完成：{finalExe}");
            return new JavaInstallResult(finalExe, false);
        }
        finally
        {
            // Only files WE created are cleaned up — never user data.
            TryDelete(zipPath);
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        }
    }

    private async Task DownloadVerifiedAsync(JavaPackage pkg, string zipPath,
        Action<DownloadProgress>? progress, CancellationToken ct)
    {
        using var slowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watcher = new SlowTransferWatcher();
        var stalled = false;

        void Watch(DownloadProgress p)
        {
            if (!stalled && watcher.OnSample(p.SpeedBytesPerSec))
            {
                stalled = true;
                p.Message = "下载线路过慢，请在设置中配置代理后重试";
                slowCts.Cancel();
            }
            progress?.Invoke(p);
        }

        try
        {
            await _engine.DownloadAsync(new[]
            {
                new DownloadEntry
                {
                    Url = pkg.Url,
                    Destination = zipPath,
                    ExpectedSize = pkg.Size > 0 ? pkg.Size : null,
                    Required = true,
                    DisplayName = $"Java {pkg.Major} ({pkg.Vendor} JDK)",
                    Category = "java"
                }
            }, Watch, slowCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && stalled)
        {
            throw new IOException("Java 下载线路持续过慢（聚合速度低于阈值约 20 秒），已中止；可在设置中配置代理后重试。");
        }

        var actual = await Task.Run(() =>
        {
            using var fs = File.OpenRead(zipPath);
            return Convert.ToHexString(SHA256.HashData(fs));
        }, ct).ConfigureAwait(false);
        if (!string.Equals(actual, pkg.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Java 安装包 SHA-256 校验失败（期望 {pkg.Sha256[..12]}…，实际 {actual[..12]}…），已删除下载文件。");
        _log.Info("Java", "Java 安装包 SHA-256 校验通过。");
    }

    private void ExtractZipSafe(string zipPath, string destDir)
    {
        PathEx.EnsureDirectory(destDir);
        var fullDest = Path.GetFullPath(destDir);
        var prefix = fullDest + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (!IsSafeEntryPath(entry.FullName))
            {
                _log.Warn("Java", $"跳过不安全的压缩包条目：{entry.FullName}");
                continue;
            }
            var rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var dest = Path.GetFullPath(Path.Combine(fullDest, rel));
            if (!dest.Equals(fullDest, StringComparison.OrdinalIgnoreCase) &&
                !dest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (entry.FullName.EndsWith('/'))
            {
                PathEx.EnsureDirectory(dest);
                continue;
            }
            PathEx.EnsureDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, true);
        }
    }

    private static string? FindJavaExe(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        var direct = Path.Combine(dir, "bin", "java.exe");
        if (File.Exists(direct)) return direct;
        // Adoptium zips contain ONE root folder (jdk-21.0.x+y).
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var candidate = Path.Combine(sub, "bin", "java.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { }
        return null;
    }

    private static string SanitizeDir(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
