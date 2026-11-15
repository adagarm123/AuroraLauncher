using System.Windows;
using System.Windows.Threading;
using AuroraCore;
using AuroraCore.Models;
using AuroraCore.Services;
using AuroraCore.Utils;
using AuroraLauncher.Desktop.ViewModels;

namespace AuroraSelfTest;

/// <summary>
/// Round-92 (opt-in, `--launch-gate-smoke`): drives the REAL launch path of a
/// modpack instance whose mods never downloaded, in an isolated data root, and
/// asserts that the launcher refuses to start a vanilla client instead.
///
/// This is the bug the user hit: the instance looked complete (all vanilla
/// files present), so the old code printed「文件完整，直接启动」and the game
/// started with no loader and no mods. Everything here is offline — the fake
/// version has no libraries, so no network call is made.
/// </summary>
internal static class LaunchGateSmoke
{
    public static int Run()
    {
        var code = 1;
        // The view model needs a WPF Application + an STA thread (same bootstrap
        // as the other UI smokes).
        var thread = new Thread(() =>
        {
            try { code = RunCore(); }
            catch (Exception ex) { Console.WriteLine("[launch-gate-smoke] EXCEPTION: " + ex); code = 2; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return code;
    }

    /// <summary>Read-only diagnostic (`--readiness-scan`): prints what the launch
    /// gate thinks of every instance in the REAL data root. Starts nothing,
    /// repairs nothing — this is how the user's own broken instance gets checked.</summary>
    public static int ScanRealRootForDiagnostics()
    {
        var svc = new LauncherService();
        var instances = svc.Instances.LoadAll();
        Console.WriteLine($"[readiness-scan] 数据根: {LauncherConfig.Instance.RootDirectory} · {instances.Count} 个实例");
        foreach (var inst in instances)
        {
            var gate = svc.CheckReadiness(inst);
            var mods = ModpackIntegrity.CountModJars(inst.Directory);
            var resolved = inst.ResolvedVersionId ?? "(null)";
            Console.WriteLine($"[readiness-scan] {inst.Name,-22} mc={inst.MinecraftVersion,-9} " +
                              $"loader={inst.LoaderType,-8} resolved={resolved,-34} " +
                              $"mods={mods,-3} pack={inst.IsModpack,-5} → " +
                              (gate.Blocks ? $"拦住【{gate.Blocker}】{gate.Headline}" : "可以启动"));
            if (gate.Blocks) Console.WriteLine($"[readiness-scan]     {gate.Detail}");
        }
        return 0;
    }

    private static int RunCore()
    {        var root = Path.Combine(Path.GetTempPath(), "aurora-gate-smoke-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root);
        LauncherConfig.UseTestRoot(root);
        LauncherConfig.Instance.EnsureDirectories();
        Logger.Instance.Configure(root);

        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        var fails = new List<string>();
        void Require(bool ok, string message) { if (!ok) fails.Add(message); }

        const string mc = "92.0.2";
        var vdir = Path.Combine(LauncherConfig.Instance.VersionsDir, mc);
        Directory.CreateDirectory(Path.Combine(vdir, "natives"));
        File.WriteAllText(Path.Combine(vdir, mc + ".json"),
            "{\"id\":\"" + mc + "\",\"mainClass\":\"net.minecraft.client.main.Main\",\"libraries\":[]}");
        File.WriteAllBytes(Path.Combine(vdir, mc + ".jar"), new byte[8192]);
        File.WriteAllBytes(Path.Combine(vdir, "natives", "lwjgl.dll"), new byte[64]);

        MainViewModel? vm = null;
        try
        {
            var svc = new LauncherService();
            var inst = svc.Instances.Create("BattleArmory TACZ", mc, ModLoaderType.Vanilla);
            inst.IsModpack = true;
            inst.ExpectedModCount = 87;      // the pack declared 87 mods
            inst.ModpackName = "BattleArmory";
            inst.ModpackSourcePath = Path.Combine(root, "gone.mrpack"); // no longer on disk
            svc.Instances.Update(inst);

            vm = new MainViewModel();
            vm.LaunchGateAnswerForTests = MainViewModel.LaunchGateAnswer.Cancel;
            vm.Instances.Clear();
            foreach (var i in svc.Instances.LoadAll()) vm.Instances.Add(i);
            vm.SelectedInstance = vm.Instances.FirstOrDefault(i => i.Id == inst.Id);
            vm.OfflineName = "Tester";
            vm.AddOfflineAccount();

            // Sanity: the readiness layer sees the gap.
            var gate = svc.CheckReadiness(inst);
            Require(gate.Blocks, "闸门没有识别出「整合包 0 个模组」");
            Require(gate.Blocker == LaunchReadiness.Blocker.ModpackEmpty,
                $"应报 ModpackEmpty，实际 {gate.Blocker}");
            Require(gate.Detail.Contains("87"), "提示里要有应有数量：" + gate.Detail);

            // The real thing: click 启动游戏.
            var task = vm.LaunchAsync();
            var spin = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
            var deadline = DateTime.UtcNow.AddSeconds(30);
            timer.Tick += (_, _) =>
            {
                if (task.IsCompleted || DateTime.UtcNow > deadline) { timer.Stop(); spin.Continue = false; }
            };
            timer.Start();
            Dispatcher.PushFrame(spin);
            if (!task.IsCompleted)
            {
                fails.Add("启动流程 30 秒内没有结束（可能真的在启动游戏）");
            }
            else
            {
                // Surfaces the exception instead of swallowing it.
                task.GetAwaiter().GetResult();
            }

            Require(vm.Status.Contains("未启动") || vm.Status.Contains("整合包"),
                "状态栏必须说明被拦住了，实际：" + vm.Status);
            Require(!vm.Status.Contains("正在启动"), "不能进入「正在启动」状态：" + vm.Status);

            // No process may have been started: the command line is only logged
            // by GameLauncher right before Process.Start.
            var logPath = Directory.EnumerateFiles(Path.Combine(root, "logs"), "*.log").OrderByDescending(f => f).FirstOrDefault();
            var logText = logPath != null ? File.ReadAllText(logPath) : "";
            Require(!logText.Contains("Launching:"), "闸门没拦住在日志里：出现了 Launching: 命令行");
            // The gate writes through AddLog (in-app log page) AND Logger (file);
            // the file write is buffered, so assert on the in-app list which is
            // what the user sees.
            Require(vm.Logs.Any(l => l.Contains("启动闸门") || l.Contains("ModpackEmpty") || l.Contains(gate.Headline)),
                "闸门结论必须写进日志页，供日后排查");

            if (fails.Count == 0)
            {
                Console.WriteLine("[launch-gate-smoke] SMOKE OK：整合包 0 个模组 → 拦住启动，未生成任何 Java 命令行");
                Console.WriteLine($"[launch-gate-smoke] 状态栏：{vm.Status}");
            }
            else
            {
                Console.WriteLine($"[launch-gate-smoke] 失败 {fails.Count} 项：");
                foreach (var f in fails) Console.WriteLine("  - " + f);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[launch-gate-smoke] EXCEPTION: " + ex);
            return 1;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
        return fails.Count == 0 ? 0 : 1;
    }
}
