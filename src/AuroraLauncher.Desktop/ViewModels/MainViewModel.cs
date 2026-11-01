using System.Collections.ObjectModel;
using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Threading;
using AuroraCore;
using AuroraCore.Download;
using AuroraCore.Models;
using AuroraCore.Services;
using AuroraCore.Utils;
using AuroraLauncher.Desktop.Views;
using AuroraLauncher.Desktop.Services;
using AuroraLauncher.ViewModels;

namespace AuroraLauncher.Desktop.ViewModels;

public class DownloadStatus : ViewModelBase
{
    private int _percent;
    private string _detail = "";
    private string _speed = "";
    private int _completed;
    private int _total;
    private bool _active;

    public int Percent { get => _percent; set => SetProperty(ref _percent, value); }
    public string Detail
    {
        get => _detail;
        set { if (SetProperty(ref _detail, value)) OnPropertyChanged(nameof(HasDetail)); }
    }
    public bool HasDetail => !string.IsNullOrWhiteSpace(_detail);
    public string Speed { get => _speed; set => SetProperty(ref _speed, value); }
    public int Completed { get => _completed; set { if (SetProperty(ref _completed, value)) OnPropertyChanged(nameof(Remaining)); } }
    public int Total { get => _total; set { if (SetProperty(ref _total, value)) OnPropertyChanged(nameof(Remaining)); } }
    public bool Active { get => _active; set => SetProperty(ref _active, value); }
    public int Remaining => Math.Max(0, Total - Completed);
}

/// <summary>One line of the install flow chart (PCL-style stage list).</summary>
public class StageItemVM : ViewModelBase
{
    public StageItemVM(string key, string label) { Key = key; Label = label; }

    public string Key { get; }
    public string Label { get; }

    private string _state = "Pending"; // Pending / Running / Done / Failed
    public string State
    {
        get => _state;
        set { if (SetProperty(ref _state, value)) { OnPropertyChanged(nameof(Marker)); } }
    }

    private string? _percentText;
    /// <summary>Percent shown while running ("13%"); null shows the animated ellipsis.</summary>
    public string? PercentText
    {
        get => _percentText;
        set { if (SetProperty(ref _percentText, value)) OnPropertyChanged(nameof(Marker)); }
    }

    /// <summary>Right-hand marker column content.</summary>
    public string Marker => State switch
    {
        "Done" => "✓",
        "Failed" => "✕",
        "Running" => string.IsNullOrEmpty(PercentText) ? "···" : PercentText!,
        _ => ""
    };
}

/// <summary>One entry of the loader-version dropdown (auto/placeholder/version).</summary>
public class LoaderVersionOptionVM
{
    public LoaderVersionOptionVM(string display, string? version, bool isPlaceholder = false)
    {
        Display = display;
        Version = version;
        IsPlaceholder = isPlaceholder;
    }

    public string Display { get; }
    public string? Version { get; }
    public bool IsPlaceholder { get; }
    public override string ToString() => Display;
}

/// <summary>A detected Java runtime shown in the settings page.</summary>
public class JavaInfoVM
{
    public JavaInfoVM(string? version, string? vendor, string path, int major)
    {
        Version = string.IsNullOrWhiteSpace(version) ? "未知版本" : version;
        Vendor = string.IsNullOrWhiteSpace(vendor) ? "" : vendor;
        Path = path;
        Major = major;
    }

    public string Version { get; }
    public string Vendor { get; }
    public string Path { get; }
    public int Major { get; }
    public string Display => $"Java {Version}{(string.IsNullOrEmpty(Vendor) ? "" : " · " + Vendor)}";
}

public class MainViewModel : ViewModelBase
{
    private readonly LauncherService _svc = new();
    private readonly Logger _log = Logger.Instance;
    private readonly Dispatcher _disp = Application.Current.Dispatcher;

    private string _status = "就绪";
    private bool _isBusy;
    private string _versionFilter = "release";
    private VersionManifestEntry? _selectedVersion;
    private GameInstance? _selectedInstance;
    private Account? _selectedAccount;
    private ModInfo? _selectedMod;
    private string _microsoftMessage = "";
    private AuthService.DeviceCodePrompt? _devicePrompt;
    private bool _darkTheme = true;
    private string _offlineName = "";
    private string _currentPageKey = "Home";
    private UIElement? _currentPage;
    private string _contentType = "模组";
    private string? _selectedWorld;

    public MainViewModel()
    {
        // Log page feed: lines are QUEUED and flushed in batches by a
        // dispatcher timer — a burst of hundreds of lines costs ONE ui update
        // instead of one per line (big smoothness win during installs/game
        // output). Trace stays file-only.
        _log.OnLog += e =>
        {
            if (e.Level == Logger.Level.Trace) return;
            EnqueueLog($"[{e.Level}] {e.Message}");
        };
        _logFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _logFlushTimer.Tick += (_, _) => FlushPendingLogs();
        _logFlushTimer.Start();
        ExportDiagnosticsCommand = new RelayCommand(async _ => await ExportDiagnosticsAsync());
        InstallFromCardsCommand = new RelayCommand(async _ => await InstallFromCardsAsync());
        OpenModrinthPageCommand = new RelayCommand(_ => OpenModrinthPage());
        FirstRunRescanCommand = new RelayCommand(async _ => await DetectJavaForFirstRunAsync());
        FirstRunOpenJavaSiteCommand = new RelayCommand(_ => OpenJavaDownloadSite());
        FirstRunNextCommand = new RelayCommand(_ => FirstRunStep = Math.Min(1, FirstRunStep + 1));
        FirstRunInstallCommand = new RelayCommand(async _ => await InstallRecommendedAsync());
        FirstRunSkipCommand = new RelayCommand(_ => FinishFirstRun());
        FirstRunDoneCommand = new RelayCommand(_ => FinishFirstRun());
        OpenModrinthPageCommand = new RelayCommand(_ => OpenModrinthPage());
        RefreshVersionsCommand = new RelayCommand(async _ => await LoadVersionsAsync());
        LaunchCommand = new RelayCommand(async _ => await LaunchAsync());
        AddOfflineAccountCommand = new RelayCommand(_ => AddOfflineAccount());
        MicrosoftBeginCommand = new RelayCommand(async _ => await BeginMicrosoftAsync());
        MicrosoftCompleteCommand = new RelayCommand(async _ => await CompleteMicrosoftAsync());
        CreateInstanceCommand = new RelayCommand(_ => CreateInstance());
        DeleteSelectedInstanceCommand = new RelayCommand(async _ => await DeleteSelectedInstanceAsync());
        ImportMinecraftCommand = new RelayCommand(async _ => await ImportMinecraftAsync());
        ImportLauncherCommand = new RelayCommand(async _ => await ImportLauncherAsync());
        ScanModsCommand = new RelayCommand(_ => ScanMods());
        ToggleModCommand = new RelayCommand(_ => ToggleMod());
        SearchContentCommand = new RelayCommand(async _ => { _searchPage = 0; await SearchContentAsync(); });
        ResetContentCommand = new RelayCommand(async _ => await ResetContentAsync());
        OpenContentDetailCommand = new RelayCommand(async o => await OpenContentDetailAsync(o as ContentItemVM));
        BackFromContentDetailCommand = new RelayCommand(_ => ShowContentDetail = false);
        InstallSpecificVersionCommand = new RelayCommand(async o => await InstallSpecificVersionAsync(o as VersionHistoryVM));
        SearchFirstPageCommand = new RelayCommand(async _ => await SearchFirstPageAsync());
        SearchPrevPageCommand = new RelayCommand(async _ => await SearchPrevPageAsync());
        SearchNextPageCommand = new RelayCommand(async _ => await SearchNextPageAsync());
        SearchLastPageCommand = new RelayCommand(async _ => await SearchLastPageAsync());
        SelectContentKindCommand = new RelayCommand(async o =>
        {
            if (o is string kind && ContentKind != kind)
            {
                ContentKind = kind;
                ShowContentDetail = false;
                if (kind == "本地管理") { ScanMods(); return; }
                _searchPage = 0;
                await LoadContentCategoriesAsync(ParseContentType(kind));
                await SearchContentAsync();
            }
        });
        InstallContentCommand = new RelayCommand(async o => await InstallContentAsync(o switch
        {
            ContentItemVM item => item.Project,
            ModrinthProject p => p,
            _ => null
        }));
        ImportMrpackCommand = new RelayCommand(_ => ImportMrpack());
        ApplySettingsCommand = new RelayCommand(_ => ApplySettings());
        OpenRootDirectoryCommand = new RelayCommand(_ => OpenRootDirectory());
        RenameInstanceCommand = new RelayCommand(_ => RenameInstance());
        EditNotesCommand = new RelayCommand(_ => EditNotes());
        ToggleFavoriteCommand = new RelayCommand(_ => ToggleFavorite());
        ChooseInstanceIconCommand = new RelayCommand(async _ => await ChooseInstanceIconAsync());
        OpenInstanceFolderCommand = new RelayCommand(_ => OpenInstanceFolder());
        OpenSavesFolderCommand = new RelayCommand(_ => OpenSavesFolder());
        OpenModsFolderCommand = new RelayCommand(_ => OpenModsFolder());
        ExportLaunchScriptCommand = new RelayCommand(async _ => await ExportLaunchScriptAsync());
        RepairInstanceCommand = new RelayCommand(async _ => await RepairInstanceAsync());
        SwitchThemeCommand = new RelayCommand(_ => SwitchTheme());
        CancelDownloadCommand = new RelayCommand(_ => CancelDownload());
        NavigateCommand = new RelayCommand(o => Navigate(o as string));
        UseJavaCommand = new RelayCommand(o => UseJava(o as string));
        ReloadJavaListCommand = new RelayCommand(async _ => await LoadJavaListAsync());
        CheckUpdateCommand = new RelayCommand(_ => _ = CheckForUpdateAsync(manual: true));

        // ---- 自动更新（round-57）：启动时若已下载好更新包，先换装再继续 ----
        if (UpdateService.HasStagedUpdate())
        {
            UpdateService.ApplyStagedAndRestart();
            Environment.Exit(0);
            return;
        }

        LoadAll();
        MaybeShowUpdateToast();
        RefreshMicrosoftLoginState();
        InitHelp();
        Navigate("Home");
        RefreshNetworkNote();
        _ = ProbeNetworkOnStartupAsync();
        _ = AutoCheckUpdateAsync();
        MaybeShowFirstRun();
    }

    private readonly UpdateService _update = new();
    private CancellationTokenSource? _updateCts;

    /// <summary>Silent startup check: download in the background when nothing
    /// else is running; every failure path is silent — an update check must
    /// never bother the user.</summary>
    private async Task AutoCheckUpdateAsync()
    {
        try
        {
            if (!LauncherConfig.Instance.AutoCheckUpdates) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var info = await _update.CheckAsync(timeout.Token).ConfigureAwait(true);
            if (info == null) return;
            if (IsBusy)
            {
                ShowToast($"发现新版本（第 {info.Round} 轮），当前有任务进行中，稍后可在「设置 → 检查更新」手动更新。");
                return;
            }
            ShowToast($"发现新版本（第 {info.Round} 轮），正在后台下载...");
            await DownloadUpdateAsync(info).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AddLog("[Update] 自动更新失败（不影响使用）: " + ex.Message);
        }
    }

    /// <summary>Manual "检查更新" from Settings: reports status clearly.</summary>
    private async Task CheckForUpdateAsync(bool manual)
    {
        try
        {
            Status = "正在检查更新...";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var info = await _update.CheckAsync(timeout.Token).ConfigureAwait(true);
            if (info == null)
            {
                Status = $"已是最新版本（第 {AuroraCore.BuildInfo.CurrentRound} 轮）";
                return;
            }
            Status = $"发现新版本（第 {info.Round} 轮），正在下载...";
            await DownloadUpdateAsync(info).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { Status = "已取消更新下载"; }
        catch (Exception ex) { Status = "更新失败: " + ex.Message; }
    }

    private async Task DownloadUpdateAsync(UpdateManifest info)
    {
        _updateCts = new CancellationTokenSource();
        try
        {
            await _update.DownloadStagedAsync(info, OnDownloadProgress, _updateCts.Token).ConfigureAwait(true);
            ShowToast("新版本已就绪：重启启动器即可自动完成更新。");
            Status = "新版本已就绪，重启启动器即可完成更新";
        }
        finally { _updateCts = null; }
    }

    // ---- 网络环境自动感知：启动时轻量探测官方 vs 镜像，用数据决定默认线路 ----
    private async Task ProbeNetworkOnStartupAsync()
    {
        try
        {
            // 尊重用户显式选择：仅当仍为默认（Official）时才自动切换。
            var probe = await NetworkProbe.ProbeAsync().ConfigureAwait(false);
            var cfg = LauncherConfig.Instance;
            var decided = probe.Decide(cfg.DownloadSource);
            if (cfg.DownloadSource == DownloadSource.Official && decided == DownloadSource.BMCLAPI)
            {
                cfg.DownloadSource = DownloadSource.BMCLAPI;
                cfg.NetworkNote = $"{probe}（国内网络：官方线路超时，已自动使用 BMCLAPI 镜像）";
                cfg.Save();
                RefreshNetworkNote();
                Dispatch(() => Status = "网络探测：官方线路不通，已自动切换 BMCLAPI 镜像");
                _log.Info("Net", $"Network probe: {probe}; auto-switched to BMCLAPI.");
            }
            else
            {
                cfg.NetworkNote = $"{probe}（线路保持不变）";
                _log.Info("Net", $"Network probe: {probe}; keeping {cfg.DownloadSource}.");
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Net", $"Network probe failed: {ex.Message}");
        }
    }

    // ---- 首次启动引导（Java 检测 → 推荐版本一键安装 → 启动） ----
    private int _firstRunStep;
    public int FirstRunStep
    {
        get => _firstRunStep;
        set
        {
            if (SetProperty(ref _firstRunStep, value))
            {
                OnPropertyChanged(nameof(FirstRunStep0Visible));
                OnPropertyChanged(nameof(FirstRunStep1Visible));
                OnPropertyChanged(nameof(FirstRunStep2Visible));
            }
        }
    }
    public bool FirstRunWizardVisible { get => _firstRunWizardVisible; private set => SetProperty(ref _firstRunWizardVisible, value); }
    private bool _firstRunWizardVisible;
    public bool FirstRunStep0Visible => FirstRunStep == 0;
    public bool FirstRunStep1Visible => FirstRunStep == 1;
    public bool FirstRunStep2Visible => FirstRunStep == 2;
    public string FirstRunJavaStatus { get => _firstRunJavaStatus; private set => SetProperty(ref _firstRunJavaStatus, value); }
    private string _firstRunJavaStatus = "正在检测 Java…";
    public string FirstRunRecommendation { get => _firstRunRecommendation; private set => SetProperty(ref _firstRunRecommendation, value); }
    private string _firstRunRecommendation = "正在获取推荐版本…";
    public string FirstRunProgress { get => _firstRunProgress; private set => SetProperty(ref _firstRunProgress, value); }
    private string _firstRunProgress = "";
    public bool FirstRunInstallEnabled { get => _firstRunInstallEnabled; private set => SetProperty(ref _firstRunInstallEnabled, value); }
    private bool _firstRunInstallEnabled = true;
    private string? _recommendedVersionId;

    private void MaybeShowFirstRun()
    {
        var cfg = LauncherConfig.Instance;
        if (cfg.FirstRunCompleted || Instances.Count > 0)
        {
            cfg.FirstRunCompleted = true; // 有实例的老用户不再弹引导
            return;
        }
        FirstRunStep = 0;
        FirstRunWizardVisible = true;
        _ = DetectJavaForFirstRunAsync();
        _ = LoadRecommendationForFirstRunAsync();
    }

    private async Task DetectJavaForFirstRunAsync()
    {
        FirstRunJavaStatus = "正在检测 Java…";
        try
        {
            var javas = await _svc.JavaFinder.FindAllAsync().ConfigureAwait(true);
            FirstRunJavaStatus = javas.Count > 0
                ? $"✓ 已发现 {javas.Count} 个 Java 运行时（将自动选择最合适的版本）。"
                : "✗ 未检测到 Java。请安装 Java 21+（Adoptium 提供免费安装包），安装完成后点击「重新检测」。";
        }
        catch (Exception ex)
        {
            FirstRunJavaStatus = "检测失败: " + ex.Message;
        }
    }

    private void OpenJavaDownloadSite()
    {
        try { Process.Start(new ProcessStartInfo("https://adoptium.net/temurin/releases/?os=windows") { UseShellExecute = true }); }
        catch (Exception ex) { Status = "无法打开浏览器: " + ex.Message; }
    }

    private async Task LoadRecommendationForFirstRunAsync()
    {
        try
        {
            var manifest = await _svc.GetManifestAsync().ConfigureAwait(true);
            var latest = manifest.Latest.Release;
            _recommendedVersionId = latest;
            FirstRunRecommendation = $"推荐安装 Minecraft {latest}（最新正式版，纯原版、最稳定）。\n安装完成后可随时到「游戏下载」页加装 Fabric/Forge 等加载器，或到「内容中心」安装 Mod。";
        }
        catch (Exception ex)
        {
            FirstRunRecommendation = "暂时无法获取推荐版本（网络原因）。你可以先完成引导，稍后在「游戏下载」页手动选择。";
            _log.Warn("FirstRun", $"Failed to fetch latest release: {ex.Message}");
        }
    }

    /// <summary>Wizard step 2: install the recommended vanilla version using
    /// the SAME pipeline as the download page (progress on the flow chart).</summary>
    private async Task InstallRecommendedAsync()
    {
        if (string.IsNullOrEmpty(_recommendedVersionId)) { FirstRunProgress = "推荐版本尚未获取，请稍候或跳过引导稍后安装。"; return; }
        FirstRunInstallEnabled = false;
        try
        {
            _cts = new CancellationTokenSource();
            var mc = _recommendedVersionId;
            var name = $"Minecraft {mc}";
            var inst = _svc.Instances.Create(name, mc, ModLoaderType.Vanilla, null);
            Instances.Add(inst);
            SelectedInstance = inst;
            InitStages(BuildStages(ModLoaderType.Vanilla), $"{name} 安装");
            Navigate("Downloads");
            FirstRunProgress = "正在安装（进度见下方流程图）…";
            await _svc.InstallWithLoaderAsync(inst, OnDownloadProgress,
                s => Dispatch(() => AddLog("[Install] " + s)), _cts.Token, OnInstallStage);
            _svc.Instances.Update(inst);
            CompleteStages();
            InstallSuccess = $"实例「{inst.Name}」安装完成！";
            FirstRunProgress = "安装完成！";
            FinishFirstRun();
        }
        catch (OperationCanceledException) { FirstRunProgress = "已取消。"; }
        catch (Exception ex)
        {
            FailStages(ex.Message);
            FirstRunProgress = "安装失败: " + ex.Message + "（可跳过引导，稍后在「游戏下载」页重试）";
        }
        finally { FirstRunInstallEnabled = true; }
    }

    private void FinishFirstRun()
    {
        FirstRunWizardVisible = false;
        var cfg = LauncherConfig.Instance;
        cfg.FirstRunCompleted = true;
        cfg.Save();
    }


    // ---- collections ----
    public ObservableCollection<VersionManifestEntry> Versions { get; } = new();
    public ObservableCollection<GameInstance> Instances { get; } = new();
    public ObservableCollection<Account> Accounts { get; } = new();
    public ObservableCollection<ModInfo> Mods { get; } = new();
    public ObservableCollection<string> Logs { get; } = new();
    public ObservableCollection<ContentItemVM> ModSearchResults { get; } = new();
    public ObservableCollection<string> Worlds { get; } = new();
    public ObservableCollection<StageItemVM> InstallStages { get; } = new();

    // ---- PCL-style "install new game" loader cards ----
    public ObservableCollection<LoaderCardVM> LoaderCards { get; } = new();

    private string _newInstanceName = "";
    /// <summary>Optional instance name for the new install (defaults to MC version).</summary>
    public string NewInstanceName { get => _newInstanceName; set => SetProperty(ref _newInstanceName, value); }

    private readonly GenerationGate _loaderCardsGate = new();
    /// <summary>Generation of the card set currently in LoaderCards — picks
    /// from cards of any older generation are stale and must be ignored
    /// (WPF pushes selections into detached cards asynchronously; a
    /// collection-membership check is unreliable for that).</summary>
    private long _activeCardsGen = -1;

    // ---- global non-blocking toast (round-48) ----
    private string? _toastMessage;
    /// <summary>Auto-fading toast text (MainWindow bottom-right overlay).
    /// For "user must know, but must not be interrupted" notices — e.g. a
    /// Fabric API prerequisite that was auto-installed on their behalf.</summary>
    public string? ToastMessage { get => _toastMessage; set => SetProperty(ref _toastMessage, value); }
    private System.Windows.Threading.DispatcherTimer? _toastTimer;

    private void ShowToast(string message)
    {
        Dispatch(() =>
        {
            ToastMessage = message;
            if (_toastTimer == null)
            {
                _toastTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                _toastTimer.Tick += (s, e) => { _toastTimer.Stop(); ToastMessage = null; };
            }
            _toastTimer.Stop();
            _toastTimer.Start();
        });
    }

    /// <summary>(Re)builds the loader cards for the chosen MC version: each card
    /// offers 不安装 / 自动选择最新 / the full historical version list.
    /// Anti-lag (round-47): the four card skeletons render IMMEDIATELY on
    /// selection (loading state), and each loader's version list fills its card
    /// as soon as that fetch completes — no more "pick a version, the loader
    /// panel pops in seconds later" while Forge metadata crawls in. A
    /// generation gate (GenerationGate, unit-tested) discards stale fetches
    /// when the user switches versions faster than the network answers.</summary>
    public async Task LoadLoaderCardsAsync()
    {
        var mc = SelectedVersion?.Id ?? SelectedInstance?.MinecraftVersion;
        if (string.IsNullOrWhiteSpace(mc)) return;
        var gen = _loaderCardsGate.Begin();

        var defs = new (ModLoaderType Type, string Name, string Short)[]
        {
            (ModLoaderType.Forge, "Forge", "FO"),
            (ModLoaderType.NeoForge, "NeoForge", "NF"),
            (ModLoaderType.Fabric, "Fabric", "FA"),
            (ModLoaderType.Quilt, "Quilt", "QU"),
        };

        // 1) Show the skeleton cards NOW (no network wait): instant feedback.
        Dispatch(() =>
        {
            _activeCardsGen = gen;
            LoaderCards.Clear();
            foreach (var d in defs)
            {
                var skeleton = new LoaderCardVM(d.Type, d.Name, d.Short, OnLoaderVersionPicked, gen);
                skeleton.Versions.Add(LoaderCardVM.NoneChoice);
                skeleton.Versions.Add(LoaderCardVM.AutoChoice);
                skeleton.IsLoading = true;
                LoaderCards.Add(skeleton);
            }
        });

        // 2) Fetch all loaders in parallel; each call already hedges across routes.
        var tasks = defs.Select(async d =>
        {
            try
            {
                var infos = await _svc.ModLoaders.ListVersionInfosAsync(d.Type, mc).ConfigureAwait(true);
                return (d, infos);
            }
            catch { return (d, new List<LoaderVersionInfo>()); }
        }).ToList();

        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks).ConfigureAwait(true);
            tasks.Remove(done);
            var (def, infos) = await done;
            if (!_loaderCardsGate.IsCurrent(gen)) return; // stale: a newer selection took over

            Dispatch(() =>
            {
                var card = LoaderCards.FirstOrDefault(c => c.Loader == def.Type);
                if (card == null) return;
                // Round-50 fix: the skeleton already contains 不安装 /
                // （自动选择最新） — APPEND the fetched versions only.
                // Clearing here reset the ComboBox selection to null
                // mid-flight, which (pre-coercion) auto-"selected" cards the
                // user never touched and blanked the picked value.
                foreach (var i in infos.Take(50))
                    card.Versions.Add(i.Stable ? i.Version : i.Version + LoaderCardVM.TestSuffix);
                card.Empty = infos.Count == 0;
                card.IsLoading = false;
            });
        }
        if (!_loaderCardsGate.IsCurrent(gen)) return;
        UpdateCardLocks();
    }

    /// <summary>Mutual exclusion: an instance carries at most one mod loader.
    /// Picking a version on one card resets all other cards to 不安装 and
    /// locks their dropdowns until the choice is cleared.</summary>
    private void OnLoaderVersionPicked(LoaderCardVM source)
    {
        // Round-53: reject picks from cards of an older generation — when the
        // card set is rebuilt, WPF may push a null selection into the OLD card
        // objects ASYNCHRONOUSLY (possibly before/after removal), so a
        // collection-membership check is unreliable; the monotonic generation
        // tag is the dependable signal.
        if (source.Generation != _activeCardsGen) return;
        if (source.HasVersion)
        {
            foreach (var c in LoaderCards)
                if (c != source && c.HasVersion)
                    c.SelectedVersion = LoaderCardVM.NoneChoice;
            Status = $"已选择 {source.Name} 加载器（一个实例只能安装一个模组加载器）";
        }
        UpdateCardLocks();
    }

    /// <summary>Locks every card that does not currently hold the loader pick.</summary>
    private void UpdateCardLocks()
    {
        var any = LoaderCards.Any(c => c.HasVersion);
        foreach (var c in LoaderCards)
            c.IsLocked = any && !c.HasVersion;
    }

    /// <summary>Installs the MC version + at most one loader chosen on the cards
    /// (PCL-style "安装新游戏").</summary>
    public async Task InstallFromCardsAsync()
    {
        if (IsBusy) { Status = "有任务正在进行中，请等待完成或取消后再试"; return; }
        var mc = SelectedVersion?.Id ?? SelectedInstance?.MinecraftVersion;
        if (string.IsNullOrWhiteSpace(mc)) { Status = "请先在左侧选择一个游戏版本"; return; }

        var picked = LoaderCards.Where(c => c.HasVersion).ToList();
        if (picked.Count > 1)
        {
            Status = "一次只能安装一个加载器：请将其它加载器改回「不安装」";
            return;
        }
        var loader = picked.Count == 1 ? picked[0].Loader : ModLoaderType.Vanilla;
        var loaderVersion = picked.Count == 1 && !picked[0].IsAutoPick ? picked[0].SelectedVersion : null;
        var name = string.IsNullOrWhiteSpace(NewInstanceName) ? $"{mc} ({loader})" : NewInstanceName.Trim();

        try
        {
            IsBusy = true;
            _cts = new CancellationTokenSource();
            InitStages(BuildStages(loader), $"{name} 安装");
            Status = $"正在安装 {mc} ({loader}) ...";
            var inst = _svc.Instances.Create(name, mc, loader, loaderVersion);
            Instances.Add(inst);
            SelectedInstance = inst;
            Navigate("Downloads");
            await _svc.InstallWithLoaderAsync(inst, OnDownloadProgress,
                s => Dispatch(() => AddLog("[Install] " + s)), _cts.Token, OnInstallStage);
            _svc.Instances.Update(inst);
            CompleteStages();
            // Fabric 前置（round-47）：装完 Fabric 实例即检查 Fabric API ——
            // 设置开启（默认）时告知后自动安装适配版本，关闭时弹窗询问。
            var fabricApiInstalled = false;
            if (loader == ModLoaderType.Fabric)
                fabricApiInstalled = await EnsureFabricApiAsync(inst, _cts.Token, $"安装实例（Fabric 加载器）");
            ScanMods();
            InstallSuccess = $"实例「{inst.Name}」安装完成！到首页点「启动游戏」即可游玩。"
                + (fabricApiInstalled ? "（已自动安装适配的 Fabric API 前置）" : "");
            Status = $"安装完成: {inst.Name}" + (fabricApiInstalled ? "（含 Fabric API 前置）" : "");
        }
        catch (OperationCanceledException) { Status = "已取消下载（未完成的临时文件已清理）"; }
        catch (Exception ex) { FailStages(ex.Message); Status = "安装失败: " + ex.Message; }
        finally { IsBusy = false; Download.Active = false; }
    }

    public DownloadStatus Download { get; } = new();

    // ---- properties ----
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }
    public string VersionFilter { get => _versionFilter; set { if (SetProperty(ref _versionFilter, value)) _ = LoadVersionsAsync(); } }
    public VersionManifestEntry? SelectedVersion
    {
        get => _selectedVersion;
        set { if (SetProperty(ref _selectedVersion, value)) _ = LoadLoaderCardsAsync(); }
    }
    public GameInstance? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (SetProperty(ref _selectedInstance, value))
            {
                ScanMods();
                LoadWorlds();
                OnPropertyChanged(nameof(InstanceSummary));
                OnPropertyChanged(nameof(SelectedInstanceCategory));
                OnPropertyChanged(nameof(SelectedInstanceIconChoice));
                OnPropertyChanged(nameof(SelectedInstanceFavoriteText));
                OnPropertyChanged(nameof(SelectedInstanceNotes));
                OnPropertyChanged(nameof(SelectedInstanceIcon));
                OnPropertyChanged(nameof(HasSelectedInstanceIcon));
            }
        }
    }
    public Account? SelectedAccount { get => _selectedAccount; set => SetProperty(ref _selectedAccount, value); }
    public ModInfo? SelectedMod { get => _selectedMod; set => SetProperty(ref _selectedMod, value); }
    public string MicrosoftMessage { get => _microsoftMessage; set => SetProperty(ref _microsoftMessage, value); }
    public bool DarkTheme { get => _darkTheme; set => SetProperty(ref _darkTheme, value); }
    public string OfflineName { get => _offlineName; set => SetProperty(ref _offlineName, value); }
    public string CurrentPageKey { get => _currentPageKey; set => SetProperty(ref _currentPageKey, value); }
    public UIElement? CurrentPage { get => _currentPage; set => SetProperty(ref _currentPage, value); }

    private string _installTitle = "";
    /// <summary>Header of the install flow chart, e.g. "1.21 (NeoForge) 安装".</summary>
    public string InstallTitle { get => _installTitle; set => SetProperty(ref _installTitle, value); }

    public List<string> VersionFilterOptions => new() { "release", "snapshot", "all" };
    public List<string> DownloadSourceOptions => new() { "官方", "BMCLAPI" };
    public List<string> ContentTypeOptions => new() { "模组", "整合包", "数据包", "光影", "资源包" };

    /// <summary>Selected content kind for the Modrinth search page.</summary>
    public string ContentKind
    {
        get => _contentType;
        set => SetProperty(ref _contentType, value);
    }

    /// <summary>Target world for datapack installs.</summary>
    public string? SelectedWorld
    {
        get => _selectedWorld;
        set => SetProperty(ref _selectedWorld, value);
    }

    private static ContentType ParseContentType(string s) => s switch
    {
        "整合包" => ContentType.Modpack,
        "数据包" => ContentType.Datapack,
        "光影" or "光影包" => ContentType.Shader,
        "资源包" => ContentType.ResourcePack,
        _ => ContentType.Mod
    };

    /// <summary>Human-readable summary of the auto-fit target instance.</summary>
    public string InstanceSummary => SelectedInstance == null
        ? "未选择实例"
        : $"{SelectedInstance.MinecraftVersion} · {SelectedInstance.LoaderType}";

    // ---- commands ----
    public RelayCommand RefreshVersionsCommand { get; }
    public RelayCommand ExportDiagnosticsCommand { get; }
    public RelayCommand InstallFromCardsCommand { get; }
    public RelayCommand OpenModrinthPageCommand { get; }
    public RelayCommand FirstRunRescanCommand { get; }
    public RelayCommand FirstRunOpenJavaSiteCommand { get; }
    public RelayCommand FirstRunNextCommand { get; }
    public RelayCommand FirstRunInstallCommand { get; }
    public RelayCommand FirstRunSkipCommand { get; }
    public RelayCommand FirstRunDoneCommand { get; }
    public RelayCommand LaunchCommand { get; }
    public RelayCommand AddOfflineAccountCommand { get; }
    public RelayCommand MicrosoftBeginCommand { get; }
    public RelayCommand MicrosoftCompleteCommand { get; }
    public RelayCommand CreateInstanceCommand { get; }
    public RelayCommand DeleteSelectedInstanceCommand { get; }
    public RelayCommand ImportMinecraftCommand { get; }
    public RelayCommand ImportLauncherCommand { get; }
    public RelayCommand ScanModsCommand { get; }
    public RelayCommand ToggleModCommand { get; }
    public RelayCommand SearchContentCommand { get; }
    public RelayCommand ResetContentCommand { get; }
    public RelayCommand SelectContentKindCommand { get; }
    public RelayCommand OpenContentDetailCommand { get; }
    public RelayCommand BackFromContentDetailCommand { get; }
    public RelayCommand SearchFirstPageCommand { get; }
    public RelayCommand SearchPrevPageCommand { get; }
    public RelayCommand SearchNextPageCommand { get; }
    public RelayCommand SearchLastPageCommand { get; }
    public RelayCommand InstallSpecificVersionCommand { get; }
    public RelayCommand InstallContentCommand { get; }
    public RelayCommand ImportMrpackCommand { get; }
    public RelayCommand ApplySettingsCommand { get; }
    public RelayCommand OpenRootDirectoryCommand { get; }
    public RelayCommand RenameInstanceCommand { get; }
    public RelayCommand EditNotesCommand { get; }
    public RelayCommand ToggleFavoriteCommand { get; }
    public RelayCommand ChooseInstanceIconCommand { get; }
    public RelayCommand OpenInstanceFolderCommand { get; }
    public RelayCommand OpenSavesFolderCommand { get; }
    public RelayCommand OpenModsFolderCommand { get; }
    public RelayCommand ExportLaunchScriptCommand { get; }
    public RelayCommand RepairInstanceCommand { get; }
    public RelayCommand SwitchThemeCommand { get; }
    public RelayCommand CancelDownloadCommand { get; }
    public RelayCommand NavigateCommand { get; }
    public RelayCommand UseJavaCommand { get; }
    public RelayCommand ReloadJavaListCommand { get; }
    public RelayCommand CheckUpdateCommand { get; }

    private CancellationTokenSource? _cts;

    // ---- methods ----
    /// <summary>UI-thread dispatch. Uses BeginInvoke so high-frequency progress
    /// callbacks from worker threads can never stall the render loop.</summary>
    private void Dispatch(Action a)
    {
        if (_disp.CheckAccess()) a();
        else _disp.BeginInvoke(a);
    }

    private void LoadAll()
    {
        foreach (var a in _svc.Auth.LoadAccounts()) Accounts.Add(a);
        foreach (var i in _svc.Instances.LoadAll()) Instances.Add(i);
        SelectedAccount = Accounts.FirstOrDefault();
        SelectedInstance = Instances.FirstOrDefault();
        var c = LauncherConfig.Instance;
        MemoryMb = c.MaxMemoryMb;
        JavaPath = c.JavaPath ?? "";
        DownloadSourceText = c.DownloadSource == DownloadSource.BMCLAPI ? "BMCLAPI" : "官方";
        ProxyUrl = c.ProxyUrl ?? "";
        MicrosoftClientIdText = c.MicrosoftClientId ?? "";
        AutoInstallFabricApi = c.AutoInstallFabricApi;
        AutoCheckUpdates = c.AutoCheckUpdates;
        WindowW = c.WindowWidth; WindowH = c.WindowHeight;
        ExtraJvm = c.ExtraJvmArgs;
        RootDirectoryText = c.RootDirectory;
        DarkTheme = c.Theme != ThemePreference.Light;
    }

    private void SaveAccounts() => _svc.Auth.SaveAccounts(Accounts.ToList());

    private string _versionSearchText = "";
    /// <summary>Filters the official version list by id substring (live).</summary>
    public string VersionSearchText { get => _versionSearchText; set { if (SetProperty(ref _versionSearchText, value)) _ = LoadVersionsAsync(); } }

    // ---- help center (PCL-style, offline) ----
    public ObservableCollection<HelpTopicVM> HelpTopics { get; } = new();
    public ICollectionView? HelpTopicsView { get; private set; }

    private string _helpSearchText = "";
    public string HelpSearchText
    {
        get => _helpSearchText;
        set { if (SetProperty(ref _helpSearchText, value)) HelpTopicsView?.Refresh(); }
    }

    private HelpTopicVM? _selectedHelpTopic;
    public HelpTopicVM? SelectedHelpTopic { get => _selectedHelpTopic; set => SetProperty(ref _selectedHelpTopic, value); }

    private void InitHelp()
    {
        foreach (var t in HelpContent.Topics()) HelpTopics.Add(t);
        var view = CollectionViewSource.GetDefaultView(HelpTopics);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HelpTopicVM.Category)));
        view.Filter = o => o is HelpTopicVM t && (
            string.IsNullOrWhiteSpace(HelpSearchText) ||
            t.Title.Contains(HelpSearchText, StringComparison.OrdinalIgnoreCase) ||
            t.Summary.Contains(HelpSearchText, StringComparison.OrdinalIgnoreCase) ||
            t.Body.Contains(HelpSearchText, StringComparison.OrdinalIgnoreCase));
        HelpTopicsView = view;
        SelectedHelpTopic = HelpTopics.FirstOrDefault();
    }

    // ---- Java environment (settings) ----
    public ObservableCollection<JavaInfoVM> JavaList { get; } = new();

    public async Task LoadJavaListAsync()
    {
        try
        {
            var all = await _svc.FindJavaAsync().ConfigureAwait(true);
            Dispatch(() =>
            {
                JavaList.Clear();
                foreach (var j in all.DistinctBy(j => j.JavaExecutable))
                    JavaList.Add(new JavaInfoVM(j.Version, j.Vendor, j.JavaExecutable, j.Major));
            });
        }
        catch (Exception ex) { Status = "Java 检测失败: " + ex.Message; }
    }

    /// <summary>Selects a detected Java as the launcher's Java (persisted).</summary>
    public void UseJava(object? o)
    {
        if (o is not JavaInfoVM j) return;
        JavaPath = j.Path;
        var c = LauncherConfig.Instance;
        c.JavaPath = j.Path;
        c.Save();
        Status = $"已指定 Java {j.Version}（{j.Major}）为启动器默认 Java";
    }

    /// <summary>Round-56: one-time non-blocking "已更新" hint — if the
    /// changelog has entries newer than the last round the user has seen,
    /// toast once on startup; opening the 更新日志 page marks it read.</summary>
    private void MaybeShowUpdateToast()
    {
        var c = LauncherConfig.Instance;
        if (c.ChangelogSeenRound >= HelpContent.LatestChangelogRound) return;
        ShowToast($"启动器已更新（更新日志至第 {HelpContent.LatestChangelogRound} 轮），左侧「更新日志」页可查看本次变化。");
    }

    /// <summary>One instance per page, built on first visit and reused
    /// (round-55). Re-creating the whole XAML tree on EVERY sidebar click
    /// blocked the UI thread — rapid navigation froze the window ("未响应").
    /// Views are stateless wrappers around this VM and their Loaded/Unloaded
    /// hooks are re-entrant, so reuse is safe.</summary>
    private readonly Dictionary<string, UIElement> _pageCache = new();

    public void Navigate(string? key)
    {
        key ??= "Home";
        CurrentPageKey = key;
        // Visiting the changelog page marks the "已更新" toast as read.
        if (key == "Changelog" && LauncherConfig.Instance.ChangelogSeenRound < HelpContent.LatestChangelogRound)
        {
            LauncherConfig.Instance.ChangelogSeenRound = HelpContent.LatestChangelogRound;
            LauncherConfig.Instance.Save();
        }
        if (_pageCache.TryGetValue(key, out var cached))
        {
            CurrentPage = cached;
            return;
        }
        UserControl page = key switch
        {
            "Home" => new HomeView(),
            "Download" => new DownloadView(),
            "Instances" => new InstancesView(),
            "Mods" => new ModsView(),
            "Downloads" => new DownloadsView(),
            "Help" => new HelpView(),
            "Settings" => new SettingsView(),
            "Logs" => new LogsView(),
            "Changelog" => new ChangelogView(),
            _ => new HomeView()
        };
        _pageCache[key] = page;
        CurrentPage = page;
    }

    public int MemoryMb { get; set; }
    public string JavaPath { get; set; } = "";
    public string DownloadSourceText { get; set; } = "官方";
    public string ProxyUrl { get; set; } = "";
    public string MicrosoftClientIdText { get; set; } = "";
    /// <summary>安装 Fabric 实例时自动安装适配的 Fabric API 前置（默认开启）。</summary>
    public bool AutoInstallFabricApi { get; set; } = true;
    /// <summary>启动时自动检查更新（默认开启）。</summary>
    public bool AutoCheckUpdates { get; set; } = true;
    public int WindowW { get; set; } = 854;
    public int WindowH { get; set; } = 480;
    public string ExtraJvm { get; set; } = "";
    public string RootDirectoryText { get; set; } = "";

    public async Task LoadVersionsAsync()
    {
        try
        {
            IsBusy = true;
            var manifest = await _svc.GetManifestAsync();
            var list = VersionFilter == "all"
                ? manifest.Versions
                : manifest.Versions.Where(v => v.Type == VersionFilter).ToList();
            if (!string.IsNullOrWhiteSpace(VersionSearchText))
                list = list.Where(v => v.Id.Contains(VersionSearchText, StringComparison.OrdinalIgnoreCase)).ToList();
            Dispatch(() =>
            {
                Versions.Clear();
                foreach (var v in list) Versions.Add(v);
            });
            Status = $"已加载 {list.Count} 个版本";
        }
        catch (Exception ex) { Status = "加载版本失败: " + ex.Message; }
        finally { IsBusy = false; }
    }

    private DateTime _lastProgressUi = DateTime.MinValue;

    private string? _networkNoteDisplay;
    /// <summary>Network-probe result shown in Settings ("当前网络：…").</summary>
    public string? NetworkNoteDisplay
    {
        get => _networkNoteDisplay;
        private set => SetProperty(ref _networkNoteDisplay, value);
    }

    private void RefreshNetworkNote()
    {
        var note = LauncherConfig.Instance.NetworkNote;
        Dispatch(() => NetworkNoteDisplay = string.IsNullOrWhiteSpace(note) ? null : "当前网络：" + note);
    }

    private void OnDownloadProgress(DownloadProgress p)
    {
        // Throttle: the engine reports per read-chunk; 10 ui updates/sec is
        // plenty and keeps the dispatcher free during download storms.
        var now = DateTime.UtcNow;
        var final = p.TotalFiles > 0 && p.CompletedFiles >= p.TotalFiles;
        if (!final && (now - _lastProgressUi).TotalMilliseconds < 100) return;
        _lastProgressUi = now;

        Dispatch(() =>
        {
            Download.Total = p.TotalFiles;
            Download.Completed = p.CompletedFiles;
            Download.Speed = FormatSpeed(p.SpeedBytesPerSec);
            Download.Active = true;
            Download.Detail = p.Message ?? p.Current?.DisplayName ?? "";

            // Stage-aware overall percent: (completed stages + current stage fraction) / total stages.
            double stageFrac = p.TotalFiles > 0 ? (double)p.CompletedFiles / p.TotalFiles : 1.0;
            if (_currentStageIndex < 0 || _stageOrder.Count == 0)
                Download.Percent = (int)(stageFrac * 100);
            else
                Download.Percent = (int)Math.Round((_currentStageIndex + stageFrac) / _stageOrder.Count * 100);

            // Show per-stage percent in the running stage row (no per-file details).
            if (_currentStageIndex >= 0 && _currentStageIndex < InstallStages.Count)
            {
                var st = InstallStages[_currentStageIndex];
                if (st.State == "Running")
                    st.PercentText = p.TotalFiles > 0 ? $"{(int)Math.Round(stageFrac * 100)}%" : null;
            }
        });
    }

    // ---- install flow chart (PCL-style stages) ----
    private List<string> _stageOrder = new();
    private int _currentStageIndex = -1;

    private static List<(string Key, string Label)> BuildStages(ModLoaderType loader)
    {
        var s = new List<(string, string)>
        {
            ("json", "下载原版 json 文件"),
            ("client", "下载原版客户端文件"),
            ("libraries", "下载原版支持库文件"),
            ("assets", "下载原版资源文件"),
            ("natives", "提取本地库文件")
        };
        if (loader != ModLoaderType.Vanilla)
        {
            if (loader is ModLoaderType.Fabric or ModLoaderType.Quilt)
            {
                // Installer-free path: profile JSON from the meta API.
                s.Add(("installer-download", "获取加载器版本清单"));
                s.Add(("installer-run", $"写入 {loader} 加载器版本"));
            }
            else
            {
                s.Add(("installer-download", "获取加载器安装器"));
                s.Add(("installer-run", $"安装 {loader} 加载器"));
            }
        }
        return s;
    }

    private void InitStages(IEnumerable<(string Key, string Label)> stages, string title)
    {
        _stageOrder = stages.Select(s => s.Key).ToList();
        _currentStageIndex = -1;
        Dispatch(() =>
        {
            InstallTitle = title;
            InstallError = null;
            InstallSuccess = null;
            InstallStages.Clear();
            foreach (var (key, label) in stages) InstallStages.Add(new StageItemVM(key, label));
        });
    }

    private void OnInstallStage(string key)
    {
        Dispatch(() =>
        {
            var idx = _stageOrder.IndexOf(key);
            if (idx < 0) return;
            _currentStageIndex = idx;
            for (int i = 0; i < InstallStages.Count; i++)
            {
                var st = InstallStages[i];
                if (i < idx) { st.State = "Done"; st.PercentText = "100%"; }
                else if (i == idx) { st.State = "Running"; st.PercentText = null; }
                else { st.State = "Pending"; st.PercentText = null; }
            }
            Status = $"正在安装… {InstallStages[idx].Label}";
        });
    }

    private void CompleteStages()
    {
        Dispatch(() =>
        {
            foreach (var st in InstallStages) { st.State = "Done"; st.PercentText = "100%"; }
            Download.Percent = 100;
            Download.Active = false;
            // 收尾归零：速度不再停留在最后一次采样值。
            Download.Speed = "0 B/s";
            Download.Detail = "";
        });
    }

    private string? _installSuccess;
    /// <summary>Success banner text shown on the download page after a
    /// completed install (green panel with launch hint).</summary>
    public string? InstallSuccess
    {
        get => _installSuccess;
        private set => SetProperty(ref _installSuccess, value);
    }

    /// <summary>Marks the running stage as failed (red ✕) and resets the
    /// download stats — without this a failed install left the flow chart
    /// showing "···" forever, looking like it was stuck.</summary>
    private void FailStages(string? error = null)
    {
        Dispatch(() =>
        {
            var running = InstallStages.FirstOrDefault(s => s.State == "Running");
            if (running != null)
            {
                running.State = "Failed";
                running.PercentText = null;
            }
            InstallError = error;
            InstallSuccess = null;
            Download.Active = false;
            Download.Speed = "0 B/s";
        });
    }

    private string? _installError;
    /// <summary>Failure reason shown under the stage list.</summary>
    public string? InstallError
    {
        get => _installError;
        private set => SetProperty(ref _installError, value);
    }

    private static string FormatSpeed(double bps)
    {
        if (bps <= 0) return "0 B/s";
        string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
        int i = 0; double v = bps;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:F1} {units[i]}";
    }

    public async Task LaunchAsync()
    {
        if (SelectedInstance == null) { Status = "请先选择实例"; return; }
        if (SelectedAccount == null) { Status = "请先选择/添加账户"; return; }
        if (IsBusy) { Status = "有任务正在进行中，请等待完成或取消后再试"; return; }
        try
        {
            IsBusy = true;
            var instance = SelectedInstance;
            _cts = new CancellationTokenSource();

            // ---- 启动前自动扫描：文件不完整才补全并跳转下载页；完整则直接启动 ----
            var missing = _svc.DescribeMissing(instance);
            if (_svc.NeedsRepair(instance))
            {
                Status = $"正在扫描并补全 {instance.Name} 的游戏文件...（缺: {missing}）";
                Dispatch(() => AddLog("[Repair] 缺少文件: " + missing));
                InitStages(BuildStages(instance.LoaderType), $"{instance.Name} 文件补全");
                Navigate("Downloads");
                await _svc.RepairAsync(instance, OnDownloadProgress,
                    s => Dispatch(() => AddLog("[Repair] " + s)), _cts.Token, OnInstallStage);
                _svc.Instances.Update(instance);
                CompleteStages();
                InstallSuccess = $"实例「{instance.Name}」文件已补全，正在启动…";
            }
            else
            {
                Status = $"{instance.Name} 文件完整，直接启动...";
            }

            Status = $"正在启动 {instance.Name} ...";
            var spec = await _svc.BuildLaunchSpecAsync(instance, SelectedAccount);
            Dispatch(() => AddLog("[Launch] " + spec.DisplayCommandLine));

            // Crash-summary context: loader identity + mod census (mod
            // conflicts explain a large share of launch crashes).
            var mods = Mods.ToList();
            var modContext = mods.Count == 0
                ? "未安装 Mod"
                : $"共 {mods.Count} 个 Mod（最近: {string.Join(", ", mods.Take(2).Select(m => m.FileName ?? "?"))}）";
            var contextLine = $"{instance.Name} · {instance.LoaderType}" +
                              (instance.LoaderType != ModLoaderType.Vanilla ? $" {instance.LoaderVersion}" : "") +
                              $" · {modContext}";

            var exit = await _svc.LaunchAsync(spec, line => Dispatch(() => AddLog(line)), CancellationToken.None, contextLine);
            var crash = CrashAnalyzer.Analyze(Logs);
            if (exit == 0)
            {
                Status = $"游戏已正常退出: {SelectedInstance.Name}";
                Dispatch(() => System.Windows.MessageBox.Show(
                    Application.Current.MainWindow,
                    $"《Minecraft》已正常退出。\n\n实例：{SelectedInstance.Name}\n账户：{SelectedAccount.Username}",
                    "Aurora Launcher", MessageBoxButton.OK, MessageBoxImage.Information));
            }
            else
            {
                Status = crash != null ? $"游戏退出(代码 {exit})，可能原因: {crash}" : $"游戏已退出，代码 {exit}";
                // Crash triage: auto-switch the Logs page to errors-only so
                // the user never has to discover the toggle themselves.
                ShowErrorsOnly = true;
                Navigate("Logs");
                await Task.Delay(150); // let the page switch render before the modal dialog
                Dispatch(() => System.Windows.MessageBox.Show(
                    Application.Current.MainWindow,
                    $"游戏异常退出（代码 {exit}）。{(crash != null ? $"\n\n可能原因：{crash}" : "")}\n\n已为你打开「日志」页并只显示错误。\n崩溃摘要已写入日志文件，可直接复制反馈。",
                    "Aurora Launcher", MessageBoxButton.OK, MessageBoxImage.Warning));
            }
        }
        catch (OperationCanceledException) { Status = "已取消（未完成的临时文件已清理）"; }
        catch (Exception ex) { FailStages(ex.Message); Status = "启动失败: " + ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>One-click diagnostics bundle for bug reports.</summary>
    public async Task ExportDiagnosticsAsync()
    {
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出诊断包",
                FileName = $"aurora-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
                Filter = "诊断包 (*.zip)|*.zip"
            };
            if (dlg.ShowDialog() != true) return;
            Status = "正在导出诊断包...";
            var instances = _svc.Instances.LoadAll();
            await Task.Run(() => DiagnosticsExporter.ExportAsync(dlg.FileName,
                LauncherConfig.Instance.RootDirectory, instances));
            Status = $"诊断包已导出: {dlg.FileName}";
            Dispatch(() => System.Windows.MessageBox.Show(
                Application.Current.MainWindow,
                $"诊断包已导出：\n{dlg.FileName}\n\n包含：最近日志、系统信息、Java 列表、实例概览、最近安装记录（已脱敏）。\n报 bug 时直接附上这个文件即可。",
                "Aurora Launcher", MessageBoxButton.OK, MessageBoxImage.Information));
        }
        catch (Exception ex)
        {
            Status = "诊断包导出失败: " + ex.Message;
        }
    }

    public void AddOfflineAccount()
    {
        var name = (OfflineName ?? "").Trim();
        if (string.IsNullOrEmpty(name)) name = "Player";
        var acc = _svc.Auth.CreateOfflineAccount(name);
        Accounts.Add(acc);
        SaveAccounts();
        SelectedAccount = acc;
        Status = "已添加离线账户: " + acc.Username;
    }

    public bool MicrosoftLoginConfigured { get; private set; } = true;

    /// <summary>Refreshes the home-page hint about Microsoft login setup.</summary>
    private void RefreshMicrosoftLoginState()
    {
        MicrosoftLoginConfigured = AuthService.IsMicrosoftLoginConfigured;
        OnPropertyChanged(nameof(MicrosoftLoginHint));
    }

    public string MicrosoftLoginHint => MicrosoftLoginConfigured
        ? ""
        : "尚未配置 Azure 应用 ID，微软登录暂不可用——在下方设置里填入 Client ID 即可启用。";

    public async Task BeginMicrosoftAsync()
    {
        // Pre-flight: 未配置 Azure 应用 ID 时给出可操作指引，而不是裸 400。
        if (!AuthService.IsMicrosoftLoginConfigured)
        {
            MicrosoftMessage = "微软登录未配置。" + AuthService.MicrosoftSetupHint;
            RefreshMicrosoftLoginState();
            return;
        }
        try
        {
            _devicePrompt = await _svc.Auth.BeginMicrosoftLoginAsync();
            MicrosoftMessage = $"请在浏览器打开 {_devicePrompt.VerificationUrl} 并输入代码 {_devicePrompt.UserCode}，然后点击“完成登录”。";
        }
        catch (Exception ex)
        {
            MicrosoftMessage = ex.Message.Contains("400")
                ? "微软登录初始化失败（Azure 返回 400）：请检查 设置 → 微软登录 Client ID 是否正确，且该应用已启用「允许公共客户端流」。"
                : "微软登录初始化失败: " + ex.Message;
        }
        RefreshMicrosoftLoginState();
    }

    public async Task CompleteMicrosoftAsync()
    {
        if (_devicePrompt == null) { MicrosoftMessage = "请先点击“开始微软登录”。"; return; }
        try
        {
            IsBusy = true;
            var account = await _svc.Auth.CompleteMicrosoftLoginAsync(_devicePrompt.DeviceCode, CancellationToken.None);
            Dispatch(() => { Accounts.Add(account); SaveAccounts(); });
            SelectedAccount = account;
            MicrosoftMessage = "登录完成: " + account.Username;
        }
        catch (Exception ex) { MicrosoftMessage = "登录失败: " + ex.Message; }
        finally { IsBusy = false; }
    }

    public void CreateInstance()
    {
        var mc = SelectedVersion?.Id ?? "1.21.1";
        var inst = _svc.Instances.Create($"{mc} (Vanilla)", mc, ModLoaderType.Vanilla, null);
        Instances.Add(inst); SelectedInstance = inst;
        Status = "已创建实例 " + inst.Name;
    }

    /// <summary>Runs the (possibly slow) recursive delete off the UI thread so
    /// the window stays responsive.</summary>
    public async Task DeleteSelectedInstanceAsync()
    {
        var removed = SelectedInstance;
        if (removed == null) { Status = "请先选择要删除的实例"; return; }
        IsBusy = true;
        Status = $"正在删除实例 {removed.Name} ...";
        try
        {
            var ok = await Task.Run(() => _svc.Instances.Delete(removed.Id));
            if (ok)
            {
                Instances.Remove(removed);
                if (SelectedInstance == removed)
                    SelectedInstance = Instances.FirstOrDefault();
                Status = "已删除实例: " + removed.Name + "（未被引用的版本文件已自动清理）";
            }
            else
            {
                Status = "删除失败: 实例元数据中找不到 " + removed.Id;
            }
        }
        catch (Exception ex)
        {
            Status = "删除失败: " + ex.Message + "（若游戏正在运行或有文件被占用，请先关闭后重试）";
        }
        finally { IsBusy = false; }
    }

    // ---------- instance personalization / shortcuts / advanced ----------
    public List<string> CategoryOptions => new() { "自动", "原版", "Fabric", "Forge", "NeoForge", "Quilt", "整合包", "常用" };
    public List<string> IconChoiceOptions => new() { "自动", "自定义图片…" };

    public string SelectedInstanceCategory
    {
        get => SelectedInstance?.Category ?? "自动";
        set
        {
            var inst = SelectedInstance;
            if (inst == null) return;
            var v = string.IsNullOrWhiteSpace(value) || value == "自动" ? null : value.Trim();
            if (inst.Category == v) return;
            inst.Category = v;
            _svc.Instances.Update(inst);
            RefreshInstanceList();
            Status = v == null ? "分类已恢复为自动" : "分类已设为: " + v;
        }
    }

    public string SelectedInstanceIconChoice
    {
        get => !string.IsNullOrEmpty(SelectedInstance?.IconPath) ? "自定义图片…" : "自动";
        set
        {
            var inst = SelectedInstance;
            if (inst == null) return;
            if (value == "自定义图片…") { _ = ChooseInstanceIconAsync(); }
            else
            {
                inst.IconPath = null;
                _svc.Instances.Update(inst);
                OnPropertyChanged(nameof(SelectedInstanceIcon));
                OnPropertyChanged(nameof(HasSelectedInstanceIcon));
                OnPropertyChanged(nameof(SelectedInstanceIconChoice));
                Status = "图标已恢复为自动";
            }
        }
    }

    public string SelectedInstanceFavoriteText => SelectedInstance?.Favorite == true ? "取消收藏" : "加入收藏夹";
    public string? SelectedInstanceNotes => SelectedInstance?.Notes;

    public ImageSource? SelectedInstanceIcon
    {
        get
        {
            try
            {
                var path = SelectedInstance?.IconPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path);
                bmp.DecodePixelWidth = 96;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }
    }

    public bool HasSelectedInstanceIcon => SelectedInstanceIcon != null;

    private void RefreshInstanceList()
    {
        var selId = _selectedInstance?.Id;
        var all = _svc.Instances.LoadAll();
        Dispatch(() =>
        {
            Instances.Clear();
            foreach (var i in all) Instances.Add(i);
            SelectedInstance = Instances.FirstOrDefault(i => i.Id == selId) ?? Instances.FirstOrDefault();
        });
    }

    public void RenameInstance()
    {
        var inst = SelectedInstance;
        if (inst == null) { Status = "请先选择实例"; return; }
        var name = Views.Prompt.Show(Application.Current.MainWindow, "修改版本名", "输入新的实例名称：", inst.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == inst.Name) return;
        inst.Name = name.Trim();
        _svc.Instances.Update(inst);
        RefreshInstanceList();
        Status = "已重命名实例: " + inst.Name;
    }

    public void EditNotes()
    {
        var inst = SelectedInstance;
        if (inst == null) { Status = "请先选择实例"; return; }
        var notes = Views.Prompt.Show(Application.Current.MainWindow, "修改版本描述", "输入实例描述（显示在详情顶部）：", inst.Notes);
        if (notes == null) return;
        inst.Notes = notes.Trim();
        _svc.Instances.Update(inst);
        OnPropertyChanged(nameof(SelectedInstanceNotes));
        Status = "已更新实例描述";
    }

    public void ToggleFavorite()
    {
        var inst = SelectedInstance;
        if (inst == null) { Status = "请先选择实例"; return; }
        inst.Favorite = !inst.Favorite;
        _svc.Instances.Update(inst);
        OnPropertyChanged(nameof(SelectedInstanceFavoriteText));
        RefreshInstanceList();
        Status = inst.Favorite ? "已加入收藏" : "已取消收藏";
    }

    public async Task ChooseInstanceIconAsync()
    {
        var inst = SelectedInstance;
        if (inst == null) { Status = "请先选择实例"; return; }
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择实例图标",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.ico;*.bmp"
        };
        if (dlg.ShowDialog() != true)
        {
            OnPropertyChanged(nameof(SelectedInstanceIconChoice)); // revert combo
            return;
        }
        try
        {
            PathEx.EnsureDirectory(inst.Directory);
            var dest = Path.Combine(inst.Directory, "icon.png");
            File.Copy(dlg.FileName, dest, overwrite: true);
            inst.IconPath = dest;
            _svc.Instances.Update(inst);
            OnPropertyChanged(nameof(SelectedInstanceIcon));
            OnPropertyChanged(nameof(HasSelectedInstanceIcon));
            OnPropertyChanged(nameof(SelectedInstanceIconChoice));
            Status = "实例图标已更新";
        }
        catch (Exception ex)
        {
            OnPropertyChanged(nameof(SelectedInstanceIconChoice)); // revert combo
            Status = "设置图标失败: " + ex.Message;
        }
    }

    private void OpenFolder(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) { Status = "请先选择实例"; return; }
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start("explorer.exe", path);
            Status = "已打开: " + path;
        }
        catch (Exception ex) { Status = "打开目录失败: " + ex.Message; }
    }

    public void OpenInstanceFolder() => OpenFolder(SelectedInstance?.Directory);
    public void OpenSavesFolder() => OpenFolder(SelectedInstance == null ? null : Path.Combine(SelectedInstance.Directory, "saves"));
    public void OpenModsFolder() => OpenFolder(SelectedInstance == null ? null : Path.Combine(SelectedInstance.Directory, "mods"));

    /// <summary>Exports a ready-to-run .bat launch script for the selected
    /// instance (requires a complete install + a selected account).</summary>
    public async Task ExportLaunchScriptAsync()
    {
        var inst = SelectedInstance;
        if (inst == null) { Status = "请先选择实例"; return; }
        if (SelectedAccount == null) { Status = "导出启动脚本需要先选择账户"; return; }
        try
        {
            IsBusy = true;
            _cts = new CancellationTokenSource();
            if (_svc.NeedsRepair(inst))
            {
                Status = $"正在补全 {inst.Name} 的文件后再导出脚本...";
                await _svc.RepairAsync(inst, OnDownloadProgress,
                    s => Dispatch(() => AddLog("[Repair] " + s)), _cts.Token);
                _svc.Instances.Update(inst);
            }

            var spec = await _svc.BuildLaunchSpecAsync(inst, SelectedAccount);
            static string Quote(string a) =>
                a.Contains(' ') || a.Contains('"') || a.Contains('\t')
                    ? "\"" + a.Replace("\"", "\\\"") + "\"" : a;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 >nul");
            sb.AppendLine($"rem Aurora Launcher 导出启动脚本 | 实例: {inst.Name} | {inst.MinecraftVersion} / {inst.LoaderType}");
            sb.AppendLine($"cd /d \"{spec.WorkingDirectory}\"");
            sb.AppendLine($"\"{spec.JavaPath}\" {string.Join(" ", spec.Arguments.Select(Quote))}");
            sb.AppendLine("pause");
            var safe = string.Concat(inst.Name.Split(Path.GetInvalidFileNameChars()));
            var file = Path.Combine(inst.Directory, "启动-" + safe + ".bat");
            File.WriteAllText(file, sb.ToString(), new System.Text.UTF8Encoding(false));
            Status = "启动脚本已导出: " + file + "（脚本包含登录令牌，请妥善保管）";
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file}\""); } catch { }
        }
        catch (OperationCanceledException) { Status = "已取消（未完成的临时文件已清理）"; }
        catch (Exception ex) { Status = "导出启动脚本失败: " + ex.Message; }
        finally { IsBusy = false; Download.Active = false; }
    }

    /// <summary>Manual "补全文件" for the selected instance.</summary>
    public async Task RepairInstanceAsync()
    {
        var inst = SelectedInstance;
        if (inst == null) { Status = "请先选择实例"; return; }
        if (!_svc.NeedsRepair(inst)) { Status = $"实例 {inst.Name} 文件完整，无需补全"; return; }
        try
        {
            IsBusy = true;
            _cts = new CancellationTokenSource();
            InitStages(BuildStages(inst.LoaderType), $"{inst.Name} 文件补全");
            Navigate("Downloads");
            await _svc.RepairAsync(inst, OnDownloadProgress,
                s => Dispatch(() => AddLog("[Repair] " + s)), _cts.Token, OnInstallStage);
            _svc.Instances.Update(inst);
            CompleteStages();
            Status = $"实例 {inst.Name} 文件补全完成";
        }
        catch (OperationCanceledException) { Status = "已取消（未完成的临时文件已清理）"; }
        catch (Exception ex) { Status = "补全失败: " + ex.Message; }
        finally { IsBusy = false; Download.Active = false; }
    }

    /// <summary>Folder dialog first (UI thread), then the copy-based import
    /// runs on a background thread — importing a big .minecraft no longer
    /// freezes the window.</summary>
    public async Task ImportMinecraftAsync()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择 .minecraft 文件夹" };
        if (dlg.ShowDialog() != true) return;
        var source = dlg.FolderName;
        IsBusy = true;
        Status = "正在导入 .minecraft（复制式导入，不影响源数据）...";
        try
        {
            var inst = await Task.Run(() => _svc.Instances.ImportMinecraftFolder(source));
            Instances.Add(inst); SelectedInstance = inst;
            Status = "已导入实例 " + inst.Name;
        }
        catch (Exception ex) { Status = "导入失败: " + ex.Message; }
        finally { IsBusy = false; }
    }

    public async Task ImportLauncherAsync()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择其他启动器根目录 (含 instances/.minecraft)" };
        if (dlg.ShowDialog() != true) return;
        var source = dlg.FolderName;
        IsBusy = true;
        Status = "正在扫描并导入实例（复制式导入，不影响源数据）...";
        try
        {
            var list = await Task.Run(() => _svc.Instances.ImportFromLauncher(source, "外部"));
            foreach (var i in list) Instances.Add(i);
            if (list.Count > 0) SelectedInstance = list[0];
            Status = list.Count > 0 ? $"已导入 {list.Count} 个实例" : "未在所选目录发现可导入的实例";
        }
        catch (Exception ex) { Status = "导入失败: " + ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>Fire-and-forget scan: jar parsing happens off the UI thread so
    /// clicking around never stutters even with many mods installed.</summary>
    public void ScanMods() => _ = ScanModsCoreAsync();

    private async Task ScanModsCoreAsync()
    {
        try
        {
            var inst = SelectedInstance;
            var list = await Task.Run(() => inst == null
                ? new List<ModInfo>()
                : _svc.ModManager.Scan(inst.Directory)).ConfigureAwait(true);
            Dispatch(() =>
            {
                Mods.Clear();
                foreach (var m in list) Mods.Add(m);
            });
        }
        catch (Exception ex) { Status = "扫描 Mod 失败: " + ex.Message; }
    }

    private void LoadWorlds()
    {
        Worlds.Clear();
        if (SelectedInstance == null) { SelectedWorld = null; return; }
        foreach (var w in _svc.ModManager.ListWorlds(SelectedInstance.Directory)) Worlds.Add(w);
        SelectedWorld = Worlds.FirstOrDefault();
    }

    public void ToggleMod()
    {
        if (SelectedMod == null || SelectedInstance == null) return;
        _svc.ModManager.SetEnabled(SelectedMod, !SelectedMod.Enabled);
        ScanMods();
    }

    private bool _isSearching;
    /// <summary>True while a Modrinth search request is in flight (drives the UI feedback).</summary>
    public bool IsSearching { get => _isSearching; set => SetProperty(ref _isSearching, value); }

    private bool _hasSearched;
    /// <summary>True once at least one search has completed (switches empty-state text).</summary>
    public bool HasSearched { get => _hasSearched; set => SetProperty(ref _hasSearched, value); }

    public List<string> SortOptions => new() { "相关性", "下载量", "最新发布", "最近更新" };
    private string _sortOption = "相关性";
    /// <summary>Modrinth search ordering chosen by the user.</summary>
    public string SortOption
    {
        get => _sortOption;
        set { if (SetProperty(ref _sortOption, value)) _ = SearchContentAsync(); }
    }

    private const int MaxLogLines = 600;      // ring capacity
    private const int LogTrimBatch = 100;     // trim in batches (amortized O(1))

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingLogLines = new();
    private DispatcherTimer? _logFlushTimer;

    /// <summary>Queues a log line for batched UI delivery.</summary>
    private void EnqueueLog(string line) => _pendingLogLines.Enqueue(line);

    /// <summary>Flushes queued log lines as ONE ui update (smoothness).</summary>
    private void FlushPendingLogs()
    {
        if (_pendingLogLines.IsEmpty) return;
        var batch = new List<string>(64);
        while (_pendingLogLines.TryDequeue(out var l))
        {
            batch.Add(l);
            if (batch.Count >= 200) break;
        }
        Dispatch(() =>
        {
            foreach (var l in batch) AddLog(l);
        });
    }

    /// <summary>Bounded ring-style log feed: trims the OLDEST lines in batches
    /// (never per-insert) so the Logs page stays responsive no matter how
    /// chatty a session gets.</summary>
    private void AddLog(string line)
    {
        Logs.Add(line);
        if (Logs.Count > MaxLogLines + LogTrimBatch)
        {
            for (int i = 0; i < LogTrimBatch; i++) Logs.RemoveAt(0);
        }
    }

    private bool _showErrorsOnly;
    /// <summary>Logs page filter: show only [Error]/[Warn] lines (crash triage).</summary>
    public bool ShowErrorsOnly
    {
        get => _showErrorsOnly;
        set
        {
            if (SetProperty(ref _showErrorsOnly, value))
                LogsView.Filter = value ? ErrorsOnlyFilter : null;
            else
                LogsView.Filter = null;
            LogsView.Refresh();
        }
    }

    private static readonly Predicate<object> ErrorsOnlyFilter = o =>
    {
        var s = o as string ?? "";
        return s.Contains("[Error]", StringComparison.OrdinalIgnoreCase)
            || s.Contains("[Warn]", StringComparison.OrdinalIgnoreCase)
            || s.Contains("错误", StringComparison.OrdinalIgnoreCase)
            || s.Contains("失败", StringComparison.OrdinalIgnoreCase);
    };

    private ICollectionView? _logsView;
    public ICollectionView LogsView => _logsView ??= CollectionViewSource.GetDefaultView(Logs);

    private static string SortIndex(string s) => s switch
    {
        "下载量" => "downloads",
        "最新发布" => "newest",
        "最近更新" => "updated",
        _ => "relevance"
    };

    // ---- content detail page (PCL-style: versions grouped by MC version) ----
    // Flattened rows (group headers + version rows) for smooth virtualized scrolling.
    public ObservableCollection<object> DetailRows { get; } = new();

    private int _detailScrollTargetIndex = -1;
    /// <summary>Row index of the first group containing an installable
    /// (instance-compatible) version; -1 = none. Used by the view to
    /// auto-scroll to the best-matching group when the page opens.</summary>
    public int DetailScrollTargetIndex
    {
        get => _detailScrollTargetIndex;
        private set
        {
            if (SetProperty(ref _detailScrollTargetIndex, value))
                OnPropertyChanged(nameof(HasCompatibleAnchor));
        }
    }

    /// <summary>True when the detail page contains at least one version that
    /// fits the current instance (enables the "定位适配版本" button).</summary>
    public bool HasCompatibleAnchor => DetailScrollTargetIndex >= 0;

    private string _detailSummaryText = "";
    /// <summary>One-line overview shown in the detail header, e.g.
    /// "128 个版本 · 43 个 MC 版本 · 5 个适配当前实例".</summary>
    public string DetailSummaryText
    {
        get => _detailSummaryText;
        private set => SetProperty(ref _detailSummaryText, value);
    }

    private ContentItemVM? _selectedContentItem;
    public ContentItemVM? SelectedContentItem { get => _selectedContentItem; set => SetProperty(ref _selectedContentItem, value); }

    private bool _showContentDetail;
    public bool ShowContentDetail
    {
        get => _showContentDetail;
        set { if (SetProperty(ref _showContentDetail, value)) OnPropertyChanged(nameof(HasSearchOverlay)); }
    }

    /// <summary>True while the detail page is open or a search is running
    /// (hides the empty-state hints).</summary>
    public bool HasSearchOverlay => ShowContentDetail || IsSearching;

    private string? _detailBody;
    public string? DetailBody { get => _detailBody; set => SetProperty(ref _detailBody, value); }

    private ImageSource? _detailIcon;
    public ImageSource? DetailIcon { get => _detailIcon; set => SetProperty(ref _detailIcon, value); }

    public string DetailPageUrl
    {
        get
        {
            var p = SelectedContentItem?.Project;
            if (p == null) return "";
            var typePath = p.ProjectType switch
            {
                "mod" => "mod",
                "modpack" => "modpack",
                "shader" => "shader",
                "resourcepack" => "resourcepack",
                "datapack" => "datapack",
                _ => "mod"
            };
            return $"https://modrinth.com/{typePath}/{(string.IsNullOrEmpty(p.Slug) ? p.ProjectId : p.Slug)}";
        }
    }

    public void OpenModrinthPage()
    {
        try
        {
            if (string.IsNullOrEmpty(DetailPageUrl)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = DetailPageUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex) { Status = "打开链接失败: " + ex.Message; }
    }

    private static bool IsCompatibleVersion(ContentType type, string? mc, string? loader, ModrinthVersion v)
    {
        bool mcOk = mc == null || v.GameVersions.Contains(mc);
        bool loaderOk = type != ContentType.Mod || loader == null || v.Loaders.Contains(loader);
        return mcOk && loaderOk;
    }

    /// <summary>Opens the PCL-style detail page: full description + ALL versions
    /// grouped by the MC versions they support (newest first), each installable.</summary>
    public async Task OpenContentDetailAsync(ContentItemVM? item)
    {
        if (item == null) return;
        try
        {
            IsSearching = true;
            var type = ParseContentType(ContentKind);
            var inst = SelectedInstance;
            var mc = _contentMcVersion == "自动（跟随实例）" ? inst?.MinecraftVersion : _contentMcVersion;
            var loader = type == ContentType.Mod && inst is { LoaderType: not ModLoaderType.Vanilla }
                ? inst.LoaderType.ToString().ToLowerInvariant() : null;

            var detailTask = _svc.ModManager.GetProjectDetailAsync(item.Project.ProjectId);
            var versionsTask = _svc.ModManager.GetVersionsAsync(item.Project.ProjectId, type, null, null);
            await Task.WhenAll(detailTask, versionsTask).ConfigureAwait(true);
            var detail = await detailTask;
            var all = await versionsTask;

            var groups = all
                .SelectMany(v => (v.GameVersions.Count == 0 ? new[] { "其他" } : v.GameVersions.ToArray()).Select(gv => (gv, v)))
                .GroupBy(x => x.gv)
                .Select(g => new McVersionGroupVM(
                    g.Key,
                    new ObservableCollection<VersionHistoryVM>(
                        // Modpacks always install into a NEW dedicated instance,
                        // so instance-compatibility badges do not apply.
                        g.Select(x => new VersionHistoryVM(x.v, IsCompatibleVersion(type, mc, loader, x.v), neutral: type == ContentType.Modpack))
                         .OrderByDescending(v => v.Version.DatePublished ?? DateTime.MinValue))))
                .OrderByDescending(g => g.Versions.Max(v => v.Version.DatePublished ?? DateTime.MinValue))
                .ToList();

            var icon = await IconCache.GetAsync(detail.IconUrl).ConfigureAwait(true);

            // Flatten groups into uniform rows (header + versions) so the
            // virtualized ListBox scrolls smoothly by pixel while dragging.
            // The scroll anchor is the FIRST INSTALLABLE VERSION row itself —
            // not its group header: within one MC group, earlier rows may
            // still be incompatible (wrong loader), and the user asked to
            // land directly on what they can download.
            int scrollTarget = -1;
            var rows = new List<object>();
            foreach (var g in groups)
            {
                int headerIndex = rows.Count;
                rows.Add(new ContentGroupHeaderVM(g.McVersion, g.CountText));
                foreach (var v in g.Versions)
                {
                    if (scrollTarget < 0 && v.Compatible)
                        scrollTarget = rows.Count == headerIndex + 1 ? headerIndex : rows.Count;
                    rows.Add(v);
                }
            }

            Dispatch(() =>
            {
                SelectedContentItem = item;
                // Cap very long bodies so the TextBlock never chokes the UI.
                var body = string.IsNullOrWhiteSpace(detail.Body) ? item.Project.Description : detail.Body;
                DetailBody = body.Length > 8000 ? body[..8000] + "\n\n……（内容过长已截断，完整内容请在 Modrinth 页面查看）" : body;
                DetailIcon = icon;
                DetailRows.Clear();
                foreach (var r in rows) DetailRows.Add(r);
                DetailScrollTargetIndex = scrollTarget;
                DetailSummaryText = type == ContentType.Modpack
                    ? $"{all.Count} 个版本 · {groups.Count} 个 MC 版本 · 安装将新建实例"
                    : $"{all.Count} 个版本 · {groups.Count} 个 MC 版本 · {rows.Count(r => r is VersionHistoryVM h && h.Compatible)} 个适配当前实例";
                ShowContentDetail = true;
            });
            Status = $"已打开 {item.Project.Title} 详情（{all.Count} 个版本，按 MC 版本分组）";
        }
        catch (Exception ex) { Status = "打开详情失败: " + FriendlyNetworkError(ex); }
        finally { IsSearching = false; }
    }

    /// <summary>Installs an explicitly chosen version; incompatible picks ask
    /// for confirmation first.</summary>
    public async Task InstallSpecificVersionAsync(VersionHistoryVM? vh)
    {
        if (vh?.Version == null || SelectedContentItem == null) return;
        var type = ParseContentType(ContentKind);
        if (!vh.Compatible && type != ContentType.Modpack)
        {
            var answer = System.Windows.MessageBox.Show(
                Application.Current.MainWindow,
                $"版本 {vh.Version.VersionNumber} 与当前实例不匹配（{InstanceSummary}）。\n\n仍要强制安装吗？（可能导致游戏无法加载该内容）",
                "版本不适配", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }
        try
        {
            IsBusy = true;
            _cts = new CancellationTokenSource();
            var project = SelectedContentItem.Project;
            if (type == ContentType.Modpack) { await InstallModpackAsync(project, vh.Version); return; }

            var inst = SelectedInstance ?? throw new InvalidOperationException("请先选择实例");
            Status = $"正在下载 {project.Title} {vh.Version.VersionNumber} ...";
            InitStages(new List<(string, string)> { ("content", $"下载 {ContentKind}：{project.Title}") },
                $"{project.Title}（{ContentKind}）下载");
            OnInstallStage("content");
            Navigate("Downloads");
            await _svc.ModManager.DownloadContentAsync(project, vh.Version, type, inst.Directory,
                type == ContentType.Datapack ? SelectedWorld : null, OnDownloadProgress, _cts.Token);
            CompleteStages();
            if (type == ContentType.Mod)
                await EnsureFabricApiAsync(inst, _cts.Token, $"安装模组 {project.Title}");
            ScanMods();
            ShowContentDetail = false;
            Status = $"已安装 {project.Title} {vh.Version.VersionNumber} 到 {inst.Name}";
        }
        catch (OperationCanceledException) { Status = "已取消下载（未完成的临时文件已清理）"; }
        catch (Exception ex) { FailStages(ex.Message); Status = "安装失败: " + ex.Message; }
        finally { IsBusy = false; Download.Active = false; }
    }

    // ---- content search state (PCL-style paging / categories / detail) ----
    private int _searchPage;
    private long _searchTotalHits;
    private const int SearchPageSize = 20;

    public string SearchPageText => $"{_searchPage + 1} / {TotalPages}";
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(_searchTotalHits / (double)SearchPageSize));
    public bool CanGoPrevPage => _searchPage > 0;
    public bool CanGoNextPage => _searchPage + 1 < TotalPages;

    public ObservableCollection<string> ContentCategories { get; } = new() { "全部" };
    private string _selectedCategory = "全部";
    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (!SetProperty(ref _selectedCategory, value)) return;
            _searchPage = 0;
            _ = SearchContentAsync();
        }
    }

    public ObservableCollection<string> ContentVersionOptions { get; } = new() { "自动（跟随实例）" };
    private string _contentMcVersion = "自动（跟随实例）";
    public string ContentMcVersion
    {
        get => _contentMcVersion;
        set
        {
            if (!SetProperty(ref _contentMcVersion, value)) return;
            _searchPage = 0;
            _ = SearchContentAsync();
        }
    }

    public string SearchPageInfo => $"共 {_searchTotalHits:N0} 个结果";

    private void NotifyPaging()
    {
        OnPropertyChanged(nameof(SearchPageText));
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(CanGoPrevPage));
        OnPropertyChanged(nameof(CanGoNextPage));
        OnPropertyChanged(nameof(SearchPageInfo));
    }

    private async Task GoToPageAsync(int page)
    {
        _searchPage = Math.Clamp(page, 0, TotalPages - 1);
        NotifyPaging();
        ShowContentDetail = false;
        await SearchContentAsync();
    }

    public async Task SearchFirstPageAsync() => await GoToPageAsync(0);
    public async Task SearchPrevPageAsync() => await GoToPageAsync(_searchPage - 1);
    public async Task SearchNextPageAsync() => await GoToPageAsync(_searchPage + 1);
    public async Task SearchLastPageAsync() => await GoToPageAsync(TotalPages - 1);

    /// <summary>Loads the MC version list (from the official manifest) that
    /// powers the content-search version selector.</summary>
    public async Task LoadContentVersionOptionsAsync()
    {
        try
        {
            var manifest = await _svc.GetManifestAsync();
            var ids = manifest.Versions.Take(150).Select(v => v.Id).ToList();
            Dispatch(() =>
            {
                ContentVersionOptions.Clear();
                ContentVersionOptions.Add("自动（跟随实例）");
                foreach (var id in ids) ContentVersionOptions.Add(id);
            });
        }
        catch (Exception ex) { _log.Warn("UI", "MC 版本列表获取失败: " + ex.Message); }
    }

    /// <summary>Loads Modrinth categories for the current content kind.</summary>
    private async Task LoadContentCategoriesAsync(ContentType type)
    {
        try
        {
            var cats = await _svc.ModManager.GetCategoriesAsync(type);
            Dispatch(() =>
            {
                ContentCategories.Clear();
                ContentCategories.Add("全部");
                foreach (var c in cats) ContentCategories.Add(c);
                _selectedCategory = "全部";
                OnPropertyChanged(nameof(SelectedCategory));
            });
        }
        catch (Exception ex) { _log.Warn("UI", "分类获取失败: " + ex.Message); }
    }

    public async Task SearchContentAsync()
    {
        try
        {
            var type = ParseContentType(ContentKind);
            string? mc = null, loader = null;
            if (type != ContentType.Modpack)
            {
                var inst = SelectedInstance ?? throw new InvalidOperationException("请先在首页或实例管理中选择一个实例");
                mc = _contentMcVersion == "自动（跟随实例）" ? inst.MinecraftVersion : _contentMcVersion;
                loader = type == ContentType.Mod && inst.LoaderType != ModLoaderType.Vanilla
                    ? inst.LoaderType.ToString().ToLowerInvariant() : null;
            }
            IsSearching = true;
            Status = $"正在搜索{ContentKind}…";
            var result = await _svc.ModManager.SearchAsync(ModSearchQuery, type, mc, loader,
                SortIndex(SortOption), SearchPageSize, _searchPage,
                SelectedCategory == "全部" ? null : SelectedCategory);
            var items = result.Projects.Select(r => new ContentItemVM(r)).ToList();
            _searchTotalHits = result.TotalHits;
            _searchPage = Math.Min(_searchPage, TotalPages - 1);
            Dispatch(() =>
            {
                ModSearchResults.Clear();
                foreach (var i in items) ModSearchResults.Add(i);
            });
            HasSearched = true;
            NotifyPaging();
            Status = $"「{ContentKind}」搜索到 {result.TotalHits:N0} 个结果（第 {_searchPage + 1} 页，已按实例自动适配）";
        }
        catch (Exception ex)
        {
            HasSearched = true;
            Status = "Modrinth 搜索失败: " + FriendlyNetworkError(ex);
        }
        finally { IsSearching = false; }
    }

    /// <summary>Translates raw network failures into actionable Chinese hints.</summary>
    private static string FriendlyNetworkError(Exception ex) => ex switch
    {
        TimeoutException or TaskCanceledException or OperationCanceledException =>
            "所有线路均超时（官方默认/直连/国内镜像均已尝试）。请检查网络，或在「设置 → 代理地址」填写可用代理后重试。",
        HttpRequestException =>
            "网络错误。已尝试官方与国内镜像线路，可在「设置 → 代理地址」配置代理后重试。原始错误: " + ex.Message,
        _ => ex.Message
    };

    public string ModSearchQuery { get; set; } = "";

    private async Task ResetContentAsync()
    {
        ModSearchQuery = "";
        OnPropertyChanged(nameof(ModSearchQuery));
        await SearchContentAsync();
    }

    public async Task InstallContentAsync(ModrinthProject? project)
    {
        if (project == null) return;
        var type = ParseContentType(ContentKind);
        if (type == ContentType.Modpack) { await InstallModpackAsync(project); return; }

        try
        {
            var inst = SelectedInstance ?? throw new InvalidOperationException("请先选择实例（用于自动适配版本）");
            IsBusy = true;
            _cts = new CancellationTokenSource();
            Status = $"正在为 {inst.Name} 查找适配的{ContentKind}...";
            var loader = type == ContentType.Mod && inst.LoaderType != ModLoaderType.Vanilla
                ? inst.LoaderType.ToString().ToLowerInvariant() : null;
            var versions = await _svc.ModManager.GetVersionsAsync(project.ProjectId, type, inst.MinecraftVersion, loader, _cts.Token);
            if (versions.Count == 0)
            {
                Status = $"未找到适配 {inst.Name}（MC {inst.MinecraftVersion}{(loader != null ? " / " + inst.LoaderType : "")}）的{ContentKind}版本";
                return;
            }
            Status = $"正在下载 {project.Title} {versions[0].VersionNumber} ...";
            // All content downloads surface in the download manager.
            InitStages(new List<(string, string)> { ("content", $"下载 {ContentKind}：{project.Title}") },
                $"{project.Title}（{ContentKind}）下载");
            OnInstallStage("content");
            Navigate("Downloads");
            await _svc.ModManager.DownloadContentAsync(project, versions[0], type, inst.Directory,
                type == ContentType.Datapack ? SelectedWorld : null, OnDownloadProgress, _cts.Token);
            CompleteStages();
            Status = $"已安装 {project.Title} {versions[0].VersionNumber} 到 {inst.Name}";
            if (type == ContentType.Mod)
                await EnsureFabricApiAsync(inst, _cts.Token, $"安装模组 {project.Title}");
            ScanMods();
        }
        catch (OperationCanceledException) { Status = "已取消下载（未完成的临时文件已清理）"; }
        catch (Exception ex) { FailStages(ex.Message); Status = "安装失败: " + ex.Message; }
        finally { IsBusy = false; Download.Active = false; }
    }

    /// <summary>Fabric API's well-known Modrinth project id.</summary>
    private const string FabricApiProjectId = "P7dR8mSH";

    private bool InstanceHasFabricApi(GameInstance inst) =>
        _svc.ModManager.Scan(inst.Directory).Any(m =>
            (m.ModId ?? "").Contains("fabric-api", StringComparison.OrdinalIgnoreCase) ||
            (m.FileName ?? "").Contains("fabric-api", StringComparison.OrdinalIgnoreCase) ||
            (m.Name ?? "").Contains("fabric api", StringComparison.OrdinalIgnoreCase));

    /// <summary>Ensures a Fabric instance has Fabric API (most Fabric mods
    /// depend on it). Runs after instance install AND after each mod install.
    /// Setting「自动安装 Fabric API」开启（默认）→ 告知后直接自动安装适配版本：
    /// 状态栏 + 非阻塞 toast（8 秒，注明触发原因与关闭入口）+ 文件日志记录触发
    /// 来源——用户装完实例后 mods 目录里多了什么、为什么，必须可追溯。
    /// 关闭 → 弹窗询问后再装。
    /// Returns true when Fabric API was installed by this call.</summary>
    private async Task<bool> EnsureFabricApiAsync(GameInstance inst, CancellationToken ct, string trigger)
    {
        try
        {
            if (inst.LoaderType != ModLoaderType.Fabric) return false;
            AddLog($"[Fabric API] 前置检查触发：{trigger}（实例 {inst.Name}，MC {inst.MinecraftVersion}）");
            // Zip-parsing the mods folder can take a while — keep it off the UI thread.
            var hasApi = await Task.Run(() => InstanceHasFabricApi(inst)).ConfigureAwait(true);
            if (hasApi) { AddLog("[Fabric API] 已存在，跳过安装"); return false; }
            if (LauncherConfig.Instance.AutoInstallFabricApi)
            {
                // 告知用户后自动安装：状态栏 + toast（非阻塞），不打断流程。
                Status = $"检测到未安装 Fabric API，正在自动安装适配 MC {inst.MinecraftVersion} 的 Fabric API 前置...";
            }
            else
            {
                var answer = System.Windows.MessageBox.Show(
                    Application.Current.MainWindow,
                    $"检测到当前 Fabric 实例未安装 Fabric API。\n\n大多数 Fabric Mod 需要 Fabric API 作为前置，否则会无法运行。\n\n是否自动安装适配 MC {inst.MinecraftVersion} 的 Fabric API？\n\n（可在 设置 → 游戏 中开启自动安装）",
                    "缺少前置：Fabric API", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return false;
                Status = "正在安装前置 Fabric API ...";
            }

            var versions = await _svc.ModManager.GetVersionsAsync(FabricApiProjectId, ContentType.Mod,
                inst.MinecraftVersion, "fabric", ct);
            if (versions.Count == 0) { Status = "未找到适配当前实例的 Fabric API 版本"; return false; }
            var project = new ModrinthProject { ProjectId = FabricApiProjectId, Title = "Fabric API" };
            InitStages(new List<(string, string)> { ("content", "下载前置：Fabric API") },
                $"Fabric API（前置模组）下载");
            OnInstallStage("content");
            await _svc.ModManager.DownloadContentAsync(project, versions[0], ContentType.Mod, inst.Directory,
                null, OnDownloadProgress, ct);
            CompleteStages();
            ScanMods();
            AddLog($"[Fabric API] 已自动安装 {versions[0].VersionNumber}（触发：{trigger}）");
            Status = "已自动安装前置 Fabric API " + versions[0].VersionNumber;
            ShowToast($"已自动安装 Fabric API 前置 {versions[0].VersionNumber}（触发：{trigger}）。可在 设置 → 游戏 中关闭自动安装。");
            return true;
        }
        catch (OperationCanceledException) { Status = "已取消 Fabric API 前置安装"; return false; }
        catch (Exception ex) { Status = "Fabric API 安装失败: " + ex.Message; return false; }
    }

    /// <summary>Installs a Modrinth modpack: auto-creates a matching instance
    /// (MC version + loader parsed from the pack), installs the game + loader,
    /// then extracts overrides and downloads the pack's client files.</summary>
    private async Task InstallModpackAsync(ModrinthProject project, ModrinthVersion? specific = null)
    {
        try
        {
            IsBusy = true;
            _cts = new CancellationTokenSource();
            ModrinthVersion packVer;
            if (specific != null)
            {
                packVer = specific;
            }
            else
            {
                Status = $"正在获取整合包 {project.Title} 的版本信息...";
                var versions = await _svc.ModManager.GetVersionsAsync(project.ProjectId, ContentType.Modpack, null, null, _cts.Token);
                if (versions.Count == 0) { Status = "该整合包没有可用版本"; return; }
                packVer = versions[0];
            }

            // Modpacks ALWAYS create a dedicated new instance — make that
            // explicit before any download starts.
            var loaderNames = new[] { "forge", "fabric", "quilt", "neoforge" };
            var estMc = packVer.GameVersions.FirstOrDefault(g => !loaderNames.Contains(g)) ?? "?";
            var estLoader = packVer.Loaders.FirstOrDefault() ?? "原版";
            var proceed = System.Windows.MessageBox.Show(
                Application.Current.MainWindow,
                $"将为整合包「{project.Title}」新建一个独立实例：\n\n    MC 版本：{estMc}\n    加载器：{estLoader}\n    整合包版本：{packVer.VersionNumber}\n\n不会修改任何现有实例。是否继续？",
                "安装整合包", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (proceed != MessageBoxResult.Yes) return;

            // Phase 1: show a stage list IMMEDIATELY while the .mrpack downloads.
            InitStages(new List<(string, string)> { ("modpack", "下载整合包文件") },
                $"{project.Title}（{packVer.FileName}）");
            OnInstallStage("modpack");
            Navigate("Downloads");

            var tempDir = Path.Combine(Path.GetTempPath(), "AuroraLauncher", "mrpack");
            Directory.CreateDirectory(tempDir);
            var tempFile = Path.Combine(tempDir, project.ProjectId + ".mrpack");
            Status = $"正在下载整合包 {packVer.FileName} ...";
            await _svc.ModManager.DownloadMrpackAsync(project, packVer, tempFile, OnDownloadProgress, _cts.Token);

            var info = ModManager.ReadMrpackInfo(tempFile);
            var inst = _svc.Instances.Create($"{project.Title}", info.GameVersion, info.Loader, info.LoaderVersion);
            Dispatch(() => { Instances.Add(inst); SelectedInstance = inst; });

            // Phase 2: full pipeline stages (game + loader + deploy pack files).
            var stages = BuildStages(info.Loader);
            stages.Add(("modpack", "部署整合包文件"));
            InitStages(stages, $"{project.Title}（MC {info.GameVersion} / {info.Loader}）安装");

            Status = $"正在安装整合包 {project.Title}（MC {info.GameVersion} / {info.Loader}）...";
            await _svc.InstallWithLoaderAsync(inst, OnDownloadProgress, s => Dispatch(() => AddLog("[Install] " + s)), _cts.Token, OnInstallStage);
            OnInstallStage("modpack");
            await _svc.ModManager.InstallMrpackAsync(tempFile, inst.Directory, OnDownloadProgress, _cts.Token);
            CompleteStages();
            ScanMods();
            Status = $"整合包 {project.Title} 安装完成，已创建实例「{inst.Name}」（{info.ClientFiles} 个文件已就位，启动时自动补齐依赖）";
        }
        catch (OperationCanceledException) { Status = "已取消下载（未完成的临时文件已清理）"; }
        catch (Exception ex) { FailStages(ex.Message); Status = "整合包安装失败: " + ex.Message; }
        finally { IsBusy = false; Download.Active = false; }
    }

    public void ImportMrpack()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "mrpack|*.mrpack" };
        if (dlg.ShowDialog() == true && SelectedInstance != null)
        {
            _svc.ModManager.ImportMrpack(dlg.FileName, SelectedInstance.Directory);
            ScanMods();
            Status = "已导入整合包";
        }
    }

    /// <summary>Opens the storage directory in Explorer (creating it if needed).</summary>
    public void OpenRootDirectory()
    {
        try
        {
            var dir = (RootDirectoryText ?? "").Trim();
            if (string.IsNullOrWhiteSpace(dir)) { Status = "存储目录为空"; return; }
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start("explorer.exe", dir);
            Status = "已打开存储目录: " + dir;
        }
        catch (Exception ex) { Status = "打开目录失败: " + ex.Message; }
    }

    public void ApplySettings()
    {
        var c = LauncherConfig.Instance;
        c.MaxMemoryMb = MemoryMb;
        c.MinMemoryMb = Math.Max(512, MemoryMb / 2);
        c.JavaPath = string.IsNullOrWhiteSpace(JavaPath) ? null : JavaPath;
        c.DownloadSource = DownloadSourceText == "BMCLAPI" ? DownloadSource.BMCLAPI : DownloadSource.Official;
        c.ProxyUrl = string.IsNullOrWhiteSpace(ProxyUrl) ? null : ProxyUrl;
        c.MicrosoftClientId = string.IsNullOrWhiteSpace(MicrosoftClientIdText) ? null : MicrosoftClientIdText.Trim();
        c.AutoInstallFabricApi = AutoInstallFabricApi;
        c.AutoCheckUpdates = AutoCheckUpdates;
        RefreshMicrosoftLoginState();
        c.WindowWidth = WindowW; c.WindowHeight = WindowH;
        c.ExtraJvmArgs = ExtraJvm;
        c.Theme = DarkTheme ? ThemePreference.Dark : ThemePreference.Light;

        // ---- storage directory change (requires restart to take effect) ----
        var newRoot = (RootDirectoryText ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(newRoot))
        {
            try { newRoot = Path.GetFullPath(newRoot); }
            catch { Status = "存储目录不是有效路径"; return; }
        }
        if (!string.IsNullOrWhiteSpace(newRoot) &&
            !string.Equals(newRoot, Path.GetFullPath(c.RootDirectory), StringComparison.OrdinalIgnoreCase))
        {
            var oldRoot = c.RootDirectory;
            c.RootDirectory = newRoot;
            // Persist the new value into BOTH locations: the old config file is
            // the one read on next startup, so it must carry the new root too.
            c.Save();
            try { File.WriteAllText(Path.Combine(oldRoot, "config.json"), JsonHelper.SerializeIndented(c)); }
            catch { /* old dir may be gone; new location config is already written */ }

            var answer = System.Windows.MessageBox.Show(
                Application.Current.MainWindow,
                $"存储目录已更改为：\n{newRoot}\n\n需要重启启动器才能生效（已有文件不会自动迁移，新目录将重新下载）。\n\n立即重启吗？",
                "存储目录已更改", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes)
            {
                System.Diagnostics.Process.Start(Environment.ProcessPath!);
                Application.Current.Shutdown();
                return;
            }
            Status = "存储目录已更新，重启启动器后生效";
            return;
        }

        c.Save();
        Status = "设置已保存";
    }

    public void SwitchTheme()
    {
        DarkTheme = !DarkTheme;
        var c = LauncherConfig.Instance;
        c.Theme = DarkTheme ? ThemePreference.Dark : ThemePreference.Light;
        c.Save();
        ApplyTheme();
    }

    public string RootDirectory => LauncherConfig.Instance.RootDirectory;

    public void ApplyTheme()
    {
        var app = Application.Current;
        const string dark = "AuroraDark";
        const string light = "AuroraLight";
        var toRemove = app.Resources.MergedDictionaries
            .Where(d => d["ThemeKey"] is string k && (k == dark || k == light)).ToList();
        foreach (var d in toRemove) app.Resources.MergedDictionaries.Remove(d);
        var uri = DarkTheme
            ? "pack://application:,,,/Themes/AuroraBrushes.Dark.xaml"
            : "pack://application:,,,/Themes/AuroraBrushes.Light.xaml";
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(uri) });
    }

    public void CancelDownload() { _cts?.Cancel(); Status = "已取消下载"; }
}
