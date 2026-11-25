using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AuroraCore;
using AuroraCore.Download;
using AuroraCore.Services;
using AuroraCore.Utils;

namespace AuroraSelfTest;

/// <summary>
/// Round-86 opt-in network check: does the Forge installer URL we now build
/// actually exist, on BOTH routes? The reported bug was a 404 from a path that
/// said "forge/47.4.22" instead of "forge/1.20.1-47.4.22"; a pure unit test can
/// only check the string shape, so this downloads the real file (≈6 MB) through
/// our own DownloadEngine and verifies it is a ZIP.
///
/// Invoked ONLY with --forge-url-check (network + 6 MB). Not part of CI.
/// </summary>
internal static class ForgeUrlCheck
{
    public static async Task<int> Run()
    {
        var failures = new List<string>();
        var root = Path.Combine(Path.GetTempPath(), "aurora-forge-check-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root);
        try
        {
            const string mavenBase = "https://maven.minecraftforge.net/net/minecraftforge/forge";
            var mavenVersion = ModLoaderInstaller.ForgeMavenVersion("1.20.1", "47.4.22");
            var (official, file) = ModLoaderInstaller.BuildMavenInstallerUrl(mavenBase, "forge", mavenVersion);
            var mirror = LauncherConfig.ToBmclapiUrl(official);

            Console.WriteLine($"[forge-check] 版本拼接: 47.4.22 + 1.20.1 → {mavenVersion}");
            Console.WriteLine($"[forge-check] 官方: {official}");
            Console.WriteLine($"[forge-check] 镜像: {mirror}");

            // The URL from the bug report must be recognized as wrong.
            var reported = "https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge/47.4.22/forge-47.4.22-installer.jar";
            if (string.Equals(mirror, reported, StringComparison.OrdinalIgnoreCase))
                failures.Add("仍然生成了报告里那个 404 地址");

            var engine = new DownloadEngine();
            foreach (var (label, url) in new[] { ("官方", official), ("镜像", mirror) })
            {
                var dest = Path.Combine(root, $"{label}-{file}");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    await engine.DownloadAsync(new[]
                    {
                        new DownloadEntry { Url = url, Destination = dest, DisplayName = file, Category = "installer" }
                    }, null, CancellationToken.None).ConfigureAwait(false);
                    sw.Stop();
                    var length = new FileInfo(dest).Length;
                    var head = new byte[2];
                    using (var fs = File.OpenRead(dest))
                    {
                        var read = 0;
                        while (read < head.Length)
                        {
                            var n = fs.Read(head, read, head.Length - read);
                            if (n == 0) break;
                            read += n;
                        }
                    }
                    var isZip = head[0] == 0x50 && head[1] == 0x4B; // "PK"
                    Console.WriteLine($"[forge-check] {label}: OK {length / 1024} KB, {sw.ElapsedMilliseconds} ms, ZIP={isZip}");
                    if (length < 100_000) failures.Add($"{label} 下载的文件太小（{length} 字节），不像安装器");
                    if (!isZip) failures.Add($"{label} 下载的内容不是 ZIP（可能被换成了错误页）");
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    Console.WriteLine($"[forge-check] {label}: FAIL {ex.Message}");
                    failures.Add($"{label}: {ex.Message}");
                }
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        if (failures.Count == 0)
        {
            Console.WriteLine("[forge-check] OK - 两条线路都能拿到安装器（不再是 404）");
            // Round-86: also prove the NEXT step works (installer actually runs
            // and produces a version folder) — that is the whole point of the
            // fix. Opt-in because it downloads + executes a JVM.
            if (Environment.GetEnvironmentVariable("AURORA_FORGE_FULL") == "1")
                await RunFullInstallAsync(failures).ConfigureAwait(false);
        }
        if (failures.Count == 0)
        {
            Console.WriteLine("[forge-check] ALL OK");
            return 0;
        }
        foreach (var f in failures) Console.WriteLine("[forge-check] FAIL: " + f);
        return 1;
    }

    /// <summary>End-to-end: Forge 1.20.1 + 47.4.22 into an isolated root, the way
    /// a modpack import does it. Downloads the installer, runs it with a real
    /// JDK, then asserts a "1.20.1-forge-…" version directory exists.</summary>
    private static async Task RunFullInstallAsync(List<string> failures)
    {
        var root = Path.Combine(Path.GetTempPath(), "aurora-forge-install-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root);
        LauncherConfig.UseTestRoot(root);
        try
        {
            var javas = await new JavaFinder().FindAllAsync().ConfigureAwait(false);
            var java = javas.Where(j => j.Major >= 17).OrderByDescending(j => j.Major).FirstOrDefault();
            if (java == null) { failures.Add("本机没有 Java 17+，无法执行安装器"); return; }
            Console.WriteLine($"[forge-check] 使用 Java {java.Major}: {java.JavaExecutable}");

            var installer = new ModLoaderInstaller(new DownloadEngine());
            var result = await installer.InstallAsync(ModLoaderType.Forge, "1.20.1", "47.4.22",
                root, java, line => Console.WriteLine("  " + line)).ConfigureAwait(false);

            Console.WriteLine($"[forge-check] 安装结果: Success={result.Success} VersionId={result.VersionId} " +
                              $"Loader={result.LoaderVersion} Error={result.Error}");
            if (!result.Success) failures.Add("Forge 安装器执行失败: " + result.Error);
            if (result.VersionId != null)
            {
                var dir = Path.Combine(root, "versions", result.VersionId);
                Console.WriteLine($"[forge-check] 版本目录: {dir} exists={Directory.Exists(dir)}");
                if (!Directory.Exists(dir)) failures.Add("安装器跑完了但没有生成版本目录");
            }
        }
        catch (Exception ex)
        {
            failures.Add("完整安装异常: " + ex.Message);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
