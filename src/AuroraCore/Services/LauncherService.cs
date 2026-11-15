using AuroraCore.Download;
using AuroraCore.Models;
using AuroraCore.Utils;

namespace AuroraCore.Services;

/// <summary>
/// Top-level orchestrator used by the UI. Wires together manifest resolution,
/// installation, mod-loaders, accounts and launching. Keeps UI code thin.
/// </summary>
public sealed class LauncherService
{
    private readonly VersionManifestService _manifest = new();
    private readonly VersionResolver _resolver;
    private readonly GameInstaller _installer;
    private readonly ModLoaderInstaller _modLoaders;
    private readonly JavaFinder _javaFinder = new();
    private readonly LaunchArgumentBuilder _builder = new();
    private readonly GameLauncher _gameLauncher = new();
    private readonly ModManager _mods;
    private readonly AuthService _auth = new();
    private readonly InstanceManager _instances = new();
    private readonly Logger _log = Logger.Instance;

    public LauncherService()
    {
        var engine = new DownloadEngine();
        _resolver = new VersionResolver(_manifest);
        _installer = new GameInstaller(_resolver, engine);
        _modLoaders = new ModLoaderInstaller(engine);
        _mods = new ModManager(engine);
        Logger.Instance.Configure(LauncherConfig.Instance.LogsDir);
    }

    public VersionManifestService Manifest => _manifest;
    public GameInstaller Installer => _installer;
    public ModLoaderInstaller ModLoaders => _modLoaders;
    public ModManager ModManager => _mods;
    public AuthService Auth => _auth;
    public InstanceManager Instances => _instances;
    public JavaFinder JavaFinder => _javaFinder;

    /// <summary>Lists Java runtimes installed on this machine (for the settings page).</summary>
    public async Task<List<JavaInstallation>> FindJavaAsync(CancellationToken ct = default)
        => await _javaFinder.FindAllAsync(ct).ConfigureAwait(false);

    // ---------- version metadata ----------
    public Task<VersionManifest> GetManifestAsync(CancellationToken ct = default) => _manifest.GetManifestAsync(ct);

    /// <summary>Round-71: true when the last manifest came from the offline
    /// cache (every route failed). Surfaced in the status line.</summary>
    public bool LastManifestFromCache => _manifest.LastManifestFromCache;

    // ---------- installation ----------
    public Task InstallVanillaAsync(string versionId, Action<DownloadProgress>? onProgress = null, CancellationToken ct = default, bool includeAssets = true, Action<string>? onStage = null)
        => _installer.InstallAsync(versionId, onProgress, ct, includeAssets, onStage);

    public async Task<string> InstallWithLoaderAsync(GameInstance instance, Action<DownloadProgress>? onProgress, Action<string>? onLog, CancellationToken ct, Action<string>? onStage = null)
    {
        // 1) Ensure base game is installed centrally.
        if (!_installer.IsInstalled(instance.MinecraftVersion))
            await _installer.InstallAsync(instance.MinecraftVersion, onProgress, ct, true, onStage).ConfigureAwait(false);

        // Vanilla instances need no loader step.
        if (instance.LoaderType == ModLoaderType.Vanilla)
            return instance.MinecraftVersion;

        // 2) Run the loader installer.
        var java = await GetJavaForAsync(instance, ct).ConfigureAwait(false);
        var result = await _modLoaders.InstallAsync(instance.LoaderType, instance.MinecraftVersion, instance.LoaderVersion,
            LauncherConfig.Instance.RootDirectory, java, onLog, ct, onStage, onProgress).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException(
                $"{instance.LoaderType} 加载器安装失败：{result.Error}。请检查网络后重试，" +
                "或在「实例管理」里换一个加载器版本。");
        instance.ResolvedVersionId = result.VersionId;
        // Store the BARE loader version, never the full version id (a full id
        // here poisoned the profile URL with 400/404 on every route).
        instance.LoaderVersion = result.LoaderVersion ?? instance.LoaderVersion;

        // 3) Fetch the loader version's own libraries through the standard
        // pipeline (hash-verified files are skipped instantly). Essential for
        // the installer-free Fabric/Quilt path and a safety net for
        // Forge/NeoForge, whose version JSON libs were previously never
        // downloaded by us.
        await _installer.InstallAsync(result.VersionId!, onProgress, ct, includeAssets: false, onStage).ConfigureAwait(false);
        return result.VersionId!;
    }

    // ---------- pre-launch scan & repair ----------
    /// <summary>Round-92: the loader counts as installed only when the instance
    /// points at a loader-shaped version id AND that profile exists. Before this
    /// check, a Forge instance whose loader never installed still reported
    /// "no repair needed" — because only the vanilla files were inspected.</summary>
    public bool LoaderNeedsInstall(GameInstance instance)
    {
        if (instance.LoaderType == ModLoaderType.Vanilla) return false;
        var resolved = instance.ResolvedVersionId;
        return !LaunchReadiness.LoaderInstalled(instance.LoaderType, instance.MinecraftVersion,
            resolved, resolved != null && _installer.IsVersionJsonInstalled(resolved));
    }

    /// <summary>Cheap check: is anything missing that would prevent launch?
    /// (client jar / version JSON / natives / loader version / pack contents)</summary>
    public bool NeedsRepair(GameInstance instance)
    {
        // Single source of truth: the Evaluate function (see GameInstaller).
        var id = instance.ResolvedVersionId ?? instance.MinecraftVersion;
        if (instance.LoaderType == ModLoaderType.Vanilla)
        {
            if (!_installer.Evaluate(id, requireNatives: false).Complete) return true;
        }
        else
        {
            // Loader instance: vanilla part (no natives — extracted at launch) +
            // loader JSON + loader libraries.
            if (!_installer.Evaluate(instance.MinecraftVersion, requireNatives: false).Complete) return true;
            if (LoaderNeedsInstall(instance)) return true;
            if (!_installer.Evaluate(id, requireNatives: false).Complete) return true;
        }

        // Round-92: a modpack whose mods never arrived is NOT complete, even
        // though every vanilla file is in place.
        return instance.IsModpack && ModpackIntegrity.Evaluate(instance).IsBroken;
    }

    /// <summary>Detailed state for UI/logging: what exactly is missing.</summary>
    public string DescribeMissing(GameInstance instance)
    {
        var id = instance.ResolvedVersionId ?? instance.MinecraftVersion;
        var parts = new List<string>();
        if (instance.LoaderType != ModLoaderType.Vanilla)
        {
            if (LoaderNeedsInstall(instance))
                parts.Add($"{instance.LoaderType} {instance.LoaderVersion ?? ""} 加载器未安装".TrimEnd());
            var vanilla = _installer.Evaluate(instance.MinecraftVersion, requireNatives: false);
            if (!vanilla.Complete) parts.Add($"{instance.MinecraftVersion}: {vanilla}");
            if (!LoaderNeedsInstall(instance))
            {
                var loader = _installer.Evaluate(id, requireNatives: false);
                if (!loader.Complete) parts.Add($"{id}: {loader}");
            }
        }
        else
        {
            var v = _installer.Evaluate(id, requireNatives: false);
            if (!v.Complete) parts.Add($"{id}: {v}");
        }
        var pack = ModpackIntegrity.Evaluate(instance);
        if (pack.IsBroken) parts.Add(pack.Describe());
        return parts.Count == 0 ? "完整" : string.Join("；", parts);
    }

    /// <summary>Round-92: everything the launch gate needs, gathered once.
    /// Pure decision lives in <see cref="LaunchReadiness"/> so it is testable
    /// without a file system.</summary>
    public LaunchReadiness.Result CheckReadiness(GameInstance instance)
    {
        var resolved = instance.ResolvedVersionId;
        var profileExists = resolved != null && _installer.IsVersionJsonInstalled(resolved);
        // "Core" here means the vanilla half only — the loader half is decided
        // by LaunchReadiness itself, which is the whole point of this round.
        var coreComplete = _installer.Evaluate(instance.MinecraftVersion, requireNatives: false).Complete;
        var pack = ModpackIntegrity.Evaluate(instance);
        return LaunchReadiness.Decide(new LaunchReadiness.Input
        {
            LoaderType = instance.LoaderType,
            MinecraftVersion = instance.MinecraftVersion,
            ResolvedVersionId = resolved,
            LoaderProfileExists = profileExists,
            CoreComplete = coreComplete,
            Modpack = pack,
            CanRepairModpack = ModpackSourceAvailable(instance),
            InstanceName = instance.Name
        });
    }

    /// <summary>True when "补全" can actually do something: the original .mrpack
    /// is still on disk, or the manifest kept the download URLs.</summary>
    public bool ModpackSourceAvailable(GameInstance instance)
    {
        if (!instance.IsModpack) return false;
        if (!string.IsNullOrEmpty(instance.ModpackSourcePath) && File.Exists(instance.ModpackSourcePath)) return true;
        var manifest = ModpackManifest.Load(instance.Directory);
        return manifest != null && manifest.Missing(instance.Directory).Any(f => !string.IsNullOrEmpty(f.Url));
    }

    /// <summary>Re-downloads whatever the modpack manifest says should be there.
    /// The engine skips hash-verified files, so a complete pack costs nothing.
    /// Returns false when the pack's source is gone (the user must re-download).</summary>
    public async Task<bool> RepairModpackAsync(GameInstance instance, Action<DownloadProgress>? onProgress,
        Action<string>? onLog, CancellationToken ct)
    {
        if (!instance.IsModpack) return true;

        var manifest = ModpackManifest.Load(instance.Directory);
        if (!string.IsNullOrEmpty(instance.ModpackSourcePath) && File.Exists(instance.ModpackSourcePath))
        {
            onLog?.Invoke("[Modpack] 按原始整合包补全: " + instance.ModpackSourcePath);
            await _mods.InstallMrpackAsync(instance.ModpackSourcePath!, instance.Directory, onProgress, ct).ConfigureAwait(false);
            return true;
        }

        if (manifest != null)
        {
            var missing = manifest.Missing(instance.Directory)
                .Where(f => !string.IsNullOrEmpty(f.Url))
                .Select(f => new DownloadEntry
                {
                    Url = f.Url!,
                    Destination = Path.Combine(instance.Directory,
                        f.Path.Replace('/', Path.DirectorySeparatorChar)),
                    ExpectedSha1 = f.Sha1,
                    ExpectedSize = f.Size > 0 ? f.Size : null,
                    DisplayName = f.Path,
                    Category = "modpack-file"
                }).ToList();
            if (missing.Count > 0)
            {
                onLog?.Invoke($"[Modpack] 按清单补下 {missing.Count} 个缺失文件");
                await _mods.DownloadFilesAsync(missing, onProgress, ct).ConfigureAwait(false);
            }
            return true;
        }

        onLog?.Invoke("[Modpack] 原始整合包文件已不在，无法自动补全");
        return false;
    }

    /// <summary>Re-runs the install pipeline for whatever is missing. The
    /// download engine skips hash-verified existing files, so a healthy install
    /// completes quickly while missing files are re-downloaded.</summary>
    public async Task RepairAsync(GameInstance instance, Action<DownloadProgress>? onProgress,
        Action<string>? onLog, CancellationToken ct, Action<string>? onStage = null)
    {
        if (!_installer.IsFullyInstalled(instance.MinecraftVersion))
            await _installer.InstallAsync(instance.MinecraftVersion, onProgress, ct, true, onStage).ConfigureAwait(false);

        if (LoaderNeedsInstall(instance))
        {
            onStage?.Invoke("installer-download");
            var java = await GetJavaForAsync(instance, ct).ConfigureAwait(false);
            var result = await _modLoaders.InstallAsync(instance.LoaderType, instance.MinecraftVersion, instance.LoaderVersion,
                LauncherConfig.Instance.RootDirectory, java, onLog, ct, onStage, onProgress).ConfigureAwait(false);
            if (!result.Success)
                throw new InvalidOperationException(
                    $"{instance.LoaderType} 加载器补全失败：{result.Error}。请检查网络后重试。");
            instance.ResolvedVersionId = result.VersionId;
            // Round-92: store the BARE loader version. This used to keep
            // result.VersionId when LoaderVersion was null, which poisons every
            // later profile URL with the full id.
            instance.LoaderVersion = result.LoaderVersion ?? instance.LoaderVersion;
        }

        // Round-95: never hand a loader-shaped id with no usable profile to the
        // vanilla installer. If the loader step above could not produce one,
        // fail with an actionable hint instead of "not found in manifest".
        if (instance.ResolvedVersionId is { Length: > 0 } pending &&
            VersionSourcePolicy.IsThirdPartyLoader(pending) &&
            !_installer.IsVersionJsonInstalled(pending))
            throw new InvalidOperationException(VersionSourcePolicy.LoaderMissingHint(pending));

        // The loader profile may have arrived, but its own libraries still need
        // to be fetched (the Forge installer leaves that to us).
        if (instance.ResolvedVersionId is { Length: > 0 } installed &&
            !_installer.Evaluate(installed, requireNatives: false).Complete)
            await _installer.InstallAsync(installed, onProgress, ct, includeAssets: false, onStage).ConfigureAwait(false);

        if (instance.IsModpack && ModpackIntegrity.Evaluate(instance).IsBroken)
            await RepairModpackAsync(instance, onProgress, onLog, ct).ConfigureAwait(false);
    }

    // ---------- java ----------
    public async Task<JavaInstallation> GetJavaForAsync(GameInstance instance, CancellationToken ct = default)
    {
        var recommended = JavaRequirement.RecommendMajor(instance.MinecraftVersion);

        // Round-62 PRIORITY: instance override > launcher-wide override >
        // automatic selection driven by the merged version's javaVersion.
        // Both launch and mod-loader repair go through here, so one setting
        // covers every place an instance needs Java.
        if (!string.IsNullOrWhiteSpace(instance.JavaPath) && File.Exists(instance.JavaPath))
        {
            _log.Info("Java", $"Instance '{instance.Name}' uses its own Java ({recommended}+ recommended): {instance.JavaPath}");
            return CustomJava(instance.JavaPath, recommended);
        }

        var global = LauncherConfig.Instance.JavaPath;
        if (!string.IsNullOrEmpty(global) && File.Exists(global))
            return CustomJava(global, recommended);

        var all = await _javaFinder.FindAllAsync(ct).ConfigureAwait(false);
        // Determine required major from the MERGED version (loader profiles
        // carry no javaVersion of their own — the raw file would wrongly
        // resolve to the default and pick the wrong Java).
        int required = 21;
        var id = instance.ResolvedVersionId ?? instance.MinecraftVersion;
        try
        {
            var merged = await _resolver.ResolveAsync(id, ct).ConfigureAwait(false);
            required = VersionResolver.GetRequiredJavaMajor(merged);
        }
        catch { /* fall back to default 21 */ }
        var best = JavaFinder.SelectBest(all, required);
        if (best == null) throw new InvalidOperationException("No Java runtime found. Please install Java 21+.");
        _log.Info("Java", $"Selected {best} for required major {required}.");
        return best;
    }

    /// <summary>Wraps an explicitly chosen java.exe. Major uses the MC-version
    /// recommendation instead of a hard-coded 21, so a Java 8 override on a
    /// 1.12 instance is not misreported to the loader installer / logs.</summary>
    private static JavaInstallation CustomJava(string path, int major) => new()
    {
        JavaExecutable = path,
        Home = Path.GetDirectoryName(Path.GetDirectoryName(path)) ?? "",
        Version = "custom",
        Major = major,
        Vendor = "custom"
    };

    // ---------- launch ----------
    public async Task<LaunchSpec> BuildLaunchSpecAsync(GameInstance instance, Account account, CancellationToken ct = default)
    {
        var id = instance.ResolvedVersionId ?? instance.MinecraftVersion;
        var jsonPath = Path.Combine(LauncherConfig.Instance.VersionsDir, id, id + ".json");
        if (!File.Exists(jsonPath)) throw new InvalidOperationException($"Version '{id}' is not installed. Please install it first.");

        // Round-92: never build a VANILLA command line for a loader instance.
        // This is the exact shape of the "imported a modpack, played vanilla"
        // bug: ResolvedVersionId was empty, the id fell back to the vanilla
        // version, and the launch quietly succeeded with no loader and no mods.
        if (instance.LoaderType != ModLoaderType.Vanilla && !LaunchReadiness.IsLoaderVersionId(id))
            throw new InvalidOperationException(
                $"{instance.LoaderType} 加载器没有安装完成：实例「{instance.Name}」当前指向的是原版 {id}。" +
                "请先在「实例管理」里点「补全文件」。");

        // MUST go through the resolver: a raw deserialize of a loader profile
        // yields NO arguments (no ${classpath} token) and NO vanilla libraries
        // — the launch then fails with "ClassNotFoundException: KnotClient".
        var version = await _resolver.ResolveAsync(id, ct).ConfigureAwait(false);

        var java = await GetJavaForAsync(instance, ct).ConfigureAwait(false);
        var creds = await _auth.GetLaunchCredentialsAsync(account, ct).ConfigureAwait(false);
        if (creds == null)
            throw new InvalidOperationException("账户凭据无效（登录可能已过期）。请重新选择或添加账户。");

        var cfg = LauncherConfig.Instance;

        // Round-83: automatic heap. Computed here (not in the UI) so every
        // launch path — button, first-run wizard, loader repair, CLI — gets the
        // same number, and so the value is always bounded by physical RAM.
        var (modCount, hasShader) = MemoryAllocationPolicy.Probe(instance.Directory);
        var physicalMb = MemoryAllocationPolicy.DetectPhysicalMb();
        var maxMemoryMb = MemoryAllocationPolicy.Effective(cfg.MaxMemoryMb, cfg.AutoMemory, physicalMb,
            modCount, hasShader, instance.LoaderType != ModLoaderType.Vanilla);
        // -Xms must never exceed -Xmx (the JVM refuses to start).
        var minMemoryMb = Math.Min(cfg.MinMemoryMb, maxMemoryMb);
        if (cfg.AutoMemory)
            _log.Info("Memory",
                $"自动分配内存：物理 {physicalMb} MB · {modCount} 个模组 · 光影 {(hasShader ? "有" : "无")} → -Xmx{maxMemoryMb}M");

        var baseJar = Path.Combine(cfg.VersionsDir, instance.MinecraftVersion, instance.MinecraftVersion + ".jar");
        var loaderJar = Path.Combine(cfg.VersionsDir, id, id + ".jar");
        var extra = new List<string>();
        if (File.Exists(loaderJar) && loaderJar != baseJar) extra.Add(loaderJar);

        var logConfig = Path.Combine(cfg.VersionsDir, id, "log_config.xml");
        var ctx = new LaunchContext
        {
            Version = version,
            JavaExecutable = java.JavaExecutable,
            VersionName = id,
            VersionType = instance.LoaderType.ToString().ToLowerInvariant(),
            GameDirectory = instance.Directory,
            AssetsRoot = cfg.AssetsDir,
            LibrariesRoot = cfg.LibrariesDir,
            NativesDirectory = _installer.NativesDir(id),
            ClientJarPath = File.Exists(baseJar) ? baseJar : loaderJar,
            ExtraClasspath = extra,
            AssetsIndexName = version.AssetIndex?.Id ?? version.Assets ?? "1.21",
            LoggingConfigPath = File.Exists(logConfig) ? logConfig : "",
            PlayerName = creds.PlayerName,
            Uuid = creds.Uuid,
            AccessToken = creds.AccessToken,
            UserType = creds.UserType,
            Xuid = creds.Xuid,
            UserProperties = creds.UserProperties,
            Width = cfg.Fullscreen ? 0 : cfg.WindowWidth,
            Height = cfg.Fullscreen ? 0 : cfg.WindowHeight,
            ExtraJvmArgs = cfg.ExtraJvmArgs,
            ExtraGameArgs = cfg.ExtraGameArgs,
            MinMemoryMb = minMemoryMb,
            MaxMemoryMb = maxMemoryMb
        };

        // Make sure natives are present.
        await _installer.ExtractNativesAsync(version, ctx.NativesDirectory).ConfigureAwait(false);

        return _builder.Build(ctx);
    }

    public async Task<int> LaunchAsync(LaunchSpec spec, Action<string>? onLog = null, CancellationToken ct = default,
        string? contextLine = null)
        => (await LaunchWithStatsAsync(spec, onLog, ct, contextLine).ConfigureAwait(false)).ExitCode;

    /// <summary>Round-79: same launch, plus what the performance dashboard needs —
    /// peak working set sampled from the JVM and FPS parsed out of its output.
    /// The dashboard shows "no FPS data" rather than a fabricated number, because
    /// vanilla clients simply do not print FPS.</summary>
    public async Task<LaunchOutcome> LaunchWithStatsAsync(LaunchSpec spec, Action<string>? onLog = null,
        CancellationToken ct = default, string? contextLine = null)
    {
        var gameLines = new List<string>(64);
        var sampler = new MemorySampler();
        var started = DateTime.Now;
        var exit = await _gameLauncher.LaunchAsync(spec, line =>
        {
            // Game output must land in the FILE log too — otherwise a crash
            // like "exited with code 1" is impossible to diagnose.
            _log.Info("Game", line);
            gameLines.Add(line);
            if (gameLines.Count > 200) gameLines.RemoveAt(0);
            onLog?.Invoke(line);
        }, ct, sampler).ConfigureAwait(false);

        var ran = DateTime.Now - started;
        if (exit != 0)
        {
            // Structured crash summary — users can paste this directly in a
            // bug report and it answers every question at once.
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("========== 崩溃摘要 (CRASH SUMMARY) ==========");
            sb.AppendLine($"退出码: {exit}");
            sb.AppendLine($"Java: {spec.JavaPath}");
            sb.AppendLine($"内存: {spec.MinMemoryMb}M - {spec.MaxMemoryMb}M");
            sb.AppendLine($"版本: {spec.VersionName}");
            if (!string.IsNullOrWhiteSpace(contextLine))
                sb.AppendLine($"实例: {contextLine}");
            // 把技术日志翻译成人话：规则匹配的"可能原因"。
            var reason = CrashAnalyzer.Analyze(gameLines)
                         ?? CrashAnalyzer.DescribeInstantExit(exit, ran, gameLines.Count);
            if (reason != null)
                sb.AppendLine($"可能原因: {reason}");
            sb.AppendLine($"运行时长: {ran.TotalSeconds:0} 秒 · 内存采样 {sampler.Samples} 次 · 峰值 {sampler.PeakMb} MB");
            sb.AppendLine($"-- 最后 {Math.Min(50, gameLines.Count)} 行游戏输出 --");
            foreach (var l in gameLines.Skip(Math.Max(0, gameLines.Count - 50)))
                sb.AppendLine(l);
            sb.AppendLine("=============================================");
            _log.Error("CrashSummary", sb.ToString());
        }

        var fps = PerformanceReport.ParseFps(gameLines);
        // No sample at all means no measurement — report "unknown", never 0 MB
        // dressed up as a value (that is what produced the famous "2 MB").
        var peakMb = sampler.Samples > 0 ? sampler.PeakMb : 0;
        return new LaunchOutcome(exit, peakMb, fps, (long)ran.TotalSeconds, sampler.Samples);
    }
}

/// <summary>What one launch produced, beyond the exit code. Round-92 adds the
/// run duration and the number of memory samples so the dashboard can tell a
/// real reading from a missed one.</summary>
public sealed record LaunchOutcome(int ExitCode, int PeakWorkingSetMb, FpsSummary Fps,
    long DurationSeconds = 0, int MemorySamples = 0);
