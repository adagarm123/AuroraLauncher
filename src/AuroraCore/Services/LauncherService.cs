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
            LauncherConfig.Instance.RootDirectory, java, onLog, ct, onStage).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException($"Mod loader installation failed: {result.Error}");

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
    /// <summary>Cheap check: is anything missing that would prevent launch?
    /// (client jar / version JSON / natives / loader version)</summary>
    public bool NeedsRepair(GameInstance instance)
    {
        // Single source of truth: the Evaluate function (see GameInstaller).
        var id = instance.ResolvedVersionId ?? instance.MinecraftVersion;
        if (instance.LoaderType == ModLoaderType.Vanilla)
            return !_installer.Evaluate(id, requireNatives: false).Complete;

        // Loader instance: vanilla part (no natives — extracted at launch) +
        // loader JSON + loader libraries.
        if (!_installer.Evaluate(instance.MinecraftVersion, requireNatives: false).Complete) return true;
        if (string.IsNullOrEmpty(id) || !File.Exists(Path.Combine(
                LauncherConfig.Instance.VersionsDir, id, id + ".json"))) return true;
        return !_installer.Evaluate(id, requireNatives: false).Complete;
    }

    /// <summary>Detailed state for UI/logging: what exactly is missing.</summary>
    public string DescribeMissing(GameInstance instance)
    {
        var id = instance.ResolvedVersionId ?? instance.MinecraftVersion;
        var parts = new List<string>();
        if (instance.LoaderType != ModLoaderType.Vanilla)
        {
            var vanilla = _installer.Evaluate(instance.MinecraftVersion, requireNatives: false);
            if (!vanilla.Complete) parts.Add($"{instance.MinecraftVersion}: {vanilla}");
            var loader = string.IsNullOrEmpty(id)
                ? null : _installer.Evaluate(id, requireNatives: false);
            if (loader != null && !loader.Complete) parts.Add($"{id}: {loader}");
        }
        else
        {
            var v = _installer.Evaluate(id, requireNatives: false);
            if (!v.Complete) parts.Add($"{id}: {v}");
        }
        return parts.Count == 0 ? "完整" : string.Join("；", parts);
    }

    /// <summary>Re-runs the install pipeline for whatever is missing. The
    /// download engine skips hash-verified existing files, so a healthy install
    /// completes quickly while missing files are re-downloaded.</summary>
    public async Task RepairAsync(GameInstance instance, Action<DownloadProgress>? onProgress,
        Action<string>? onLog, CancellationToken ct, Action<string>? onStage = null)
    {
        if (!_installer.IsFullyInstalled(instance.MinecraftVersion))
            await _installer.InstallAsync(instance.MinecraftVersion, onProgress, ct, true, onStage).ConfigureAwait(false);

        if (instance.LoaderType != ModLoaderType.Vanilla &&
            (string.IsNullOrEmpty(instance.ResolvedVersionId) || !_installer.IsVersionJsonInstalled(instance.ResolvedVersionId)))
        {
            onStage?.Invoke("installer-download");
            var java = await GetJavaForAsync(instance, ct).ConfigureAwait(false);
            var result = await _modLoaders.InstallAsync(instance.LoaderType, instance.MinecraftVersion, instance.LoaderVersion,
                LauncherConfig.Instance.RootDirectory, java, onLog, ct, onStage).ConfigureAwait(false);
            if (!result.Success)
                throw new InvalidOperationException($"Mod loader repair failed: {result.Error}");
            instance.ResolvedVersionId = result.VersionId;
            instance.LoaderVersion ??= result.VersionId;
        }
    }

    // ---------- java ----------
    public async Task<JavaInstallation> GetJavaForAsync(GameInstance instance, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(LauncherConfig.Instance.JavaPath) && File.Exists(LauncherConfig.Instance.JavaPath))
            return new JavaInstallation { JavaExecutable = LauncherConfig.Instance.JavaPath, Home = Path.GetDirectoryName(Path.GetDirectoryName(LauncherConfig.Instance.JavaPath)) ?? "", Version = "custom", Major = 21, Vendor = "custom" };

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

    // ---------- launch ----------
    public async Task<LaunchSpec> BuildLaunchSpecAsync(GameInstance instance, Account account, CancellationToken ct = default)
    {
        var id = instance.ResolvedVersionId ?? instance.MinecraftVersion;
        var jsonPath = Path.Combine(LauncherConfig.Instance.VersionsDir, id, id + ".json");
        if (!File.Exists(jsonPath)) throw new InvalidOperationException($"Version '{id}' is not installed. Please install it first.");

        // MUST go through the resolver: a raw deserialize of a loader profile
        // yields NO arguments (no ${classpath} token) and NO vanilla libraries
        // — the launch then fails with "ClassNotFoundException: KnotClient".
        var version = await _resolver.ResolveAsync(id, ct).ConfigureAwait(false);

        var java = await GetJavaForAsync(instance, ct).ConfigureAwait(false);
        var creds = await _auth.GetLaunchCredentialsAsync(account, ct).ConfigureAwait(false);

        var cfg = LauncherConfig.Instance;
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
            MinMemoryMb = cfg.MinMemoryMb,
            MaxMemoryMb = cfg.MaxMemoryMb
        };

        // Make sure natives are present.
        await _installer.ExtractNativesAsync(version, ctx.NativesDirectory).ConfigureAwait(false);

        return _builder.Build(ctx);
    }

    public async Task<int> LaunchAsync(LaunchSpec spec, Action<string>? onLog = null, CancellationToken ct = default,
        string? contextLine = null)
    {
        var gameLines = new List<string>(64);
        var exit = await _gameLauncher.LaunchAsync(spec, line =>
        {
            // Game output must land in the FILE log too — otherwise a crash
            // like "exited with code 1" is impossible to diagnose.
            _log.Info("Game", line);
            gameLines.Add(line);
            if (gameLines.Count > 200) gameLines.RemoveAt(0);
            onLog?.Invoke(line);
        }, ct).ConfigureAwait(false);

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
            var reason = CrashAnalyzer.Analyze(gameLines);
            if (reason != null)
                sb.AppendLine($"可能原因: {reason}");
            sb.AppendLine($"-- 最后 {Math.Min(50, gameLines.Count)} 行游戏输出 --");
            foreach (var l in gameLines.Skip(Math.Max(0, gameLines.Count - 50)))
                sb.AppendLine(l);
            sb.AppendLine("=============================================");
            _log.Error("CrashSummary", sb.ToString());
        }
        return exit;
    }
}
