using AuroraCore.Services;
using AuroraCore.Utils;

namespace AuroraCore;

/// <summary>
/// Global launcher configuration. Persisted as JSON under the root data
/// directory. Holds paths, download source, proxy, resource limits and the
/// active Java selection. Never stores secrets (account tokens live in the
/// credential store, see AuthService).
/// </summary>
public sealed class LauncherConfig
{
    private static LauncherConfig? _instance;
    public static LauncherConfig Instance => _instance ??= Load();

    public string RootDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AuroraLauncher");

    public DownloadSource DownloadSource { get; set; } = DownloadSource.Official;
    public string? ProxyUrl { get; set; }

    /// <summary>Azure 应用（公共客户端）ID，用于微软设备码登录；留空则尝试环境变量 AURORA_MICROSOFT_CLIENT_ID。</summary>
    public string? MicrosoftClientId { get; set; }

    public int MaxConcurrency { get; set; } = 16;
    public int ConnectTimeoutSeconds { get; set; } = 60;

    /// <summary>Optional explicit Java home/executable override. Empty = auto-detect.</summary>
    public string? JavaPath { get; set; }

    public int MinMemoryMb { get; set; } = 1024;
    public int MaxMemoryMb { get; set; } = 4096;
    public string ExtraJvmArgs { get; set; } = "";
    public string ExtraGameArgs { get; set; } = "";

    public int WindowWidth { get; set; } = 854;
    public int WindowHeight { get; set; } = 480;

    public bool Fullscreen { get; set; } = false;
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public string Language { get; set; } = "zh-CN";

    /// <summary>False until the first-run wizard completes (or is dismissed).
    /// Persisted so onboarding appears exactly once.</summary>
    public bool FirstRunCompleted { get; set; } = false;

    /// <summary>Human-readable note about the last network probe (shown in
    /// settings): e.g. "国内网络：官方线路超时，已自动使用 BMCLAPI 镜像".</summary>
    public string NetworkNote { get; set; } = "";

    /// <summary>安装 Fabric 加载器实例时，自动安装适配当前 MC 版本的 Fabric API
    /// 前置（大多数 Fabric Mod 依赖它）。默认开启；关闭后改为弹窗询问。</summary>
    public bool AutoInstallFabricApi { get; set; } = true;

    /// <summary>下载慢速看门狗阈值（KB/s，聚合速度）：持续约 20 秒低于该值
    /// 即判定线路"爬行"并自动切换镜像。默认 16；极慢网络（2G/3G）用户可在
    /// config.json 中调低以避免误切。0 = 关闭看门狗。</summary>
    public int SlowTransferKBsPerSec { get; set; } = 16;

    /// <summary>CurseForge API key supplied by the USER (round-60). CurseForge
    /// requires a per-developer `x-api-key`; no key is bundled with the app.
    /// This is a public API key, not an account credential.</summary>
    public string? CurseForgeApiKey { get; set; }

    /// <summary>用户已读过的更新日志轮次——启动时若小于最新轮次则轻提示一次
    /// （访问「更新日志」页即标记已读）。</summary>
    public int ChangelogSeenRound { get; set; }

    /// <summary>更新源清单（latest.json）的 URL——留空则用程序内置地址
    /// （UpdateService.DefaultFeedUrl，发布者发布前设置）；两者都空 = 不检查更新。</summary>
    public string? UpdateFeedUrl { get; set; }

    /// <summary>更新方式三态开关（round-58）：1 自动更新 / 2 提醒但不自动更新 /
    /// 3 不获取更新。默认 2 —— 每次启动检查，发现新版本弹窗（立即更新 / 关闭）。</summary>
    public UpdateMode UpdateMode { get; set; } = UpdateMode.Notify;

    /// <summary>启动更新检查方式（旧布尔开关，round-58 起由 UpdateMode 取代）。
    /// 仅用于读取旧 config.json 的兼容迁移，不再写入。</summary>
    [JsonIgnore]
    public bool AutoCheckUpdates { get; set; } = true;

    // ---- Derived directories ----
    public string VersionsDir => Path.Combine(RootDirectory, "versions");
    public string LibrariesDir => Path.Combine(RootDirectory, "libraries");
    public string AssetsDir => Path.Combine(RootDirectory, "assets");
    public string AssetIndexesDir => Path.Combine(AssetsDir, "indexes");
    public string AssetObjectsDir => Path.Combine(AssetsDir, "objects");
    public string InstancesDir => Path.Combine(RootDirectory, "instances");
    public string LogsDir => Path.Combine(RootDirectory, "logs");
    /// <summary>Launcher-managed JDKs installed by the one-click Java button
    /// (round-59). Never written to Program Files or system env vars.</summary>
    public string JavaRuntimesDir => Path.Combine(RootDirectory, "java");

    public void EnsureDirectories()
    {
        foreach (var d in new[] { RootDirectory, VersionsDir, LibrariesDir, AssetsDir, AssetIndexesDir, AssetObjectsDir, InstancesDir, LogsDir, JavaRuntimesDir })
            PathEx.EnsureDirectory(d);
    }

    // ---- Download source URL resolution ----
    public string VersionManifestUrl =>
        DownloadSource == DownloadSource.BMCLAPI
            ? "https://bmclapi.cn/mc/game/version_manifest_v2.json"
            : "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    /// <summary>
    /// Rewrites a Mojang-hosted URL to the configured mirror when BMCLAPI is
    /// selected. BMCLAPI is a legitimate, terms-compliant mirror.
    /// </summary>
    /// <summary>Maps official Mojang / loader Maven URLs onto the BMCLAPI
    /// community mirror (bmclapi2.bangbang93.com). Static so installers can
    /// use it as an automatic fallback when official hosts fail.</summary>
    public static string ToBmclapiUrl(string url) => url
        .Replace("https://piston-meta.mojang.com", "https://bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase)
        .Replace("https://piston-data.mojang.com", "https://bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase)
        .Replace("https://launcher.mojang.com", "https://bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase)
        .Replace("https://libraries.minecraft.net", "https://bmclapi2.bangbang93.com/maven", StringComparison.OrdinalIgnoreCase)
        .Replace("https://resources.download.minecraft.net", "https://bmclapi2.bangbang93.com/assets", StringComparison.OrdinalIgnoreCase)
        .Replace("https://maven.minecraftforge.net", "https://bmclapi2.bangbang93.com/maven", StringComparison.OrdinalIgnoreCase)
        .Replace("https://maven.fabricmc.net", "https://bmclapi2.bangbang93.com/maven", StringComparison.OrdinalIgnoreCase)
        .Replace("https://maven.neoforged.net", "https://bmclapi2.bangbang93.com/maven", StringComparison.OrdinalIgnoreCase)
        .Replace("https://maven.quiltmc.org", "https://bmclapi2.bangbang93.com/maven", StringComparison.OrdinalIgnoreCase)
        .Replace("https://meta.fabricmc.net", "https://bmclapi2.bangbang93.com/fabric-meta", StringComparison.OrdinalIgnoreCase)
        .Replace("https://meta.quiltmc.org", "https://bmclapi2.bangbang93.com/quilt-meta", StringComparison.OrdinalIgnoreCase);

    public string RewriteUrl(string url)
    {
        if (DownloadSource != DownloadSource.BMCLAPI) return url;
        return ToBmclapiUrl(url);
    }

    public void Save()
    {
        EnsureDirectories();
        var path = Path.Combine(RootDirectory, "config.json");
        File.WriteAllText(path, JsonHelper.SerializeIndented(this));
        HttpClientFactory.Instance.ConfigureProxy(ProxyUrl);
    }

    public static LauncherConfig Load()
    {
        var defaultRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AuroraLauncher");
        var path = Path.Combine(defaultRoot, "config.json");
        LauncherConfig cfg;
        if (File.Exists(path))
        {
            var raw = File.ReadAllText(path);
            try { cfg = JsonHelper.Deserialize<LauncherConfig>(raw); }
            catch { cfg = new LauncherConfig(); }
            // One-time migration (round-58): old config.json had a boolean
            // "autoCheckUpdates". If it was explicitly false and the new
            // three-way "updateMode" is absent, honor the user's opt-out.
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (cfg.UpdateMode == UpdateMode.Notify &&
                    !doc.RootElement.TryGetProperty("updateMode", out _) &&
                    doc.RootElement.TryGetProperty("autoCheckUpdates", out var legacy) &&
                    legacy.ValueKind == JsonValueKind.False)
                    cfg.UpdateMode = UpdateMode.Disabled;
            }
            catch { /* malformed legacy config → keep defaults */ }
        }
        else
        {
            cfg = new LauncherConfig();
        }
        cfg.EnsureDirectories();
        HttpClientFactory.Instance.ConfigureProxy(cfg.ProxyUrl);
        _instance = cfg;
        return cfg;
    }
}

public enum DownloadSource { Official, BMCLAPI }

public enum ThemePreference { Light, Dark, System }
