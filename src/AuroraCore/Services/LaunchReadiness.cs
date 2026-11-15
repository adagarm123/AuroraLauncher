namespace AuroraCore.Services;

/// <summary>
/// Round-92: why a click on「启动游戏」must not turn into a launch, decided in
/// one pure function so the dialog, the log line and the tests can never
/// disagree.
///
/// The case that motivated it: an instance that says「Forge 1.20.1」but whose
/// <c>ResolvedVersionId</c> is empty (the loader never installed). Every old
/// check asked "are the *vanilla* files complete?" — yes — so the launcher
/// started a plain vanilla client and the user played the wrong game.
/// </summary>
public static class LaunchReadiness
{
    public enum Blocker
    {
        None,
        /// <summary>Vanilla files (jar / libraries / version JSON) incomplete.</summary>
        CoreFilesMissing,
        /// <summary>Loader instance with no loader version installed (or an id that is just the vanilla one).</summary>
        LoaderNotInstalled,
        /// <summary>Modpack instance whose mods/ folder is empty.</summary>
        ModpackEmpty,
        /// <summary>Modpack instance missing a large share of its mods.</summary>
        ModpackPartial
    }

    public sealed record Input
    {
        public ModLoaderType LoaderType { get; init; }
        public string MinecraftVersion { get; init; } = "";
        public string? ResolvedVersionId { get; init; }
        public bool LoaderProfileExists { get; init; }
        public bool CoreComplete { get; init; } = true;
        public ModpackIntegrity.Scan Modpack { get; init; } = new(ModpackIntegrity.State.NotModpack, 0, 0, 0);
        /// <summary>False when neither the .mrpack nor the manifest survived, so
        /// "补全" cannot be offered — the user has to re-download the pack.</summary>
        public bool CanRepairModpack { get; init; }
        public string InstanceName { get; init; } = "";
    }

    public sealed record Result(Blocker Blocker, string Headline, string Detail, bool CanOverride, bool CanRepair)
    {
        public bool Blocks => Blocker != Blocker.None;
    }

    public static readonly Result Ready = new(Blocker.None, "", "", false, false);

    /// <summary>True for ids that carry a mod loader (Forge also ships ids like
    /// <c>1.20.1-forge-47.4.22</c>, so a plain prefix check is not enough).</summary>
    public static bool IsLoaderVersionId(string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return false;
        return versionId.Contains("forge-", StringComparison.OrdinalIgnoreCase)
            || versionId.Contains("fabric-loader-", StringComparison.OrdinalIgnoreCase)
            || versionId.Contains("quilt-loader-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The loader is "installed" only when the instance points at a
    /// loader-shaped version id AND that profile is really on disk. Pointing at
    /// the vanilla id (or at nothing) is exactly the bug this exists for.</summary>
    public static bool LoaderInstalled(ModLoaderType loader, string mcVersion, string? resolvedId, bool profileExists)
    {
        if (loader == ModLoaderType.Vanilla) return true;
        if (!IsLoaderVersionId(resolvedId)) return false;
        if (string.Equals(resolvedId, mcVersion, StringComparison.OrdinalIgnoreCase)) return false;
        return profileExists;
    }

    public static Result Decide(Input input)
    {
        var name = string.IsNullOrWhiteSpace(input.InstanceName) ? "这个实例" : $"「{input.InstanceName}」";

        if (!input.CoreComplete)
            return new Result(Blocker.CoreFilesMissing,
                $"{name} 的游戏文件不完整",
                "缺少原版核心文件（客户端 jar / 库 / 版本 JSON）。现在启动一定失败，需要先补全。",
                CanOverride: false, CanRepair: true);

        if (!LoaderInstalled(input.LoaderType, input.MinecraftVersion, input.ResolvedVersionId, input.LoaderProfileExists))
        {
            var loaderName = input.LoaderType == ModLoaderType.Vanilla ? "原版" : input.LoaderType.ToString();
            return new Result(Blocker.LoaderNotInstalled,
                $"{loaderName} 加载器没有安装完成",
                $"{name} 现在只有原版 {input.MinecraftVersion} 的文件。从这里启动只会进原版" +
                "（没有加载器、没有模组），这正是「下载了整合包却玩到原版」的原因。请先补全。",
                CanOverride: false, CanRepair: true);
        }

        if (input.Modpack.State == ModpackIntegrity.State.Empty)
            return new Result(Blocker.ModpackEmpty,
                "整合包的模组一个都没有",
                $"这个整合包声明了 {input.Modpack.Expected} 个模组，但 mods 文件夹是空的 —— " +
                "整合包文件没有下载完成。现在启动只会进原版。",
                CanOverride: true, CanRepair: input.CanRepairModpack);

        if (input.Modpack.State == ModpackIntegrity.State.Partial)
            return new Result(Blocker.ModpackPartial,
                "整合包缺少大部分模组",
                $"应有 {input.Modpack.Expected} 个模组，实际只有 {input.Modpack.Actual} 个。" +
                "多半是下载中断了；直接启动很可能一进游戏就崩。",
                CanOverride: true, CanRepair: input.CanRepairModpack);

        return Ready;
    }
}
