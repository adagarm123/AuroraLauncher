using System.Diagnostics;
using System.Text.Json;
using AuroraCore;
using AuroraCore.Download;
using AuroraCore.Services;
using AuroraCore.Utils;

namespace AuroraSelfTest;

/// <summary>
/// Round-94 opt-in end-to-end check (`--forge-install-smoke`): run the REAL
/// Forge installer through our own `ModLoaderInstaller` against an isolated
/// root, and assert it finishes green.
///
/// This is the bug the user hit: the installer exits 1 with
/// "There is no Minecraft launcher profile in …, you need to run the launcher
/// first!" because our data root has no <c>launcher_profiles.json</c>. The unit
/// test proves the file gets written; this proves the whole install path —
/// argument building, profile file, installer run and version detection —
/// actually produces <c>versions/1.20.1-forge-47.4.22</c>.
///
/// Needs network (~9 MB installer + its own library downloads) and a JDK, hence
/// opt-in and never part of CI.
/// </summary>
internal static class ForgeInstallSmoke
{
    public static async Task<int> Run()
    {
        const string mc = "1.20.1";
        const string loader = "47.4.22";
        var root = Path.Combine(Path.GetTempPath(), "aurora-forge-install-" + Guid.NewGuid().ToString("N")[..6]);
        var clock = Stopwatch.StartNew();
        try
        {
            // Isolate BEFORE the installer type is touched: its cache dir is a
            // static field resolved from the config root.
            LauncherConfig.UseTestRoot(root);
            LauncherConfig.Instance.EnsureDirectories();
            Logger.Instance.Configure(root);

            // 1) The installer patches the vanilla version, so it needs its JSON.
            var versionDir = Path.Combine(root, "versions", mc);
            Directory.CreateDirectory(versionDir);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            var manifestJson = await http.GetStringAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
            string? versionUrl = null;
            using (var doc = JsonDocument.Parse(manifestJson))
                foreach (var v in doc.RootElement.GetProperty("versions").EnumerateArray())
                    if (v.GetProperty("id").GetString() == mc) { versionUrl = v.GetProperty("url").GetString(); break; }
            if (versionUrl == null)
            {
                Console.WriteLine($"[forge-install] FAIL: 版本清单里找不到 {mc}");
                return 1;
            }
            File.WriteAllText(Path.Combine(versionDir, mc + ".json"), await http.GetStringAsync(versionUrl));

            // 2) A JDK that can run the installer.
            var javas = await new JavaFinder().FindAllAsync();
            var java = javas.OrderByDescending(j => j.Major).FirstOrDefault();
            if (java == null)
            {
                Console.WriteLine("[forge-install] SKIP: 本机没有可用的 Java");
                return 0;
            }
            Console.WriteLine($"[forge-install] root={root}");
            Console.WriteLine($"[forge-install] Java {java.Major} ({java.Vendor}) @ {java.JavaExecutable}");

            // 3) The real thing.
            var installer = new ModLoaderInstaller(new DownloadEngine());
            var result = await installer.InstallAsync(ModLoaderType.Forge, mc, loader, root, java,
                line => Console.WriteLine("[forge-install]   " + line));

            var fails = new List<string>();
            var profilePath = Path.Combine(root, LauncherProfiles.FileName);
            if (!File.Exists(profilePath)) fails.Add("launcher_profiles.json 没有被补出来");
            else if (!LauncherProfiles.HasProfiles(File.ReadAllText(profilePath)))
                fails.Add("补出来的 launcher_profiles.json 不合法");

            if (!result.Success) fails.Add("安装失败：" + result.Error);
            if (string.IsNullOrEmpty(result.VersionId) || !result.VersionId.Contains(loader))
                fails.Add($"版本目录检测错了：{result.VersionId}");
            else if (!File.Exists(Path.Combine(root, "versions", result.VersionId, result.VersionId + ".json")))
                fails.Add("声称装好了，但版本 JSON 不存在：" + result.VersionId);

            clock.Stop();
            if (fails.Count == 0)
            {
                Console.WriteLine($"[forge-install] SMOKE OK：{result.VersionId} 安装成功（{clock.Elapsed.TotalSeconds:0} 秒）");
                Console.WriteLine($"[forge-install] launcher_profiles.json 已自动补上：{profilePath}");
            }
            else
            {
                Console.WriteLine($"[forge-install] FAIL（{clock.Elapsed.TotalSeconds:0} 秒）：");
                foreach (var f in fails) Console.WriteLine("  - " + f);
            }
            return fails.Count == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[forge-install] EXCEPTION: " + ex);
            return 2;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
