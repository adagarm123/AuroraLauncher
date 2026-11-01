using AuroraCore;
using AuroraCore.Download;
using AuroraCore.Services;

LauncherConfig.Instance.RootDirectory = @"C:/Users/94232/AppData/Roaming/AuroraLauncher";
LauncherConfig.Instance.EnsureDirectories();
var installer = new GameInstaller(new VersionResolver(new VersionManifestService()), new DownloadEngine(4));
var svc = new LauncherService();

foreach (var id in new[] { "1.21", "fabric-loader-0.20.1-1.21" })
{
    var st = installer.Evaluate(id);
    Console.WriteLine($"[{id}] Complete={st.Complete} -> {st}");
}
var insts = new InstanceManager().LoadAll();
foreach (var i in insts)
    Console.WriteLine($"instance {i.Name}: MC={i.MinecraftVersion} loader={i.LoaderType} lv={i.LoaderVersion} resolved={i.ResolvedVersionId} NeedsRepair={svc.NeedsRepair(i)}");
