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
    private static readonly object InstanceGate = new();
    /// <summary>Round-104: Load() creates directories and writes config.json.
    /// A lazy race at startup could run it twice and clobber settings.</summary>
    public static LauncherConfig Instance
    {
        get { lock (InstanceGate) return _instance ??= Load(); }
    }

    public string RootDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AuroraLauncher");

    /// <summary>Round-74: true when the launcher runs from a portable folder
    /// (portable.txt / config.json beside the EXE). Computed on every load, never
    /// persisted — a copied folder must stay portable even after someone edits
    /// config.json elsewhere.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPortableMode { get; private set; }

    /// <summary>Where the config file itself lives (portable: beside the EXE).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ConfigPath { get; private set; } = "";

    /// <summary>Round-85: Auto (race both, faster first) / Official only / Mirror only.
    /// Replaces the old two-value "downloadSource" (0=official, 1=BMCLAPI), which
    /// could only express "always this one" — see <see cref="MigrateLegacySource"/>.</summary>
    public Utils.DownloadPolicy RoutePolicy { get; set; } = Utils.DownloadPolicy.Auto;
    public string? ProxyUrl { get; set; }

    /// <summary>Azure 应用（公共客户端）ID，用于微软设备码登录；留空则尝试环境变量 AURORA_MICROSOFT_CLIENT_ID。</summary>
    public string? MicrosoftClientId { get; set; }

    public int MaxConcurrency { get; set; } = 16;
    public int ConnectTimeoutSeconds { get; set; } = 60;

    /// <summary>Optional explicit Java home/executable override. Empty = auto-detect.</summary>
    public string? JavaPath { get; set; }

    public int MinMemoryMb { get; set; } = 1024;
    public int MaxMemoryMb { get; set; } = 4096;

    /// <summary>Round-83: derive -Xmx per launch from physical RAM + the
    /// instance's mod count + shaders, instead of using <see cref="MaxMemoryMb"/>.
    /// Defaults to FALSE so an upgrade never changes a setup that already works;
    /// a brand-new install turns it on when the first-run wizard finishes, and
    /// <see cref="MaxMemoryMb"/> keeps holding the last manual value.</summary>
    public bool AutoMemory { get; set; }

    /// <summary>Round-83: after the window has sat in the tray for a few
    /// minutes, drop the launcher's own icon cache and trim its working set.
    /// Only this process — the game (java) process is never touched.</summary>
    public bool ReleaseIdleMemory { get; set; } = true;
    public string ExtraJvmArgs { get; set; } = "";
    public string ExtraGameArgs { get; set; } = "";

    /// <summary>Round-87: when should the download page warn about leftover
    /// <c>.part</c> files? Two independent dimensions because both shapes are
    /// suspicious (20 files × 300 MB, or 400 files × 1 MB). Either threshold at 0
    /// disables that dimension; both at 0 turns the banner off entirely.</summary>
    public int ResidueWarnBytesMb { get; set; } = 200;
    public int ResidueWarnCount { get; set; } = 50;

    public int WindowWidth { get; set; } = 854;
    public int WindowHeight { get; set; } = 480;

    public bool Fullscreen { get; set; } = false;
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public string Language { get; set; } = "zh-CN";

    /// <summary>False until the first-run wizard completes (or is dismissed).
    /// Persisted so onboarding appears exactly once.</summary>
    public bool FirstRunCompleted { get; set; } = false;

    /// <summary>Round-74: tray icon. Turning it off restores the round-73
    /// behaviour (close = exit, with the download guard).</summary>
    public bool MinimizeToTrayEnabled { get; set; } = true;

    /// <summary>Round-77: scan mod metadata (missing dependencies / conflicts /
    /// duplicates) before every launch. Cheap (~1 ms per jar) and catches the most
    /// common "started once, crashed immediately" cause before it happens.</summary>
    public bool CheckModsBeforeLaunch { get; set; } = true;

    /// <summary>Round-79: automatic <c>saves/</c> snapshot policy. Default
    /// "after exit" — it never delays a launch and still protects against a
    /// crash that corrupted the world while playing.</summary>
    public SaveBackupMode SaveBackup { get; set; } = SaveBackupMode.AfterExit;

    /// <summary>Round-79: how many snapshots to keep per instance (oldest are
    /// pruned after each successful backup).</summary>
    public int SaveBackupKeep { get; set; } = SaveBackupPolicy.DefaultKeep;

    /// <summary>Round-80: liquid-glass mode. Default Off — the original opaque
    /// theme — because every other mode is an opt-in with a performance warning.</summary>
    public GlassMode GlassMode { get; set; } = GlassMode.Off;

    /// <summary>Round-74: what the X does. <see cref="CloseAction.Ask"/> shows
    /// the dialog with a "记住我的选择" checkbox (which writes this value).</summary>
    public CloseAction CloseAction { get; set; } = CloseAction.Ask;

    /// <summary>Human-readable note about the last network probe (shown in
    /// settings): e.g. "国内网络：官方线路超时，已自动使用 BMCLAPI 镜像".</summary>
    public string NetworkNote { get; set; } = "";

    /// <summary>安装 Fabric 加载器实例时，自动安装适配当前 MC 版本的 Fabric API
    /// 前置（大多数 Fabric Mod 依赖它）。默认开启；关闭后改为弹窗询问。</summary>
    public bool AutoInstallFabricApi { get; set; } = true;

    /// <summary>Round-98: JDK vendor used by 「一键安装 Java」 in settings.
    /// Persisted as display text so an unknown value degrades to Temurin.</summary>
    public string JavaDistribution { get; set; } =
        JavaDistributionPolicy.DisplayText(AuroraCore.Services.JavaDistribution.Temurin);

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

    /// <summary>Round-88: disk cache for project icons. It used to live in
    /// <c>%TEMP%\AuroraLauncher\icons</c>, which made it invisible to the storage
    /// cleaner, shared across every data root and liable to be wiped by Windows
    /// Storage Sense. Inside the data root it is one predictable folder that the
    /// user can actually see and clean.</summary>
    public string IconsCacheDir => Path.Combine(RootDirectory, "cache", "icons");
    /// <summary>Launcher-managed JDKs installed by the one-click Java button
    /// (round-59). Never written to Program Files or system env vars.</summary>
    public string JavaRuntimesDir => Path.Combine(RootDirectory, "java");

    public void EnsureDirectories()
    {
        foreach (var d in new[] { RootDirectory, VersionsDir, LibrariesDir, AssetsDir, AssetIndexesDir, AssetObjectsDir, InstancesDir, LogsDir, JavaRuntimesDir })
            PathEx.EnsureDirectory(d);
    }

    // ---- Download source URL resolution ----
    /// <summary>Round-71: ALWAYS the official Mojang URL.
    ///
    /// This used to return the BMCLAPI URL when the source was set to BMCLAPI
    /// (auto-selected by the network probe). NetRouting then derived the mirror
    /// from an already-mirrored URL, got the SAME string back, deduplicated to
    /// two routes, and the official host was never tried at all. When
    /// bmclapi.cn stopped resolving the user got an empty version list with
    /// "所有 2 条线路均失败" — the exact round-70 screenshot.
    ///
    /// Mirror selection now lives in one place only (NetRouting, which tries
    /// both hosts and cancels the losers).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string VersionManifestUrl => OfficialVersionManifestUrl;

    public const string OfficialVersionManifestUrl =
        "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

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

    /// <summary>Round-71: best-effort reverse of <see cref="ToBmclapiUrl"/>.
    ///
    /// Needed because a URL reaching NetRouting may ALREADY be a mirror URL
    /// (an older config, a cached entry, or a caller that rewrote first). Only
    /// the unambiguous shapes are reversed — a shared <c>/maven/…</c> path could
    /// belong to any of five upstreams, so it is left untouched instead of
    /// guessing and hitting the wrong host.</summary>
    public static string ToOfficialUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        if (!url.Contains("bmclapi", StringComparison.OrdinalIgnoreCase)) return url;
        foreach (var host in MirrorHostsForReverseMapping)
        {
            if (!url.StartsWith(host, StringComparison.OrdinalIgnoreCase)) continue;
            var mapped = MapMirrorPathToOfficial(url[host.Length..]);
            if (mapped != null) return mapped;
        }
        return url; // truly ambiguous — do not guess
    }

    /// <summary>All mirror domains we have ever produced or accepted. The old
    /// <c>bmclapi.bangbang93.com</c> host is still sitting in download journals
    /// of older installs (the round-103 screenshot); without it here, a failed
    /// resume had no alternate route and "retry" failed forever.</summary>
    public static readonly string[] MirrorHostsForReverseMapping =
    {
        "https://bmclapi2.bangbang93.com", "https://bmclapi.cn", "https://bmclapi.bangbang93.com"
    };

    private static string? MapMirrorPathToOfficial(string rest)
    {
        // Round-103: client jars are NOT ambiguous — /v1/packages/<hash>/<file>
        // always maps to Mojang's piston-data host. Leaving it unmapped was why
        // a stalled 26.1.2.jar could never fall back to the official route.
        if (rest.StartsWith("/v1/packages/", StringComparison.OrdinalIgnoreCase))
            return "https://piston-data.mojang.com" + rest;
        if (rest.StartsWith("/mc/", StringComparison.OrdinalIgnoreCase))
            return "https://piston-meta.mojang.com" + rest;
        if (rest.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
            return "https://resources.download.minecraft.net" + rest["/assets".Length..];
        if (rest.StartsWith("/fabric-meta/", StringComparison.OrdinalIgnoreCase))
            return "https://meta.fabricmc.net" + rest["/fabric-meta".Length..];
        if (rest.StartsWith("/quilt-meta/", StringComparison.OrdinalIgnoreCase))
            return "https://meta.quiltmc.org" + rest["/quilt-meta".Length..];

        // Round-86: a bare "/maven/…" is ambiguous, but the GROUP PATH tells
        // us the upstream — that is not a guess.
        if (rest.StartsWith("/maven/", StringComparison.OrdinalIgnoreCase))
        {
            var path = rest["/maven/".Length..];
            var repoPrefix = "";
            foreach (var candidate in new[] { "repository/release/", "repository/releases/", "releases/" })
            {
                if (!path.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)) continue;
                repoPrefix = path[..candidate.Length];
                path = path[candidate.Length..];
                break;
            }

            if (path.StartsWith("net/minecraftforge/", StringComparison.OrdinalIgnoreCase))
                return "https://maven.minecraftforge.net/" + repoPrefix + path;
            if (path.StartsWith("net/neoforged/", StringComparison.OrdinalIgnoreCase))
                return "https://maven.neoforged.net/" + repoPrefix + path;
            if (path.StartsWith("net/fabricmc/", StringComparison.OrdinalIgnoreCase))
                return "https://maven.fabricmc.net/" + repoPrefix + path;
            if (path.StartsWith("org/quiltmc/", StringComparison.OrdinalIgnoreCase))
                return "https://maven.quiltmc.org/" + repoPrefix + path;
        }
        return null;
    }

    public string RewriteUrl(string url)
    {
        // Round-85: the policy decides where BINARY downloads go; the metadata
        // path hedges both routes anyway (NetRouting). Auto follows the measured
        // preference, so "fastest route wins" also holds for client.jar.
        var policy = RoutePolicy;
        if (policy == Utils.DownloadPolicy.Mirror) return ToBmclapiUrl(url);
        if (policy == Utils.DownloadPolicy.Official) return ToOfficialUrl(url);
        return Utils.NetworkProbe.Preference == Utils.RoutePreference.Mirror
            ? ToBmclapiUrl(url)
            : ToOfficialUrl(url);
    }

    /// <summary>The mirror's deprecated domain. Still recognised by
    /// <see cref="ToOfficialUrl"/> and healed by <see cref="HealLegacyMirrorHost"/>,
    /// but it must never be PRODUCED again.</summary>
    public const string LegacyMirrorHost = "bmclapi.cn";
    /// <summary>Round-103: another old domain found in real download journals.</summary>
    public const string LegacyMirrorHost2 = "bmclapi.bangbang93.com";
    public const string MirrorHost = "bmclapi2.bangbang93.com";

    /// <summary>Replaces the dead mirror domain in every persisted string field.
    /// Reflection (not a hardcoded list) so a future URL field cannot resurrect
    /// the problem. Returns true when something was changed and should be saved.</summary>
    public static bool HealLegacyMirrorHost(LauncherConfig cfg)
    {
        var changed = false;
        foreach (var prop in typeof(LauncherConfig).GetProperties(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (prop.PropertyType != typeof(string) || !prop.CanRead || !prop.CanWrite) continue;
            if (prop.GetIndexParameters().Length > 0) continue;
            if (prop.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), true).Length > 0) continue;
            try
            {
                var value = (string?)prop.GetValue(cfg);
                if (string.IsNullOrEmpty(value)) continue;
                if (!value.Contains(LegacyMirrorHost, StringComparison.OrdinalIgnoreCase) &&
                    !value.Contains(LegacyMirrorHost2, StringComparison.OrdinalIgnoreCase)) continue;
                var healed = value
                    .Replace(LegacyMirrorHost2, MirrorHost, StringComparison.OrdinalIgnoreCase)
                    .Replace(LegacyMirrorHost, MirrorHost, StringComparison.OrdinalIgnoreCase);
                prop.SetValue(cfg, healed);
                changed = true;
            }
            catch { /* a read-only or throwing property must not break startup */ }
        }
        if (changed)
            Utils.Logger.Instance.Info("Config",
                $"已自动修正旧配置里的废弃镜像域名 {LegacyMirrorHost} → {MirrorHost}");
        return changed;
    }

    /// <summary>Round-84: true when this instance was pointed at a throw-away
    /// root by a test (config file lives there too). Such an instance may write
    /// freely; the production one may not persist a temp root.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsTestIsolated { get; private set; }

    /// <summary>Round-84: the temp root that was replaced during <c>Load()</c>
    /// (null = nothing was healed). Reported after the log file is configured,
    /// because Load() runs before we know where the log lives.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? RootHealedFrom { get; private set; }

    /// <summary>Round-84 test hook: isolate BOTH the config file and the data
    /// root. Setting <c>RootDirectory</c> alone is not enough — a repair action
    /// calling <c>Save()</c> would then write the temp root into the REAL
    /// config.json (round-78 crash-card smoke did exactly that and pointed a
    /// user's launcher at %TEMP%).</summary>
    public static void UseTestRoot(string root)
    {
        PathEx.EnsureDirectory(root);
        var cfg = Instance;
        cfg.ConfigPath = Path.Combine(root, "config.json");
        cfg.RootDirectory = root;
        cfg.IsTestIsolated = true;
        // Round-85: `Instance` above already LOADED the real config (that is how
        // the singleton works), so a user who forced "mirror only" would make the
        // whole suite run mirror-only — and a rate-limited mirror then skips every
        // network test. Tests start from the default policy instead.
        cfg.RoutePolicy = Utils.DownloadPolicy.Auto;
        cfg.EnsureDirectories();
    }

    /// <summary>Round-85: old configs stored "downloadSource" 0/1
    /// (official / BMCLAPI). Both become Auto — that old value was frequently
    /// written by the round-42 startup probe (not by the user), so carrying it
    /// over as "force mirror" would lock people into one route forever: exactly
    /// the complaint that produced this feature. A user who really needs the
    /// mirror can still choose "仅使用镜像线路".</summary>
    public static Utils.DownloadPolicy MigrateLegacySource(int legacy) => Utils.DownloadPolicy.Auto;

    public void Save()
    {
        // Round-84: never persist a temp root into the production config, no
        // matter which code path got here. A launcher pointed at %TEMP% shows
        // "one instance that cannot start" and the user has no idea why.
        if (!IsTestIsolated && DataRootPolicy.IsEphemeral(RootDirectory))
        {
            var fallback = DataRootPolicy.DefaultRoot();
            Utils.Logger.Instance.Warn("Config",
                $"拒绝把临时目录写入配置（{RootDirectory}），已回退到 {fallback}");
            RootDirectory = fallback;
        }

        EnsureDirectories();
        var path = string.IsNullOrEmpty(ConfigPath) ? Path.Combine(RootDirectory, "config.json") : ConfigPath;
        // Round-104: a killed write used to leave an empty config.json and the
        // next launch fell back to defaults ("设置全没了"). Write-then-atomic-
        // move, keeping the previous copy as a recoverable backup.
        var backup = path + ".bak-last";
        var temp = path + ".tmp";
        try { if (File.Exists(path)) File.Copy(path, backup, overwrite: true); } catch { }
        File.WriteAllText(temp, JsonHelper.SerializeIndented(this));
        File.Move(temp, path, overwrite: true);
        HttpClientFactory.Instance.ConfigureProxy(ProxyUrl);
    }

    public static LauncherConfig Load()
    {
        // Round-74: portable detection happens BEFORE reading anything, because
        // it decides which config.json is the real one.
        var exePath = Environment.ProcessPath;
        var portableRoot = PortableModePolicy.ResolveRoot(exePath);
        var defaultRoot = portableRoot
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AuroraLauncher");
        var path = Path.Combine(defaultRoot, "config.json");
        LauncherConfig cfg;
        string? rawJson = null;
        if (File.Exists(path))
        {
            var raw = File.ReadAllText(path);
            rawJson = raw;
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

        // Round-71 self-heal: the mirror's old domain (bmclapi.cn) is dead, and
        // every build up to round-70 could persist it into config.json. Replace
        // it silently wherever it survived — a user must never be stuck on a
        // hostname we already know is wrong.
        // A legacy host in an ignored/unknown field must also disappear from the
        // file: healing the live values is not enough if the stale key survives.
        var healed = HealLegacyMirrorHost(cfg);

        // Round-85: configs up to round-84 stored "downloadSource" as 0/1
        // (official / BMCLAPI) — a value that could only mean "always use this
        // one". 1 must become "force mirror" (that is what the user chose), 0
        // becomes "auto", which is the better default now that both routes race.
        if (rawJson != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                if (doc.RootElement.TryGetProperty("downloadSource", out var legacy) &&
                    legacy.ValueKind == JsonValueKind.Number &&
                    !doc.RootElement.TryGetProperty("routePolicy", out _))
                {
                    cfg.RoutePolicy = MigrateLegacySource(legacy.GetInt32());
                    healed = true;
                }
            }
            catch { /* malformed legacy config → keep the Auto default */ }
        }

        // Round-74: in portable mode the folder wins over any stored path. This
        // must be forced on EVERY load, not once, or a folder copied to another
        // machine (or drive letter) would keep writing to the old absolute path.
        cfg.ConfigPath = path;
        if (portableRoot != null)
        {
            cfg.IsPortableMode = true;
            if (!string.Equals(cfg.RootDirectory, portableRoot, StringComparison.OrdinalIgnoreCase))
            {
                cfg.RootDirectory = portableRoot;
                healed = true;
            }
        }

        // Round-84: heal a config whose data root was pointed at a temp folder
        // (the round-78 crash-card smoke persisted its own temp root here). The
        // real data is still in the default root; the user just saw an empty
        // instance that could not launch.
        if (!cfg.IsPortableMode)
        {
            var (root, rootHealed) = DataRootPolicy.Sanitize(cfg.RootDirectory, defaultRoot);
            if (rootHealed)
            {
                Utils.Logger.Instance.Warn("Config",
                    $"配置里的存储目录指向临时文件夹（{cfg.RootDirectory}），已自动恢复到 {root}");
                cfg.RootHealedFrom = cfg.RootDirectory;
                cfg.RootDirectory = root;
                healed = true;
            }
        }

        if (healed || (rawJson != null && rawJson.Contains(LegacyMirrorHost, StringComparison.OrdinalIgnoreCase)))
            cfg.Save();
        cfg.EnsureDirectories();
        HttpClientFactory.Instance.ConfigureProxy(cfg.ProxyUrl);
        _instance = cfg;
        return cfg;
    }
}


public enum ThemePreference { Light, Dark, System }
