namespace AuroraCore.Services;

/// <summary>
/// Round-95: "where does the version JSON for this id come from?" decided in
/// one pure function, so the service, the resolver and the tests cannot
/// disagree.
///
/// The bug that motivated it: a failed Forge install left an instance pointing
/// at "forge-1.20.1-47.4.22" (the installer's *URL hint*) although no such
/// profile existed on disk. A later 补全 handed that id to GameInstaller,
/// which asked the Mojang manifest for it and died with
/// "Version 'forge-1.20.1-47.4.22' not found in manifest." Third-party loader
/// ids are NEVER in the official manifest, so that request could only fail.
///
/// Rule: local files are authoritative → otherwise a missing loader profile is
/// an install-state problem (say what to do), and only a genuine vanilla id
/// may reach the official manifest.
/// </summary>
public enum VersionSource { Local, Official, LoaderProfileMissing }

public static class VersionSourcePolicy
{
    // Forge also ships "1.20.1-forge-47.4.22" (marker in the middle), so a
    // plain StartsWith check is not enough — see LaunchReadiness.Round92.
    private static readonly string[] Prefixes =
        { "forge-", "neoforge-", "fabric-loader-", "quilt-loader-", "fabric-", "quilt-", "optifine-" };

    private static readonly string[] Infixes =
        { "-forge-", "-neoforge-", "-fabric-", "-quilt-", "-fabricloader-" };

    public static bool IsThirdPartyLoader(string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return false;
        var id = versionId.Trim();
        foreach (var p in Prefixes)
            if (id.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var m in Infixes)
            if (id.Contains(m, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static VersionSource Decide(string? versionId, bool localJsonExists)
    {
        if (localJsonExists) return VersionSource.Local;
        return IsThirdPartyLoader(versionId) ? VersionSource.LoaderProfileMissing : VersionSource.Official;
    }

    public static string LoaderMissingHint(string versionId) =>
        $"加载器版本「{versionId}」的本地版本文件不存在 —— 说明这个加载器还没装成功。" +
        "Forge / NeoForge / Fabric / Quilt 是第三方版本，不会出现在 Mojang 官方版本清单里，" +
        "所以不能把它当成原版版本去下载。请在「实例管理」点「补全文件」重新安装加载器；" +
        "若反复失败，请检查网络/代理后重试（安装器要单独下载自己的依赖库，启动器已会预先补齐）。";

    public static string CorruptLocalHint(string versionId) =>
        $"本地版本文件「{versionId}」存在但内容不完整（缺少 mainClass），多半是上次安装中断留下的残片。" +
        "启动器不会拿它去查官方清单；请在「实例管理」点「补全文件」重新安装这个加载器。";
}
