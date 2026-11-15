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

/// <summary>One row of the per-instance Java picker (round-62). Path is the
/// value persisted in GameInstance.JavaPath; empty means "auto".</summary>
public class JavaChoiceVM
{
    public JavaChoiceVM(string label, string path) { Label = label; Path = path; }
    public string Label { get; }
    public string Path { get; }

    /// <summary>Round-93: the closed ComboBox renders the selected item through
    /// SelectionBoxItemTemplate; when that template is null it falls back to
    /// ToString(), which printed the full type name
    /// ("AuroraLauncher.Desktop.ViewModels.JavaChoiceVM") in the instance card.
    /// DisplayMemberPath feeds the drop-down list, not this fallback.</summary>
    public override string ToString() => Label;
}

public partial class MainViewModel : ViewModelBase
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

        // Round-72: one manager for every download job. The dispatcher hook keeps
        // Core free of WPF; jobs self-throttle their notifications.
        _playtime = new PlaytimeTracker(LauncherConfig.Instance.RootDirectory);
        RecoverPlaytimeSessions();
        _jobManager = new DownloadJobManager(a => Dispatch(a));
        _downloadsTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _downloadsTimer.Tick += (_, _) => RefreshDownloadAggregate();
        _downloadsTimer.Start();
        InitRound79();
        InitGlass();
        InitRound83();

        // Round-82: every add/remove goes through the collection, so one handler
        // keeps "has instances" (button label + launch guard) honest.
        Instances.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasInstances));
            OnPropertyChanged(nameof(InstanceCardHint));
            OnPropertyChanged(nameof(LaunchButtonText));
            RebuildPalette();
        };
        CancelDownloadJobCommand = new RelayCommand(o => CancelDownloadJob(o as DownloadJob));
        CancelAllDownloadsCommand = new RelayCommand(_ =>
        {
            _jobManager.CancelAll();
            RefreshDownloadAggregate(force: true);
            Status = "已取消所有下载任务";
        }, _ => HasCancellableJobs);
        RetryLastFailedJobCommand = new RelayCommand(async _ => await RetryLastFailedJobAsync(),
            _ => CanRetryLastFailed);
        ClearFinishedDownloadsCommand = new RelayCommand(_ => ClearFinishedDownloads());
        ResumePendingDownloadsCommand = new RelayCommand(async _ => await ResumePendingDownloadsAsync());
        DismissPendingDownloadsCommand = new RelayCommand(_ => DismissPendingDownloads());
        ScanPendingDownloads();
        // Round-87: leftover .part files, surfaced only above the configured
        // thresholds. Same worker-thread rule as the journal replay above.
        InitRound87Commands();
        ScanDownloadResidue();
        // Round-88: storage cleanup (scan on demand only — no walking at startup).
        InitRound88Commands();
        ExportDiagnosticsCommand = new RelayCommand(async _ => await ExportDiagnosticsAsync());
        ExportLogsCommand = new RelayCommand(async _ => await ExportLogsAsync());
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
                // Round-60: these two kinds are NOT Modrinth categories —
                // never run the Modrinth search/category pipeline for them.
                if (kind == "CurseForge")
                {
                    if (CurseForgeResults.Count == 0)
                        CurseForgeStatus = "输入关键词并填写 API Key 后点「搜索」；没有 Key 也可直接点「网页搜索」。";
                    return;
                }
                if (kind == "OptiFine") { await LoadOptiFineBuildsAsync(); return; }
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
        ImportMrpackCommand = new RelayCommand(_ => ChooseAndImportMrpack());
        ApplySettingsCommand = new RelayCommand(_ => ApplySettings());
        EnablePortableModeCommand = new RelayCommand(_ => EnablePortableMode());
        ExportMrpackCommand = new RelayCommand(async _ => await ExportMrpackAsync());
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
        InstallJava21Command = new RelayCommand(async _ => await InstallJava21Async());
        DismissCrashReportCommand = new RelayCommand(_ => DismissCrashReport());
        ApplyCrashFixCommand = new RelayCommand(async o => await ApplyCrashFixAsync(o as CrashSolutionVM));
        SendCrashReportCommand = new RelayCommand(async _ => await SendCrashReportAsync());
        // Round-79: 存档备份 / Ctrl+K 全局搜索
        BackupSavesNowCommand = new RelayCommand(async _ => await BackupSavesNowAsync());
        OpenBackupFolderCommand = new RelayCommand(_ => OpenBackupFolder());
        TogglePaletteCommand = new RelayCommand(_ => TogglePalette());
        ClosePaletteCommand = new RelayCommand(_ => ClosePalette());
        OpenHelpLinkCommand = new RelayCommand(_ => OpenHelpLink());
        SearchCurseForgeCommand = new RelayCommand(async _ => await SearchCurseForgeAsync());
        OpenCurseForgeItemCommand = new RelayCommand(OpenCurseForgeItem);
        OpenCurseForgeWebCommand = new RelayCommand(_ => OpenUrl(CurseForgeService.BuildWebSearchUrl(CurseForgeQuery)));
        OpenCurseForgeKeyPageCommand = new RelayCommand(_ => OpenUrl(CurseForgeService.KeyApplyUrl));
        LoadOptiFineBuildsCommand = new RelayCommand(async _ => await LoadOptiFineBuildsAsync());
        InstallOptiFineCommand = new RelayCommand(async _ => await InstallOptiFineAsync());
        OpenOptiFineWebCommand = new RelayCommand(_ => OpenUrl(OptiFineService.WebUrl));
        PickLocalOptiFineCommand = new RelayCommand(async _ => await PickLocalOptiFineAsync());
        CheckUpdateCommand = new RelayCommand(async _ => await CheckForUpdateAsync());
        RestartToApplyCommand = new RelayCommand(_ => RestartToApply());
        OneClickUpdateCommand = new RelayCommand(async _ => await OneClickUpdateAsync());
        // Round-83: 手动释放启动器自己的内存（绝不碰游戏进程）
        DeepCleanMemoryCommand = new RelayCommand(async _ => await DeepCleanMemoryAsync());
        // Round-84: 对账 instances.json 与 instances/<id>/（幽灵实例 / 死链）
        ScanInstancesCommand = new RelayCommand(async _ => await ScanUnregisteredInstancesAsync());

        // ---- 自动更新（round-57）：启动时若已下载好更新包，先换装再继续 ----
        if (UpdateService.HasStagedUpdate())
        {
            UpdateService.ApplyStagedAndRestart();
            Environment.Exit(0);
            return;
        }

        LoadAll();
        // Round-90: the baseline must be taken from the freshly loaded config,
        // otherwise the very first page switch would claim "unsaved changes".
        InitRound90SettingsGuard();
        MaybeShowUpdateToast();
        RefreshMicrosoftLoginState();
        InitHelp();
        Navigate("Home");
        RefreshNetworkNote();
        EnsureNetworkProbe();
        _ = AutoCheckUpdateAsync();
        MaybeShowFirstRun();
    }

    private readonly UpdateService _update = new();
    private CancellationTokenSource? _updateCts;

    // ---- 更新设置（round-58）----
    public ObservableCollection<string> UpdateModeOptions { get; } = new()
    {
        UpdatePolicy.DisplayText(UpdateMode.Auto),
        UpdatePolicy.DisplayText(UpdateMode.Notify),
        UpdatePolicy.DisplayText(UpdateMode.Disabled),
    };
    public string UpdateModeText { get; set; } = UpdatePolicy.DisplayText(UpdateMode.Notify);
    public string UpdateFeedUrlText { get; set; } = "";

    private bool _isCheckingUpdate;
    /// <summary>True while checking OR downloading — drives the progress bar
    /// in 设置 → 更新 (same indeterminate-then-determinate pattern as the mod
    /// search progress indicator).</summary>
    public bool IsCheckingUpdate { get => _isCheckingUpdate; set => SetProperty(ref _isCheckingUpdate, value); }

    private bool _updateProgressIndeterminate = true;
    public bool UpdateProgressIndeterminate { get => _updateProgressIndeterminate; set => SetProperty(ref _updateProgressIndeterminate, value); }

    private double _updateProgress;
    public double UpdateProgress { get => _updateProgress; set => SetProperty(ref _updateProgress, value); }

    private string _updateStatusText = "";
    public string UpdateStatusText { get => _updateStatusText; set => SetProperty(ref _updateStatusText, value); }

    /// <summary>True once a verified update package is waiting to be applied.
    /// Drives the 「立即重启并应用更新」 button in 设置 → 更新 (round-61).</summary>
    private bool _stagedUpdateReady;
    public bool StagedUpdateReady { get => _stagedUpdateReady; set => SetProperty(ref _stagedUpdateReady, value); }

    // ---- 版本列表「一键更新」（round-62）----
    private UpdateManifest? _availableUpdate;

    public string LauncherVersionText => $"启动器 v{BuildInfo.Version} · 第 {BuildInfo.CurrentRound} 轮";

    private string _updateBannerText = UpdateBannerPolicy.Text(BuildInfo.CurrentRound, null, false);
    public string UpdateBannerText { get => _updateBannerText; set => SetProperty(ref _updateBannerText, value); }

    private void RefreshUpdateBanner() =>
        UpdateBannerText = UpdateBannerPolicy.Text(BuildInfo.CurrentRound, _availableUpdate?.Round, StagedUpdateReady);

    /// <summary>Round-62: the banner button in the game-version list. If the
    /// startup check already found a newer launcher, go straight to the
    /// two-button dialog → download → visible updater window; otherwise check
    /// first and report "already latest" clearly. Never a dead button.</summary>
    private async Task OneClickUpdateAsync()
    {
        if (IsCheckingUpdate) return;
        IsCheckingUpdate = true;
        try
        {
            var info = _availableUpdate;
            if (info == null || !UpdateService.IsNewer(info, BuildInfo.CurrentRound))
            {
                UpdateStatusText = "正在检查启动器更新...";
                Status = UpdateStatusText;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                info = await _update.CheckAsync(timeout.Token).ConfigureAwait(true);
                if (info == null)
                {
                    _availableUpdate = null;
                    RefreshUpdateBanner();
                    UpdateStatusText = _update.LastCheckMessage;
                    Status = UpdateStatusText;
                    ShowToast(UpdateStatusText);
                    return;
                }
                _availableUpdate = info;
                RefreshUpdateBanner();
            }
            if (!ShowUpdateDialog(info))
            {
                UpdateStatusText = $"已关闭更新提醒（第 {info.Round} 轮仍可一键更新）";
                return;
            }
            await DownloadUpdateAsync(info, applyImmediately: true).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { UpdateStatusText = "已取消更新"; }
        catch (Exception ex) { UpdateStatusText = "更新失败: " + ex.Message; Status = UpdateStatusText; }
        finally { IsCheckingUpdate = false; }
    }

    /// <summary>Startup check (round-58): every launch checks unless the user
    /// picked mode 3. Mode 1 downloads automatically; mode 2 shows the
    /// two-button reminder; failures are silent so the check never annoys.</summary>
    private async Task AutoCheckUpdateAsync()
    {
        var mode = LauncherConfig.Instance.UpdateMode;
        if (!UpdatePolicy.ShouldCheckOnStartup(mode)) return;
        try
        {
            // Let the main window finish loading (and the first-run wizard
            // settle) before any modal dialog can appear.
            await Task.Delay(1200).ConfigureAwait(true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var info = await _update.CheckAsync(timeout.Token).ConfigureAwait(true);
            var action = UpdatePolicy.Decide(mode, info != null, manual: false);
            // Make the finding visible on the version-list banner too, even
            // when the user dismisses the modal or mode 1 downloads silently.
            if (info != null) { _availableUpdate = info; RefreshUpdateBanner(); }
            if (action == UpdateAction.None || info == null) return;

            if (action == UpdateAction.AutoDownload)
            {
                ShowToast($"发现新版本（第 {info.Round} 轮），正在后台下载…（设置 → 更新 可查看进度）");
                await DownloadUpdateAsync(info, applyImmediately: false).ConfigureAwait(true);
            }
            else if (ShowUpdateDialog(info))
            {
                await DownloadUpdateAsync(info, applyImmediately: true).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            AddLog("[Update] 启动检查失败（不影响使用）: " + ex.Message);
        }
    }

    /// <summary>Shows the startup reminder. Returns true ONLY when the user
    /// picked 「立即更新」; the dialog has exactly two buttons.</summary>
    private bool ShowUpdateDialog(UpdateManifest info)
    {
        try
        {
            var dlg = new UpdateDialog(info);
            if (Application.Current.MainWindow is { IsLoaded: true } main) dlg.Owner = main;
            return dlg.ShowDialog() == true && dlg.UpdateNow;
        }
        catch (Exception ex)
        {
            AddLog("[Update] 更新提醒弹窗显示失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>Round-61: explicit "apply now" action. The updater host shows
    /// a visible progress window, so the user always knows the short gap is
    /// an intentional install, not a crash.</summary>
    private void RestartToApply()
    {
        if (!UpdateService.HasStagedUpdate())
        {
            UpdateStatusText = "没有待安装的更新包，可先点「检查更新」";
            return;
        }
        UpdateStatusText = "正在打开更新窗口，启动器即将重启…";
        ShowToast("正在应用更新并重启，期间会显示进度窗口…");
        UpdateService.ApplyStagedAndRestart();
        Environment.Exit(0);
    }

    /// <summary>Manual 「检查更新」 from Settings: shows a progress bar while
    /// checking, then the same two-button dialog. Works in ALL modes (including
    /// mode 3) because it is an explicit user action.</summary>
    private async Task CheckForUpdateAsync()
    {
        if (IsCheckingUpdate) return;
        IsCheckingUpdate = true;
        UpdateProgressIndeterminate = true;
        UpdateProgress = 0;
        UpdateStatusText = "正在检查更新...";
        Status = UpdateStatusText;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var info = await _update.CheckAsync(timeout.Token).ConfigureAwait(true);
            _availableUpdate = info;
            RefreshUpdateBanner();
            if (info == null)
            {
                UpdateStatusText = _update.LastCheckMessage;
                Status = UpdateStatusText;
                return;
            }
            UpdateStatusText = _update.LastCheckMessage;
            Status = UpdateStatusText;
            if (!ShowUpdateDialog(info))
            {
                UpdateStatusText = "已关闭更新提醒（可随时再次检查）";
                return;
            }
            await DownloadUpdateAsync(info, applyImmediately: true).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText = "已取消更新";
            Status = UpdateStatusText;
        }
        catch (Exception ex)
        {
            UpdateStatusText = "更新失败: " + ex.Message;
            Status = UpdateStatusText;
        }
        finally { IsCheckingUpdate = false; }
    }

    private async Task DownloadUpdateAsync(UpdateManifest info, bool applyImmediately)
    {
        _updateCts = new CancellationTokenSource();
        IsCheckingUpdate = true;
        UpdateProgressIndeterminate = false;
        UpdateProgress = 0;
        UpdateStatusText = "正在下载更新...";
        Status = "正在下载启动器更新...";
        try
        {
            await _update.DownloadStagedAsync(info, p => Dispatch(() =>
            {
                var total = p.TotalBytes > 0 ? p.TotalBytes : info.Size;
                var pct = total > 0 ? Math.Clamp(p.DownloadedBytes * 100.0 / total, 0, 100) : 0;
                UpdateProgress = pct;
                const double mb = 1024.0 * 1024.0;
                var size = total > 0
                    ? $"{p.DownloadedBytes / mb:0.0} / {total / mb:0.0} MB"
                    : "正在获取文件大小";
                var speed = p.SpeedBytesPerSec >= mb
                    ? $"{p.SpeedBytesPerSec / mb:0.0} MB/s"
                    : $"{p.SpeedBytesPerSec / 1024.0:0} KB/s";
                UpdateStatusText = $"正在下载更新 {pct:0}%（{size} · {speed}）";
                Status = UpdateStatusText; // main status bar follows the download too
            }), _updateCts.Token).ConfigureAwait(true);
            UpdateProgress = 100;
            // Round-74: say whether the delta was used — the user should see the
            // saving instead of taking it on faith.
            var modeNote = string.IsNullOrEmpty(_update.LastDownloadMode) ? "" : $"（{_update.LastDownloadMode}）";
            if (applyImmediately)
            {
                UpdateStatusText = "更新下载完成" + modeNote + "，即将显示安装进度窗口…";
                Status = UpdateStatusText;
                ShowToast("更新下载完成，接下来会显示安装进度窗口…");
                await Task.Delay(1200).ConfigureAwait(true);
                UpdateService.ApplyStagedAndRestart();
                Environment.Exit(0);
            }
            else
            {
                StagedUpdateReady = true;
                RefreshUpdateBanner();
                UpdateStatusText = "新版本已就绪" + modeNote + "：点「立即重启并应用更新」，或下次启动时自动完成";
                ShowToast("新版本已就绪：重启时会显示安装进度窗口。");
                Status = UpdateStatusText;
            }
        }
        finally { _updateCts = null; IsCheckingUpdate = false; }
    }

    // ---- 网络环境自动感知：测速决定"优先哪条"，并允许用户强制单线路 ----
    /// <summary>Round-85: one cached probe, shared by every caller. Never awaited
    /// on the download path — the download path only reads
    /// <see cref="NetworkProbe.Preference"/>.</summary>
    private void EnsureNetworkProbe()
    {
        if (_probeInFlight) return;
        _probeInFlight = true;
        _ = ProbeNetworkAsync();
    }

    private bool _probeInFlight;

    private async Task ProbeNetworkAsync()
    {
        try
        {
            var probe = await NetworkProbe.ProbeCachedAsync().ConfigureAwait(true);
            if (probe == null) { _log.Warn("Net", "测速没有拿到结果，沿用当前线路"); return; }
            RefreshNetworkNote();
            _log.Info("Net", $"Network probe: {probe}；策略 {DownloadRoutePolicy.DisplayName(LauncherConfig.Instance.RoutePolicy)}，" +
                             $"优先 {NetworkProbe.Preference}。");
        }
        catch (Exception ex)
        {
            _log.Warn("Net", $"Network probe failed: {ex.Message}");
        }
        finally
        {
            _probeInFlight = false;
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
        DownloadJob? job = null;
        try
        {
            var mc = _recommendedVersionId;
            var name = $"Minecraft {mc}";
            var inst = _svc.Instances.Create(name, mc, ModLoaderType.Vanilla, null);
            Instances.Add(inst);
            SelectedInstance = inst;
            job = await BeginJobAsync("实例", $"{name} 安装", name);
            InitStages(job, BuildStages(ModLoaderType.Vanilla), $"{name} 安装");
            FirstRunProgress = "正在安装（进度见「下载管理」）…";
            await _svc.InstallWithLoaderAsync(inst, job.Report,
                s => Dispatch(() => AddLog("[Install] " + s)), job.Token, job.EnterStage);
            _svc.Instances.Update(inst);
            FinishJob(job, null, false, $"实例「{inst.Name}」安装完成！");
            FirstRunProgress = "安装完成！";
            FinishFirstRun();
        }
        catch (OperationCanceledException) { FinishJob(job, null, true); FirstRunProgress = "已取消。"; }
        catch (Exception ex)
        {
            FinishJob(job, ex, false);
            FirstRunProgress = "安装失败: " + ex.Message + "（可跳过引导，稍后在「游戏下载」页重试）";
        }
        finally { FirstRunInstallEnabled = true; }
    }

    private void FinishFirstRun()
    {
        FirstRunWizardVisible = false;
        var cfg = LauncherConfig.Instance;
        cfg.FirstRunCompleted = true;
        // Round-83: a brand-new user has no idea what -Xmx means, and 4096 MB is
        // wrong on a 4 GB laptop. Turn the automatic mode on exactly once, here,
        // where we know this is a fresh install. Existing configs keep whatever
        // they already have, so an upgrade never changes a working setup.
        if (!cfg.AutoMemory)
        {
            cfg.AutoMemory = true;
            AutoMemory = true;
            cfg.MaxMemoryMb = EffectiveMemoryMb();
        }
        cfg.Save();
    }


    // ---- collections ----
    public BulkObservableCollection<VersionManifestEntry> Versions { get; } = new();
    public ObservableCollection<GameInstance> Instances { get; } = new();

    /// <summary>Round-82: "no instances yet" is a normal first-run state, not an
    /// error. The launch button reads this to relabel itself and the command
    /// guards on it, so the button is never a dead click.</summary>
    public bool HasInstances => Instances.Count > 0;

    /// <summary>Placeholder-friendly hero text ("未选择实例" instead of an empty gap).</summary>
    public string SelectedInstanceNameText => SelectedInstance?.Name ?? "未选择实例";

    public string SelectedAccountNameText => SelectedAccount?.Username ?? "未登录";

    /// <summary>Hint under the instance card, switched by the same state.</summary>
    public string InstanceCardHint => LaunchGuardPolicy.InstanceHint(HasInstances);

    /// <summary>"启动游戏" or "去下载游戏" — from the same policy that blocks the
    /// command, so the label can never promise something the click won't do.</summary>
    public string LaunchButtonText => LaunchGuardPolicy.ButtonText(HasInstances);
    public ObservableCollection<Account> Accounts { get; } = new();
    public ObservableCollection<ModInfo> Mods { get; } = new();
    /// <summary>Round-69: bound to the Logs ListBox through <see cref="LogsView"/>.
    /// Bulk collection so re-entering the page publishes the backlog with ONE
    /// Reset instead of one notification per line.</summary>
    public BulkObservableCollection<string> Logs { get; } = new();
    public BulkObservableCollection<ContentItemVM> ModSearchResults { get; } = new();
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
    private static readonly (ModLoaderType Type, string Name, string Short)[] LoaderCardDefs =
    {
        (ModLoaderType.Forge, "Forge", "FO"),
        (ModLoaderType.NeoForge, "NeoForge", "NF"),
        (ModLoaderType.Fabric, "Fabric", "FA"),
        (ModLoaderType.Quilt, "Quilt", "QU"),
    };

    private long _cardsGenForFlight;
    private CoalescingSingleFlight<string>? _loaderCardsFlight;

    /// <summary>Round-64: skeletons render SYNCHRONOUSLY on every selection.
    /// The 180 ms debounce delays ONLY the network batch — instant feedback
    /// from round-47 must never wait. Also starts a new generation so async
    /// results for an older selection are discarded.</summary>
    private void ShowLoaderCardSkeletons()
    {
        var gen = _loaderCardsGate.Begin();
        _cardsGenForFlight = gen;
        Dispatch(() =>
        {
            _activeCardsGen = gen;
            LoaderCards.Clear();
            foreach (var d in LoaderCardDefs)
            {
                var skeleton = new LoaderCardVM(d.Type, d.Name, d.Short, OnLoaderVersionPicked, gen);
                skeleton.Versions.Add(LoaderCardVM.NoneChoice);
                skeleton.Versions.Add(LoaderCardVM.AutoChoice);
                skeleton.IsLoading = true;
                LoaderCards.Add(skeleton);
            }
        });
    }

    /// <summary>Coalescing single-flight (round-64): in-progress requests
    /// collapse into exactly ONE rerun using the LATEST MC version — neither
    /// queued (N reruns) nor dropped (first choice wins). The pure contract
    /// lives in CoalescingSingleFlight and is covered by a dedicated test.</summary>
    public Task LoadLoaderCardsAsync()
    {
        var mc = SelectedVersion?.Id ?? SelectedInstance?.MinecraftVersion ?? "";
        if (string.IsNullOrWhiteSpace(mc)) return Task.CompletedTask;
        _loaderCardsFlight ??= new CoalescingSingleFlight<string>(LoadLoaderCardsCoreAsync);
        return _loaderCardsFlight.RequestAsync(mc);
    }

    private async Task LoadLoaderCardsCoreAsync(string mc)
    {
        if (string.IsNullOrWhiteSpace(mc)) return;
        // Called straight from the page Loaded (no selection change) →
        // skeletons may not exist yet, show them before any network IO.
        if (LoaderCards.Count == 0) ShowLoaderCardSkeletons();
        var gen = _cardsGenForFlight;
        var defs = LoaderCardDefs;

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

        DownloadJob? job = null;
        try
        {
            var inst = _svc.Instances.Create(name, mc, loader, loaderVersion);
            Instances.Add(inst);
            SelectedInstance = inst;
            job = await BeginJobAsync("实例", $"{name} 安装", name);
            InitStages(job, BuildStages(loader), $"{name} 安装");
            Status = $"正在安装 {mc} ({loader}) ...";
            await _svc.InstallWithLoaderAsync(inst, job.Report,
                s => Dispatch(() => AddLog("[Install] " + s)), job.Token, job.EnterStage);
            _svc.Instances.Update(inst);
            // Fabric 前置（round-47）：装完 Fabric 实例即检查 Fabric API ——
            // 设置开启（默认）时告知后自动安装适配版本，关闭时弹窗询问。
            var fabricApiInstalled = false;
            if (loader == ModLoaderType.Fabric)
                fabricApiInstalled = await EnsureFabricApiAsync(inst, job.Token, $"安装实例（Fabric 加载器）");
            ScanMods();
            var doneText = $"实例「{inst.Name}」安装完成！到首页点「启动游戏」即可游玩。"
                + (fabricApiInstalled ? "（已自动安装适配的 Fabric API 前置）" : "");
            FinishJob(job, null, false, doneText);
            Status = $"安装完成: {inst.Name}" + (fabricApiInstalled ? "（含 Fabric API 前置）" : "");
        }
        catch (OperationCanceledException) { FinishJob(job, null, true); Status = "已取消下载（未完成的临时文件已清理）"; }
        catch (Exception ex) { FinishJob(job, ex, false); Status = "安装失败: " + ex.Message; }
        finally { RefreshDownloadAggregate(force: true); }
    }

    public DownloadStatus Download { get; } = new();

    // ---- properties ----
    public string Status
    {
        get => _status;
        set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(HeaderText)); }
    }
    public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }

    /// <summary>Round-96: the title bar has ONE source of truth. While any job
    /// is alive it is driven by that job (title + live stage); when the last job
    /// finishes, an install-driven header falls back to 空闲 instead of leaving
    /// a stale "正在安装…" above an empty list. Non-install status text passes
    /// through unchanged (update checks, exports, game exit, errors).</summary>
    public string HeaderText
    {
        get
        {
            var live = _jobManager.Jobs.FirstOrDefault(j => !j.IsFinished);
            if (live != null)
            {
                var stage = string.IsNullOrWhiteSpace(live.StageText) ? live.StateText : live.StageText;
                return $"正在安装… {live.Title} · {stage}";
            }
            return _jobDrivenStatus ? "空闲" : _status;
        }
    }

    /// <summary>Round-96: 全部取消 must be grey when there is nothing to cancel.</summary>
    public bool HasCancellableJobs => _jobManager.Jobs.Any(j => j.CanCancel);
    public bool CanRetryLastFailed => _lastFailedRepairInstanceId != null;
    private bool _jobDrivenStatus;
    private string? _lastFailedRepairInstanceId;
    public string VersionFilter { get => _versionFilter; set { if (SetProperty(ref _versionFilter, value)) _ = LoadVersionsAsync(); } }
    private DispatcherTimer? _loaderCardsDebounce;
    private string _debounceTargetVersion = "";

    /// <summary>Round-64: the debounce covers ONLY the network batch. Skeleton
    /// cards are rendered synchronously in the setter (round-47 instant
    /// feedback), while repeated selections collapse into one 180 ms timer.</summary>
    public VersionManifestEntry? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (!SetProperty(ref _selectedVersion, value)) return;
            ShowLoaderCardSkeletons();
            ScheduleLoaderCardRefresh();
        }
    }

    private void ScheduleLoaderCardRefresh()
    {
        _loaderCardsDebounce ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _loaderCardsDebounce.Stop();
        _loaderCardsDebounce.Tick -= OnLoaderCardsDebounceTick;
        _loaderCardsDebounce.Tick += OnLoaderCardsDebounceTick;
        _debounceTargetVersion = SelectedVersion?.Id ?? "";
        _loaderCardsDebounce.Start();
    }

    private void OnLoaderCardsDebounceTick(object? sender, EventArgs e)
    {
        _loaderCardsDebounce?.Stop();
        // Within the debounce window the user may have left the page or picked
        // another version — never fire a stale 4-route loader fetch.
        if (!LoaderDebouncePolicy.ShouldFire(CurrentPageKey, _debounceTargetVersion,
                SelectedVersion?.Id ?? "")) return;
        _ = LoadLoaderCardsAsync();
    }

    /// <summary>Called on every DownloadView re-attach. If the debounce was
    /// cancelled when the user navigated away, re-arm it so the cards never
    /// stay at "加载中…" forever. Lightweight: no network by itself.</summary>
    public void EnsureLoaderCardsRefresh()
    {
        if (CurrentPageKey != "Download") return;
        if (LoaderCards.Count == 0) { _ = LoadLoaderCardsAsync(); return; }
        if (LoaderCards.Any(c => c.IsLoading) &&
            (_loaderCardsDebounce == null || !_loaderCardsDebounce.IsEnabled))
            ScheduleLoaderCardRefresh();
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
                OnPropertyChanged(nameof(SelectedInstanceJavaPath));
                OnPropertyChanged(nameof(InstanceJavaHint));
                OnPropertyChanged(nameof(SelectedInstanceNameText));
                RefreshInstanceJavaChoices();
                // Round-79: the backup + performance cards are per-instance.
                RefreshBackupInfo();
                RefreshPerformanceCard();
                // Round-83: the recommendation depends on THIS instance's mods.
                RefreshMemoryAdvice();
                RebuildPalette();
            }
        }
    }
    public Account? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (SetProperty(ref _selectedAccount, value))
                OnPropertyChanged(nameof(SelectedAccountNameText));
        }
    }
    public ModInfo? SelectedMod { get => _selectedMod; set => SetProperty(ref _selectedMod, value); }
    public string MicrosoftMessage { get => _microsoftMessage; set => SetProperty(ref _microsoftMessage, value); }
    public bool DarkTheme { get => _darkTheme; set => SetProperty(ref _darkTheme, value); }
    public string OfflineName { get => _offlineName; set => SetProperty(ref _offlineName, value); }
    public string CurrentPageKey { get => _currentPageKey; set => SetProperty(ref _currentPageKey, value); }

    private string _installTitle = "";
    /// <summary>Header of the install flow chart, e.g. "1.21 (NeoForge) 安装".</summary>
    public string InstallTitle { get => _installTitle; set => SetProperty(ref _installTitle, value); }

    public List<string> VersionFilterOptions => new() { "release", "snapshot", "all" };
    /// <summary>Round-85: Auto / Official-only / Mirror-only.</summary>
    public List<string> DownloadSourceOptions { get; } = new()
    {
        DownloadRoutePolicy.DisplayName(DownloadPolicy.Auto),
        DownloadRoutePolicy.DisplayName(DownloadPolicy.Official),
        DownloadRoutePolicy.DisplayName(DownloadPolicy.Mirror)
    };
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
    /// <summary>Round-70: 「导出日志」— one plain-text file for bug reports.</summary>
    public RelayCommand ExportLogsCommand { get; }
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
    public RelayCommand EnablePortableModeCommand { get; }
    public RelayCommand ExportMrpackCommand { get; }
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
    public RelayCommand InstallJava21Command { get; }
    public RelayCommand DismissCrashReportCommand { get; }
    public RelayCommand ApplyCrashFixCommand { get; }
    public RelayCommand SendCrashReportCommand { get; }
    /// <summary>Round-83: user-triggered release of the launcher's own caches.
    /// Declared here (not in the partial) because the command-binding guard scans
    /// this file for VM command properties.</summary>
    public RelayCommand DeepCleanMemoryCommand { get; }
    /// <summary>Round-84: reconcile the instance list with the folders on disk.</summary>
    public RelayCommand ScanInstancesCommand { get; }
    public RelayCommand BackupSavesNowCommand { get; }
    public RelayCommand OpenBackupFolderCommand { get; }
    public RelayCommand TogglePaletteCommand { get; }
    public RelayCommand ClosePaletteCommand { get; }
    public RelayCommand OpenHelpLinkCommand { get; }
    public RelayCommand SearchCurseForgeCommand { get; }
    public RelayCommand OpenCurseForgeItemCommand { get; }
    public RelayCommand OpenCurseForgeWebCommand { get; }
    public RelayCommand OpenCurseForgeKeyPageCommand { get; }
    public RelayCommand LoadOptiFineBuildsCommand { get; }
    public RelayCommand InstallOptiFineCommand { get; }
    public RelayCommand OpenOptiFineWebCommand { get; }
    public RelayCommand PickLocalOptiFineCommand { get; }
    public RelayCommand CheckUpdateCommand { get; }
    public RelayCommand RestartToApplyCommand { get; }
    public RelayCommand OneClickUpdateCommand { get; }

    // ---- round-72: multi-job download center ----
    // Every install (instance / modpack / mod / loader / java / repair) is its own
    // DownloadJob with its own token, stages and progress. The legacy single
    // _cts + global Download object were the reason a second install hijacked
    // the first one's cancellation and why the page could only describe one job.
    private readonly DownloadJobManager _jobManager;
    private readonly DispatcherTimer _downloadsTimer;

    /// <summary>Round-77: playtime sessions. Written to disk so a launcher killed
    /// with the game still running can be settled on the next start.</summary>
    private readonly PlaytimeTracker _playtime;

    /// <summary>Legacy single token, still used by the two flows that were not
    /// converted to jobs (launch-time file repair and script export). Those are
    /// short, sequential actions — they do not belong in the download center,
    /// but they must keep a way to be cancelled.</summary>
    private CancellationTokenSource? _cts;
    private int _seenJobVersion = -1;

    /// <summary>All visible download jobs (newest first) — bound by 下载管理.</summary>
    public ObservableCollection<DownloadJob> DownloadJobs => _jobManager.Jobs;

    private int _downloadActive;
    public int DownloadActiveCount { get => _downloadActive; private set => SetProperty(ref _downloadActive, value); }

    private int _downloadQueued;
    public int DownloadQueuedCount { get => _downloadQueued; private set => SetProperty(ref _downloadQueued, value); }

    private int _downloadAggregate;
    public int DownloadAggregatePercent { get => _downloadAggregate; private set => SetProperty(ref _downloadAggregate, value); }

    private string _downloadSpeedText = "0 B/s";
    public string DownloadSpeedText { get => _downloadSpeedText; private set => SetProperty(ref _downloadSpeedText, value); }

    private string _downloadSummary = "当前没有下载任务";
    public string DownloadSummaryText { get => _downloadSummary; private set => SetProperty(ref _downloadSummary, value); }

    private bool _hasDownloadJobs;
    public bool HasDownloadJobs { get => _hasDownloadJobs; private set => SetProperty(ref _hasDownloadJobs, value); }

    private bool _canClearFinished;
    public bool CanClearFinished { get => _canClearFinished; private set => SetProperty(ref _canClearFinished, value); }

    private DownloadJob? _selectedDownloadJob;
    /// <summary>Row selected in the download manager; its stage chart is shown below the list.</summary>
    public DownloadJob? SelectedDownloadJob
    {
        get => _selectedDownloadJob;
        set => SetProperty(ref _selectedDownloadJob, value);
    }

    // ---- round-74: resume after a crash / forced kill ----
    private IReadOnlyList<AuroraCore.Download.PendingDownload> _pendingDownloads =
        Array.Empty<AuroraCore.Download.PendingDownload>();

    private bool _hasPendingDownloads;
    public bool HasPendingDownloads { get => _hasPendingDownloads; private set => SetProperty(ref _hasPendingDownloads, value); }

    private string _pendingDownloadsText = "";
    public string PendingDownloadsText { get => _pendingDownloadsText; private set => SetProperty(ref _pendingDownloadsText, value); }

    /// <summary>Startup scan: a killed process leaves .part files behind. Runs on
    /// a worker thread — the journal replay touches disk and must not delay the
    /// first frame (round-69 lesson).</summary>
    private void ScanPendingDownloads()
    {
        _ = Task.Run(() =>
        {
            try
            {
                DownloadJournal.Flush(400);
                var list = DownloadJournal.Load(LauncherConfig.Instance.RootDirectory);
                Dispatch(() => ApplyPendingDownloads(list));
            }
            catch (Exception ex)
            {
                Logger.Instance.Trace("Download", "扫描未完成下载失败（忽略）: " + ex.Message);
            }
        });
    }

    private void ApplyPendingDownloads(IReadOnlyList<AuroraCore.Download.PendingDownload> list)
    {
        _pendingDownloads = list;
        HasPendingDownloads = list.Count > 0;
        if (list.Count == 0) { PendingDownloadsText = ""; return; }

        var total = list.Sum(p => p.ExpectedSize > 0 ? p.ExpectedSize : p.PartialBytes);
        var done = list.Sum(p => p.PartialBytes);
        var percent = total > 0 ? (int)Math.Round(done * 100.0 / total) : 0;
        PendingDownloadsText =
            $"检测到上次未完成的下载：{list.Count} 个文件，已下载 {JobProgressPolicy.FormatBytes(done)}" +
            (total > done ? $" / {JobProgressPolicy.FormatBytes(total)}（{percent}%）" : "") +
            "。继续只补下剩余部分，不会从头开始。";
        Logger.Instance.Info("Download", $"发现 {list.Count} 个未完成下载（{JobProgressPolicy.FormatBytes(done)}），等待用户选择继续或忽略。");
    }

    /// <summary>Re-enqueues the interrupted files as ONE job. The engine resumes
    /// from each .part via HTTP Range, so this is not a restart.</summary>
    private async Task ResumePendingDownloadsAsync()
    {
        var pending = _pendingDownloads.ToList();
        if (pending.Count == 0) { Status = "没有需要恢复的下载"; return; }

        DownloadJob? job = null;
        try
        {
            job = await BeginJobAsync("恢复", $"继续未完成的下载（{pending.Count} 个文件）", "");
            job.SetStageText($"继续下载：{pending[0].FileName}" + (pending.Count > 1 ? $" 等 {pending.Count} 个文件" : ""));
            var entries = pending.Select(p => new DownloadEntry
            {
                Url = p.Url,
                Destination = p.Destination,
                ExpectedSha1 = p.ExpectedSha1,
                ExpectedSize = p.ExpectedSize > 0 ? p.ExpectedSize : null,
                Required = true,
                DisplayName = p.FileName
            }).ToList();

            var engine = new DownloadEngine();
            await engine.DownloadAsync(entries, job.Report, job.Token);

            _pendingDownloads = Array.Empty<AuroraCore.Download.PendingDownload>();
            HasPendingDownloads = false;
            PendingDownloadsText = "";
            DownloadJournal.Flush(800);
            FinishJob(job, null, false, $"已恢复并完成 {pending.Count} 个未完成的下载");
            Status = $"已恢复并完成 {pending.Count} 个未完成的下载";
        }
        catch (OperationCanceledException) { FinishJob(job, null, true); Status = "已取消恢复（未完成的临时文件已清理）"; }
        catch (Exception ex) { FinishJob(job, ex, false); Status = "恢复失败: " + ex.Message; }
        finally { RefreshDownloadAggregate(force: true); }
    }

    /// <summary>"忽略" must also free the disk: leaving a 5 GB .part behind after
    /// the user said "no" is worse than re-downloading it later.</summary>
    private void DismissPendingDownloads()
    {
        var result = DownloadJournal.Discard(LauncherConfig.Instance.RootDirectory, deletePartials: true);
        Logger.Instance.Info("Download", $"用户忽略未完成下载，清理 {result} 个临时文件。");
        _pendingDownloads = Array.Empty<AuroraCore.Download.PendingDownload>();
        HasPendingDownloads = false;
        PendingDownloadsText = "";
        Status = result > 0 ? $"已忽略并清理 {result} 个未完成的临时文件" : "已忽略未完成的下载";
    }

    public RelayCommand CancelDownloadJobCommand { get; }
    public RelayCommand ResumePendingDownloadsCommand { get; } = null!;
    public RelayCommand DismissPendingDownloadsCommand { get; } = null!;
    public RelayCommand CancelAllDownloadsCommand { get; }
    public RelayCommand RetryLastFailedJobCommand { get; }
    public RelayCommand ClearFinishedDownloadsCommand { get; }

    /// <summary>Creates a job, waits for a download slot (max 3 at once) and
    /// switches to the download manager. Throws OperationCanceledException when
    /// the job is cancelled while queued.</summary>
    private async Task<DownloadJob> BeginJobAsync(string kind, string title, string instance,
        bool keepPreparing = false)
    {
        var job = _jobManager.Create(kind, title, instance ?? "");
        job.MarkPreparing();
        _jobDrivenStatus = true;
        OnPropertyChanged(nameof(HeaderText));
        SelectedDownloadJob = job;
        if (CurrentPageKey != "Downloads") Navigate("Downloads");
        RefreshDownloadAggregate(force: true);
        await _jobManager.StartAsync(job, markRunning: !keepPreparing);
        RefreshDownloadAggregate(force: true);
        Logger.Instance.Info("Download",
            $"任务开始：{kind}「{title}」" + (string.IsNullOrWhiteSpace(instance) ? "" : $"（实例 {instance}）"));
        return job;
    }

    /// <summary>Single exit point for a job: updates the row, releases the slot
    /// and keeps the page banner in sync.</summary>
    private void FinishJob(DownloadJob? job, Exception? error, bool canceled, string? successText = null)
    {
        if (job == null) return;
        _jobManager.Finish(job, error, canceled, successText);
        if (canceled) Status = "已取消下载";
        else if (error != null)
        {
            InstallSuccess = null;
            InstallError = successText == null ? error.Message : successText;
            Status = "任务失败：" + error.Message;
        }
        else if (successText != null)
        {
            InstallError = null;
            InstallSuccess = successText;
        }
        if (error != null && job.Kind == "修复") _lastFailedRepairInstanceId = job.InstanceName;
        else if (error == null && !canceled) _lastFailedRepairInstanceId = null;
        _jobDrivenStatus = _jobManager.Jobs.Any(j => !j.IsFinished);
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(CanRetryLastFailed));
        RetryLastFailedJobCommand.RaiseCanExecuteChanged();
        RefreshDownloadAggregate(force: true);
    }

    /// <summary>Round-96: a repair is a DOWNLOAD JOB, not a global status
    /// string. The row exists as 准备中 before the first byte moves, follows
    /// every stage, and stays in the list (red, retryable) on failure.</summary>
    private async Task RunRepairJobAsync(GameInstance instance)
    {
        var job = await BeginJobAsync("修复", $"补全 {instance.Name} 的文件", instance.Name,
            keepPreparing: true);
        InitStages(job, BuildStages(instance.LoaderType), $"{instance.Name} 文件补全");
        job.SetStageText("准备中… 正在检查缺失文件");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _cts?.Token ?? CancellationToken.None, job.Token);
        try
        {
            await _svc.RepairAsync(instance,
                pr => { OnDownloadProgress(pr); job.Report(pr); },
                line =>
                {
                    Dispatch(() => AddLog("[Repair] " + line));
                    // Installer output is tens of thousands of patch lines —
                    // only milestone lines should touch the UI stage text.
                    if (line.Contains("预填充") || line.Contains("安装器") ||
                        line.Contains("launcher_profiles") || line.Contains("认领") ||
                        line.Contains("复用已安装"))
                        job.NoteProgress(line);
                },
                linked.Token,
                key => OnInstallStage(job, key));
            _svc.Instances.Update(instance);
            CompleteStages();
            FinishJob(job, null, false, $"实例「{instance.Name}」文件已补全");
        }
        catch (OperationCanceledException)
        {
            if (!job.IsFinished) FinishJob(job, null, true);
            throw;
        }
        catch (Exception ex)
        {
            if (!job.IsFinished) FinishJob(job, ex, false);
            throw;
        }
    }

    /// <summary>The red banner's 重试 button: re-run the failed repair in
    /// place, as a fresh job so the old failure row stays for inspection.</summary>
    private async Task RetryLastFailedJobAsync()
    {
        if (string.IsNullOrEmpty(_lastFailedRepairInstanceId))
        {
            Status = "没有可重试的失败任务";
            return;
        }
        var inst = _svc.Instances.LoadAll()
            .FirstOrDefault(i => string.Equals(i.Name, _lastFailedRepairInstanceId, StringComparison.Ordinal));
        if (inst == null)
        {
            Status = "找不到原实例，请到「实例管理」重新点补全";
            return;
        }
        _cts = new CancellationTokenSource();
        try
        {
            await RunRepairJobAsync(inst).ConfigureAwait(true);
            Status = "补全完成";
        }
        catch (OperationCanceledException) { Status = "已取消补全"; }
        catch (Exception ex) { Status = "补全失败：" + ex.Message; }
    }

    /// <summary>Round-96 watchdog: a job that reports nothing for too long is
    /// failed in place (row stays red + retryable) and its linked token is
    /// cancelled so the underlying install really stops.</summary>
    private void CheckStalledJobs()
    {
        var now = DateTime.UtcNow;
        foreach (var job in _jobManager.Jobs.ToList())
        {
            if (job.IsFinished) continue;
            var limit = JobWatchdogPolicy.TimeoutSeconds(job.State, job.CurrentStageKey);
            if (!JobWatchdogPolicy.IsStalled(job.LastProgressUtc, now, limit)) continue;
            var msg = JobWatchdogPolicy.StalledMessage(job.LastProgressUtc, now, limit);
            Logger.Instance.Warn("Download", $"任务「{job.Title}」{msg}");
            job.AbortAsFailure(msg);
            FinishJob(job, new TimeoutException(msg), canceled: false);
        }
    }

    /// <summary>Pulls the aggregate out of the manager. Called by the timer and
    /// after state changes — never from the per-chunk progress callback.</summary>
    private bool _windowVisible = true;
    private long _lastHiddenRefreshTick;

    /// <summary>Round-74: while the window is in the tray nothing a user can see
    /// changes, so the 250 ms aggregate refresh is dropped to 1 Hz. Downloads are
    /// unaffected — only the UI numbers wait.</summary>
    public void SetWindowVisible(bool visible)
    {
        _windowVisible = visible;
        if (visible) RefreshDownloadAggregate(force: true);
    }

    private void RefreshDownloadAggregate(bool force = false)
    {
        CheckStalledJobs(); // Round-96: must run even when Version is unchanged
        if (!force && _seenJobVersion == _jobManager.Version) return;
        if (!force && !_windowVisible)
        {
            var now = Environment.TickCount64;
            if (now - _lastHiddenRefreshTick < 1000) return;
            _lastHiddenRefreshTick = now;
        }
        _seenJobVersion = _jobManager.Version;

        var (percent, speed, active, queued) = _jobManager.Snapshot();
        DownloadActiveCount = active;
        DownloadQueuedCount = queued;
        DownloadAggregatePercent = percent;
        DownloadSpeedText = JobProgressPolicy.FormatSpeed(speed);
        HasDownloadJobs = _jobManager.Jobs.Count > 0;
        CanClearFinished = _jobManager.FinishedCount > 0;
        DownloadSummaryText = _jobManager.Jobs.Count == 0
            ? "当前没有下载任务"
            : queued > 0
                ? $"{active} 个进行中 · {queued} 个排队"
                : active > 0 ? $"{active} 个进行中" : "全部任务已结束";

        // Legacy status object: the status-bar dot and any other reader.
        Download.Active = active > 0 || queued > 0;
        Download.Percent = percent;
        Download.Speed = DownloadSpeedText;
        Download.Detail = DownloadSummaryText;
        IsBusy = active > 0 || queued > 0;
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(HasCancellableJobs));
        CancelAllDownloadsCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Round-73: called by the close guard so .part cleanup runs while
    /// the process is still alive (a hard exit would leave temp files behind).</summary>
    public void CancelAllDownloads()
    {
        _jobManager.CancelAll();
        RefreshDownloadAggregate(force: true);
        Logger.Instance.Info("Download", "窗口关闭：已取消所有下载任务");
    }

    /// <summary>Round-77: a session file left behind means the launcher (or the
    /// whole machine) died while the game was running. Credit the time up to the
    /// last heartbeat instead of losing the session.</summary>
    private void RecoverPlaytimeSessions()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var recovered = _playtime.RecoverOrphans(pid =>
                {
                    try { return !System.Diagnostics.Process.GetProcessById(pid).HasExited; }
                    catch { return false; }
                });
                if (recovered.Count == 0) return;
                foreach (var (instanceId, duration, startedAt) in recovered)
                {
                    var updated = _svc.Instances.AddPlaytime(instanceId, duration, startedAt.ToLocalTime());
                    if (updated != null) Dispatch(() => RefreshInstanceRow(updated));
                }
                Dispatch(() => Status = $"已补记上次未结算的游玩时长（{recovered.Count} 个实例）");
            }
            catch (Exception ex)
            {
                Logger.Instance.Trace("Playtime", "恢复游玩会话失败（忽略）: " + ex.Message);
            }
        });
    }

    /// <summary>Replaces the row so bindings (list + details panel) pick up new
    /// playtime numbers. AddPlaytime returns the freshly loaded copy, so the
    /// reference differs and SetProperty fires.</summary>
    private void RefreshInstanceRow(GameInstance updated)
    {
        var index = -1;
        for (var i = 0; i < Instances.Count; i++)
            if (Instances[i].Id == updated.Id) { index = i; break; }
        if (index >= 0) Instances[index] = updated;
        if (SelectedInstance?.Id == updated.Id) SelectedInstance = updated;
    }

    /// <summary>Round-77: pre-launch mod check. Returns false when the user chose
    /// not to launch. Runs on a worker thread; the dialog is shown on the UI.</summary>
    private async Task<bool> RunPreflightAsync(GameInstance instance, CancellationToken ct)
    {
        if (!LauncherConfig.Instance.CheckModsBeforeLaunch) return true;
        if (instance.LoaderType == ModLoaderType.Vanilla) return true;

        ModPreflight.Report report;
        try
        {
            report = await Task.Run(() =>
                ModPreflight.Scan(instance.Directory, instance.LoaderType,
                    line => Dispatch(() => AddLog(line))), ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Instance.Warn("Preflight", "模组预检失败（忽略，继续启动）: " + ex.Message);
            return true;
        }

        if (!report.HasIssues) return true;

        Logger.Instance.Info("Preflight", $"{instance.Name}: {report.Headline}（共 {report.IssueCount} 项）");
        var dialog = new PreflightWindow(report, instance) { Owner = Application.Current.MainWindow };
        dialog.ShowDialog();
        if (dialog.Choice == PreflightChoice.Cancel)
        {
            Status = "已取消启动（启动前检查发现模组问题）";
            return false;
        }
        if (dialog.Choice == PreflightChoice.InstallFabricApi)
        {
            Status = "正在安装 Fabric API 前置…";
            await EnsureFabricApiAsync(instance, ct, "启动前检查（缺少 Fabric API）").ConfigureAwait(true);
        }
        return true;
    }

    private void CancelDownloadJob(DownloadJob? job)
    {
        if (job is not { CanCancel: true }) return;
        _jobManager.Cancel(job);
        RefreshDownloadAggregate(force: true);
    }

    private void ClearFinishedDownloads()
    {
        var removed = _jobManager.ClearFinished();
        RefreshDownloadAggregate(force: true);
        if (removed > 0) Status = $"已清理 {removed} 个已结束任务";
    }

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
        LoadSettingsFromConfig();
    }

    /// <summary>Round-90: reload only the settings fields from config. Split out of
    /// <see cref="LoadAll"/> so "放弃修改" can restore the page without reloading
    /// accounts/instances (that would duplicate the observable collections).</summary>
    private void LoadSettingsFromConfig()
    {
        var c = LauncherConfig.Instance;
        MemoryMb = c.MaxMemoryMb;
        // Round-83: the setter above wins over whatever auto mode displayed, so
        // recompute AFTER the stored value is in place.
        RefreshMemoryAdvice();
        JavaPath = c.JavaPath ?? "";
        DownloadPolicyText = DownloadRoutePolicy.DisplayName(c.RoutePolicy);
        ProxyUrl = c.ProxyUrl ?? "";
        MicrosoftClientIdText = c.MicrosoftClientId ?? "";
        AutoInstallFabricApi = c.AutoInstallFabricApi;
        CheckModsBeforeLaunch = c.CheckModsBeforeLaunch;
        UpdateModeText = UpdatePolicy.DisplayText(c.UpdateMode);
        UpdateFeedUrlText = c.UpdateFeedUrl ?? "";
        CurseForgeApiKeyText = c.CurseForgeApiKey ?? "";
        UpdateStatusText = $"当前第 {BuildInfo.CurrentRound} 轮 · 更新方式：{UpdateModeText}";
        WindowW = c.WindowWidth; WindowH = c.WindowHeight;
        ExtraJvm = c.ExtraJvmArgs;
        RootDirectoryText = c.RootDirectory;
        CloseActionText = CloseBehaviorPolicy.DisplayText(c.CloseAction);
        TrayEnabled = c.MinimizeToTrayEnabled;
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
    public BulkObservableCollection<JavaInfoVM> JavaList { get; } = new();

    // ---- 实例级 Java（round-62）----
    /// <summary>Guards the TwoWay ComboBox binding: clearing/rebuilding the
    /// choices momentarily sets SelectedValue to null, which must NOT be
    /// written back as "auto" and silently erase the user's saved Java.</summary>
    private bool _rebuildingJavaChoices;

    /// <summary>"Auto" plus every detected runtime. Rebuilt when Java is
    /// re-scanned (including right after a one-click Java 21 install).</summary>
    public ObservableCollection<JavaChoiceVM> InstanceJavaChoices { get; } = new();

    /// <summary>Persisted per-instance java.exe path; empty = auto. Written
    /// through InstanceManager.Update — the one legal metadata write entry.</summary>
    public string SelectedInstanceJavaPath
    {
        get => SelectedInstance?.JavaPath ?? "";
        set
        {
            if (_rebuildingJavaChoices) return;
            var inst = SelectedInstance;
            if (inst == null) return;
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value;
            if (string.Equals(inst.JavaPath, normalized, StringComparison.OrdinalIgnoreCase)) return;
            inst.JavaPath = normalized;
            _svc.Instances.Update(inst);
            OnPropertyChanged(nameof(SelectedInstanceJavaPath));
            OnPropertyChanged(nameof(InstanceJavaHint));
            Status = normalized == null
                ? $"「{inst.Name}」已改为自动选择 Java（推荐 {JavaRequirement.RecommendMajor(inst.MinecraftVersion)}）"
                : $"「{inst.Name}」已单独指定 Java：{normalized}";
        }
    }

    public string InstanceJavaHint
    {
        get
        {
            var inst = SelectedInstance;
            if (inst == null) return "选择左侧实例后，可为它单独指定 Java。";
            var current = string.IsNullOrWhiteSpace(inst.JavaPath)
                ? "自动选择（跟随全局设置与游戏版本需求）"
                : Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(inst.JavaPath)) ?? inst.JavaPath);
            return $"当前：{current}\n{JavaRequirement.Describe(inst.MinecraftVersion)}";
        }
    }

    private void RefreshInstanceJavaChoices()
    {
        var selected = SelectedInstanceJavaPath;
        _rebuildingJavaChoices = true;
        try
        {
            InstanceJavaChoices.Clear();
            InstanceJavaChoices.Add(new JavaChoiceVM("自动选择（按游戏版本）", ""));
            foreach (var j in JavaList)
                InstanceJavaChoices.Add(new JavaChoiceVM($"Java {j.Major} · {j.Version} · {j.Vendor}", j.Path));
            // A previously chosen custom path that is no longer detected must
            // stay visible, otherwise the ComboBox would silently show "auto".
            if (!string.IsNullOrWhiteSpace(selected) && InstanceJavaChoices.All(c => c.Path != selected))
                InstanceJavaChoices.Add(new JavaChoiceVM("自定义路径：" + selected, selected));
        }
        finally { _rebuildingJavaChoices = false; }
        OnPropertyChanged(nameof(SelectedInstanceJavaPath));
        OnPropertyChanged(nameof(InstanceJavaHint));
    }

    // ---- One-click Java 21 (round-59) ----
    private readonly JavaRuntimeService _javaRuntime = new();
    private CancellationTokenSource? _javaCts;

    private bool _isInstallingJava;
    public bool IsInstallingJava { get => _isInstallingJava; set => SetProperty(ref _isInstallingJava, value); }

    private double _javaInstallProgress;
    public double JavaInstallProgress { get => _javaInstallProgress; set => SetProperty(ref _javaInstallProgress, value); }

    private string _javaInstallStatus = "";
    public string JavaInstallStatus { get => _javaInstallStatus; set => SetProperty(ref _javaInstallStatus, value); }

    /// <summary>One click: Adoptium API → background download (NetRouting +
    /// DownloadEngine) → SHA-256 verify → silent extract into our own java/
    /// dir → persist as the launcher's default Java. Idempotent, no system
    /// changes, every failure surfaces as a clear status message.
    /// Round-78: the crash card may ask for 17 (or whatever the log demanded),
    /// so the major is a parameter; the 21 button keeps its own entry point.</summary>
    public Task InstallJava21Async() => InstallJavaMajorAsync(JavaRuntimeService.DefaultMajor);

    public async Task InstallJavaMajorAsync(int major)
    {
        if (major < 8) major = JavaRuntimeService.DefaultMajor;
        if (IsInstallingJava) return;
        IsInstallingJava = true;
        JavaInstallProgress = 0;
        JavaInstallStatus = $"正在获取 Java {major} 版本信息...";
        Status = JavaInstallStatus;
        _javaCts = new CancellationTokenSource();
        try
        {
            JavaPackage? pkg = null;
            try { pkg = await _javaRuntime.ResolveAsync(major, _javaCts.Token).ConfigureAwait(true); }
            catch (Exception ex)
            {
                // API 暂时不可达：本地已装过就直接复用，不打扰用户。
                var offline = _javaRuntime.FindAlreadyInstalled();
                if (offline != null)
                {
                    ApplyInstalledJava(offline, true, major);
                    JavaInstallStatus = "更新源暂时不可达，已把本机已安装的 Java 设为默认";
                    return;
                }
                throw new IOException("无法连接 Adoptium 更新源：" + ex.Message, ex);
            }
            if (pkg == null)
            {
                JavaInstallStatus = $"未找到适用于 Windows x64 的 Java {major} 安装包";
                Status = JavaInstallStatus;
                return;
            }

            JavaInstallStatus = $"准备下载 {pkg.ReleaseName}...";
            var result = await _javaRuntime.InstallAsync(pkg, p => Dispatch(() =>
            {
                var total = p.TotalBytes > 0 ? p.TotalBytes : pkg.Size;
                var pct = total > 0 ? Math.Clamp(p.DownloadedBytes * 100.0 / total, 0, 100) : 0;
                JavaInstallProgress = pct;
                JavaInstallStatus = p.Message ?? $"正在下载 Java {pkg.Major} {pct:0}%";
            }), _javaCts.Token).ConfigureAwait(true);

            JavaInstallProgress = 100;
            ApplyInstalledJava(result.JavaExecutable, result.AlreadyInstalled, pkg.Major);
        }
        catch (OperationCanceledException)
        {
            JavaInstallStatus = "已取消 Java 安装";
            Status = JavaInstallStatus;
        }
        catch (Exception ex)
        {
            JavaInstallStatus = "Java 安装失败: " + ex.Message;
            Status = JavaInstallStatus;
            AddLog("[Java] " + JavaInstallStatus);
        }
        finally
        {
            _javaCts = null;
            IsInstallingJava = false;
        }
    }

    private void ApplyInstalledJava(string javaExe, bool alreadyInstalled, int major = 21)
    {
        JavaPath = javaExe;
        var c = LauncherConfig.Instance;
        c.JavaPath = javaExe;
        c.Save();
        _ = LoadJavaListAsync();
        JavaInstallStatus = alreadyInstalled
            ? $"Java {major} 已安装，已设为启动器默认 Java"
            : $"Java {major} 安装完成，已设为启动器默认 Java";
        Status = JavaInstallStatus;
        ShowToast(JavaInstallStatus);
    }

    // ---- 崩溃诊断卡（第 60 轮起；第 78 轮升级为「诊断 + 修复」） ----
    public const string HelpUrl = "https://github.com/adagarm123/AuroraLauncher/issues";
    /// <summary>GPU vendor driver page. NVIDIA is the most common case; the
    /// status line tells AMD / Intel users where to go instead.</summary>
    public const string GpuDriverUrl = "https://www.nvidia.cn/geforce/drivers/";
    /// <summary>For Windows Update-style driver search users.</summary>
    public const string GpuDriverHint = "已打开 NVIDIA 驱动下载页；AMD / Intel 显卡请到对应官网的「驱动下载」，或在 Windows 更新里检查可选驱动";

    private CrashReportVM? _lastCrashReport;
    public CrashReportVM? LastCrashReport
    {
        get => _lastCrashReport;
        private set
        {
            if (SetProperty(ref _lastCrashReport, value)) OnPropertyChanged(nameof(HasCrashReport));
        }
    }

    public bool HasCrashReport => LastCrashReport != null;

    /// <summary>Context captured on the UI thread before the background
    /// analysis (memory / Java census / loader) — the engine itself is pure.</summary>
    private CrashContext? _lastCrashContext;

    /// <summary>Where the last diagnostics bundle landed, so "发送给作者" can
    /// point the user at a real file instead of asking them to export again.</summary>
    private string? _lastBundlePath;

    /// <summary>Repairs already applied for this crash, quoted in the mail body.</summary>
    private readonly List<string> _crashFixAttempts = new();

    private void DismissCrashReport() => LastCrashReport = null;

    private void OpenHelpLink()
    {
        OpenUrl(HelpUrl);
        Status = "已打开求助页面（附上诊断包会更快得到帮助）";
    }

    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Status = "无法打开链接: " + ex.Message; }
    }

    /// <summary>Diagnoses a crash and fills the card. Round-78: the log scan
    /// (regex over up to 600 lines × 25 rules) runs on a BACKGROUND thread —
    /// the game just died and the window must stay responsive. Everything the
    /// engine needs is snapshotted on the UI thread first.</summary>
    private async Task ShowCrashCardAsync(int exit, string instanceName)
    {
        var snapshot = Logs.ToArray();
        var inst = SelectedInstance;
        var ctx = BuildCrashContext(exit, instanceName, inst);
        _lastCrashContext = ctx;
        _crashFixAttempts.Clear();

        CrashDiagnosis diagnosis;
        try
        {
            var text = string.Join("\n", snapshot);
            diagnosis = await Task.Run(() => CrashResolutionEngine.Diagnose(text, ctx)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AddLog("[Crash] 诊断过程出错（不影响手动排查）: " + ex.Message);
            diagnosis = new CrashDiagnosis(Array.Empty<CrashSolution>(), "error");
        }

        var vm = new CrashReportVM(exit, instanceName, diagnosis.Signature);
        var i = 1;
        foreach (var s in diagnosis.Solutions)
            vm.Solutions.Add(new CrashSolutionVM(s, i++));

        LastCrashReport = vm;
        ShowErrorsOnly = true;
        Navigate("Logs");
        Status = diagnosis.HasSolutions
            ? $"游戏崩溃：匹配到 {diagnosis.Solutions.Count} 个原因，其中 {vm.AutoFixCount} 条可一键修复"
            : "游戏崩溃：未匹配到预置规则，可导出诊断包发给作者";
        AddLog($"[Crash] 游戏异常退出（代码 {exit}），命中 {diagnosis.Solutions.Count} 条规则" +
               $"，指纹 {diagnosis.Signature}；卡片已显示在日志页。");
        ShowToast(diagnosis.HasSolutions ? "检测到游戏崩溃，日志页已生成诊断与修复建议" : "检测到游戏崩溃，日志页可导出诊断包");
    }

    /// <summary>Everything the diagnosis needs, read on the UI thread.</summary>
    private CrashContext BuildCrashContext(int exit, string instanceName, GameInstance? inst)
    {
        var physical = 0;
        try { physical = (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024); } catch { }

        var majors = new List<int>();
        try { foreach (var j in JavaList) if (j.Major > 0) majors.Add(j.Major); } catch { }

        // The Java this instance will actually run with: instance override wins.
        int? currentMajor = null;
        try
        {
            var path = !string.IsNullOrWhiteSpace(inst?.JavaPath) ? inst!.JavaPath : JavaPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var match = JavaList.FirstOrDefault(j =>
                    string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase));
                if (match != null) currentMajor = match.Major;
            }
            currentMajor ??= majors.Count > 0 ? majors.OrderByDescending(m => m).First() : null;
        }
        catch { }

        return new CrashContext
        {
            InstanceName = instanceName,
            ExitCode = exit,
            // Round-83: diagnose against the heap the game really got, not the
            // stored number that auto mode may have overridden.
            MaxMemoryMb = EffectiveMemoryMb(),
            TotalPhysicalMb = physical,
            Loader = inst?.LoaderType ?? ModLoaderType.Vanilla,
            MinecraftVersion = inst?.MinecraftVersion ?? "",
            InstalledJavaMajors = majors,
            CurrentJavaMajor = currentMajor
        };
    }

    /// <summary>
    /// Runs ONE repair from the crash card. Safety contract (guarded by the
    /// self-test): this method may only change launcher settings (memory / Java
    /// path), download missing files into the instance, or open a folder / page.
    /// It must never delete or move user data, and never touch the system
    /// environment. Everything runs async — the crash card stays interactive.
    /// </summary>
    private async Task ApplyCrashFixAsync(CrashSolutionVM? item)
    {
        if (item == null || !item.HasAction || !item.CanApply) return;
        var s = item.Solution;
        var inst = SelectedInstance;
        AddLog($"[CrashFix] {s.RuleId} → {s.Action}（{CrashFixSafety.Describe(s.Action)}）");
        try
        {
            switch (s.Action)
            {
                case FixAction.RaiseMemory:
                case FixAction.LowerMemory:
                {
                    if (!int.TryParse(s.Payload, out var target) || target < 512)
                    {
                        Status = "内存参数无效，请到设置页手动调整";
                        return;
                    }
                    var cfg = LauncherConfig.Instance;
                    // Round-83: with auto mode on, the effective heap is the
                    // recommendation, not the stored number — compare and report
                    // against what the game actually got.
                    var before = cfg.AutoMemory ? EffectiveMemoryMb() : cfg.MaxMemoryMb;
                    if (before == target && !cfg.AutoMemory)
                    {
                        item.MarkApplied($"当前已经是 {before} MB，无需修改");
                        return;
                    }
                    // Auto would recompute over this number on the next launch,
                    // turning the repair into a no-op. Pin the value instead.
                    if (cfg.AutoMemory)
                    {
                        cfg.AutoMemory = false;
                        AutoMemory = false;
                    }
                    cfg.MaxMemoryMb = target;
                    cfg.MinMemoryMb = Math.Max(512, target / 2);
                    cfg.Save();
                    MemoryMb = target; // 设置页同步，避免下次保存把旧值写回去
                    OnPropertyChanged(nameof(MemoryMb));
                    RefreshMemoryAdvice();
                    var note = $"已把最大内存从 {before} MB 调到 {target} MB（同时切换为手动分配），重新启动游戏后生效（可在设置页改回）";
                    _crashFixAttempts.Add($"最大内存 {before} MB → {target} MB");
                    item.MarkApplied(note);
                    Status = note;
                    ShowToast(note);
                    break;
                }

                case FixAction.InstallJava:
                {
                    var major = int.TryParse(s.Payload, out var m) ? m : JavaRuntimeService.DefaultMajor;
                    Navigate("Settings");
                    Status = $"正在安装 Java {major}…（进度见设置页）";
                    await InstallJavaMajorAsync(major).ConfigureAwait(true);
                    var ok = JavaInstallStatus.Contains("安装完成") || JavaInstallStatus.Contains("已安装");
                    if (ok)
                    {
                        AdoptJavaForSelectedInstance(JavaPath);
                        _crashFixAttempts.Add($"安装并启用 Java {major}");
                        item.MarkApplied(JavaInstallStatus + "；重新启动游戏即可");
                    }
                    else
                    {
                        item.MarkApplied("安装没有完成：" + JavaInstallStatus);
                    }
                    break;
                }

                case FixAction.UseInstalledJava:
                {
                    var required = int.TryParse(s.Payload, out var need) ? need : JavaRuntimeService.DefaultMajor;
                    var pick = JavaList.Where(j => j.Major >= required)
                        .OrderBy(j => j.Major).FirstOrDefault();
                    if (pick == null)
                    {
                        Status = $"本机没有 Java {required} 或更高版本，请用「一键安装」";
                        return;
                    }
                    var cfg = LauncherConfig.Instance;
                    cfg.JavaPath = pick.Path;
                    cfg.Save();
                    JavaPath = pick.Path;
                    AdoptJavaForSelectedInstance(pick.Path);
                    _ = LoadJavaListAsync();
                    var note = $"已切换到 Java {pick.Major}（{pick.Version}），重新启动游戏即可";
                    _crashFixAttempts.Add($"Java 切换到 {pick.Major}");
                    item.MarkApplied(note);
                    Status = note;
                    ShowToast(note);
                    break;
                }

                case FixAction.InstallFabricApi:
                {
                    if (inst == null) { Status = "请先选择一个实例"; return; }
                    if (inst.LoaderType != ModLoaderType.Fabric)
                    {
                        Status = "当前实例不是 Fabric，Fabric API 不适用";
                        return;
                    }
                    var installed = await EnsureFabricApiAsync(inst, CancellationToken.None, "崩溃诊断修复")
                        .ConfigureAwait(true);
                    item.MarkApplied(installed ? "Fabric API 已下载到 mods 目录，重新启动游戏即可"
                                               : "Fabric API 已在实例中或安装被取消");
                    _crashFixAttempts.Add(installed ? "安装 Fabric API" : "检查 Fabric API（已存在）");
                    break;
                }

                case FixAction.RepairFiles:
                {
                    Navigate("Downloads");
                    await RepairInstanceAsync().ConfigureAwait(true);
                    item.MarkApplied(Status ?? "已尝试补全游戏文件");
                    _crashFixAttempts.Add("校验并补全游戏文件");
                    break;
                }

                case FixAction.ScanMods:
                {
                    if (inst == null) { Status = "请先选择一个实例"; return; }
                    Status = "正在逐个读取模组元数据…";
                    var report = await Task.Run(() => ModPreflight.Scan(inst.Directory, inst.LoaderType))
                        .ConfigureAwait(true);
                    var lines = new List<string>();
                    if (report.Missing.Count > 0)
                        lines.Add("缺少前置：" + string.Join("、", report.Missing.Select(d => d.ModId).Distinct()));
                    if (report.Duplicates.Count > 0) lines.Add(string.Join("；", report.Duplicates));
                    if (report.Conflicts.Count > 0) lines.Add(string.Join("；", report.Conflicts));
                    if (report.WrongLoader.Count > 0) lines.Add(string.Join("；", report.WrongLoader));
                    var text = lines.Count == 0
                        ? $"扫描了 {report.ScannedJars} 个模组：没有发现缺失前置 / 冲突 / 重复，问题可能出在模组与游戏版本的匹配上"
                        : $"扫描了 {report.ScannedJars} 个模组：" + string.Join("；", lines);
                    item.MarkApplied(text);
                    Status = text;
                    AddLog("[CrashFix] " + text);
                    ShowToast("模组体检完成，结果见崩溃卡片");
                    break;
                }

                case FixAction.OpenModsFolder:
                    OpenFolder(inst == null ? null : Path.Combine(inst.Directory, "mods"));
                    item.MarkApplied("已打开 mods 文件夹：删掉重复/冲突的模组后重新启动");
                    break;

                case FixAction.OpenShaderFolder:
                    OpenFolder(inst == null ? null : Path.Combine(inst.Directory, "shaderpacks"));
                    item.MarkApplied("已打开 shaderpacks：先把光影包移出去再试一次");
                    break;

                case FixAction.OpenSavesFolder:
                    OpenFolder(inst == null ? null : Path.Combine(inst.Directory, "saves"));
                    item.MarkApplied("已打开 saves：先把出问题的存档移出去");
                    break;

                case FixAction.OpenGameFolder:
                    OpenFolder(inst?.Directory);
                    item.MarkApplied("已打开实例目录");
                    break;

                case FixAction.OpenSettings:
                    Navigate("Settings");
                    item.MarkApplied("已切换到设置页");
                    break;

                case FixAction.OpenAccounts:
                    Navigate("Home");
                    Status = "请在首页重新登录微软账户，或改用离线账户";
                    item.MarkApplied("已回到首页账户区");
                    break;

                case FixAction.OpenGpuDriver:
                    OpenUrl(GpuDriverUrl);
                    Status = GpuDriverHint;
                    item.MarkApplied(GpuDriverHint);
                    break;

                case FixAction.OpenHelpPage:
                    OpenHelpLink();
                    item.MarkApplied("已打开求助页");
                    break;

                case FixAction.ExportBundle:
                    await ExportDiagnosticsAsync().ConfigureAwait(true);
                    item.MarkApplied(_lastBundlePath == null ? "未导出" : "诊断包：" + _lastBundlePath);
                    break;

                case FixAction.MailAuthor:
                    await SendCrashReportAsync().ConfigureAwait(true);
                    item.MarkApplied("已打开邮件窗口，请把诊断包拖进附件后发送");
                    break;

                default:
                    Status = "这条建议需要你手动处理";
                    break;
            }
        }
        catch (Exception ex)
        {
            Status = "修复失败：" + ex.Message;
            AddLog("[CrashFix] 失败: " + ex);
            item.MarkApplied("修复失败：" + ex.Message);
        }
    }

    /// <summary>If the instance carries its own Java override, switching the
    /// launcher default alone would NOT change what the game actually runs —
    /// so the repair updates the override too (still launcher config only).</summary>
    private void AdoptJavaForSelectedInstance(string javaExe)
    {
        var inst = SelectedInstance;
        if (inst == null || string.IsNullOrWhiteSpace(inst.JavaPath)) return;
        inst.JavaPath = javaExe;
        _svc.Instances.Update(inst);
        AddLog($"[CrashFix] 实例 {inst.Name} 的 Java 覆盖已同步为 {javaExe}");
    }

    /// <summary>Hands a fully prepared bug report to the user's own mail
    /// client. Nothing is sent automatically: the user reviews it and presses
    /// Send — the launcher never holds mail credentials.</summary>
    public async Task SendCrashReportAsync()
    {
        var vm = LastCrashReport;
        if (vm == null) { Status = "没有可发送的崩溃报告"; return; }

        if (string.IsNullOrEmpty(_lastBundlePath) || !File.Exists(_lastBundlePath))
        {
            Status = "正在生成诊断包（已脱敏）…";
            _lastBundlePath = await ExportCrashBundleAsync().ConfigureAwait(true);
        }

        var ctx = new SupportReport.Context
        {
            InstanceName = vm.InstanceName,
            ExitCode = vm.ExitCode,
            MinecraftVersion = SelectedInstance?.MinecraftVersion ?? "",
            Loader = SelectedInstance == null
                ? ""
                : SelectedInstance.LoaderType + (string.IsNullOrWhiteSpace(SelectedInstance.LoaderVersion)
                    ? "" : " " + SelectedInstance.LoaderVersion),
            BundlePath = _lastBundlePath,
            Signature = vm.Signature,
            AttemptedFixes = _crashFixAttempts.ToArray(),
            OsDescription = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            JavaText = JavaList.Count == 0
                ? "未探测到"
                : string.Join("，", JavaList.Select(j => $"Java {j.Major}"))
        };

        var solutions = vm.Solutions.Select(s => s.Solution).ToList();
        var mailto = SupportReport.BuildMailto(solutions, ctx);
        try
        {
            Process.Start(new ProcessStartInfo(mailto) { UseShellExecute = true });
            Status = $"已打开邮件窗口，收件人 {SupportReport.AuthorEmail}" +
                     (_lastBundlePath == null ? "" : $"；请把诊断包拖进附件：{_lastBundlePath}");
            ShowToast("邮件已准备好，请附上诊断包后手动发送");
        }
        catch (Exception ex)
        {
            Status = $"无法唤起邮件客户端（{ex.Message}），可手动发到 {SupportReport.AuthorEmail}";
        }
    }

    /// <summary>Exports a bundle without a dialog (used by "发送给作者").</summary>
    private async Task<string?> ExportCrashBundleAsync()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AuroraLauncher");
            Directory.CreateDirectory(dir);
            var zip = Path.Combine(dir, $"aurora-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            var instances = _svc.Instances.LoadAll();
            await Task.Run(() => DiagnosticsExporter.ExportAsync(zip, LauncherConfig.Instance.RootDirectory,
                instances)).ConfigureAwait(true);
            _lastBundlePath = zip;
            Status = "诊断包已生成：" + zip;
            AddLog("[Crash] 诊断包已生成：" + zip);
            return zip;
        }
        catch (Exception ex)
        {
            Status = "诊断包生成失败：" + ex.Message;
            AddLog("[Crash] 诊断包生成失败: " + ex);
            return null;
        }
    }

    // ---- CurseForge browse (round-60) ----
    private readonly CurseForgeService _curseForge = new();
    public ObservableCollection<CurseForgeItemVM> CurseForgeResults { get; } = new();
    public string CurseForgeQuery { get; set; } = "";
    public string CurseForgeApiKeyText { get; set; } = "";

    private string _curseForgeStatus = "CurseForge 要求自备 API Key；没有 Key 也可以直接用网页搜索。";
    public string CurseForgeStatus { get => _curseForgeStatus; set => SetProperty(ref _curseForgeStatus, value); }

    private bool _isCurseForgeBusy;
    public bool IsCurseForgeBusy { get => _isCurseForgeBusy; set => SetProperty(ref _isCurseForgeBusy, value); }

    public async Task SearchCurseForgeAsync()
    {
        if (IsCurseForgeBusy) return;
        IsCurseForgeBusy = true;
        CurseForgeStatus = "正在搜索 CurseForge…";
        try
        {
            var c = LauncherConfig.Instance;
            c.CurseForgeApiKey = string.IsNullOrWhiteSpace(CurseForgeApiKeyText) ? null : CurseForgeApiKeyText.Trim();
            c.Save();
            var list = await _curseForge.SearchAsync(CurseForgeQuery, c.CurseForgeApiKey ?? "").ConfigureAwait(true);
            Dispatch(() =>
            {
                CurseForgeResults.Clear();
                foreach (var m in list) CurseForgeResults.Add(new CurseForgeItemVM(m));
            });
            CurseForgeStatus = list.Count == 0
                ? "没有找到匹配项目，可换个关键词或用网页搜索"
                : $"找到 {list.Count} 个项目 · 点「前往下载页」在浏览器下载，再到「本地管理」导入";
        }
        catch (Exception ex) { CurseForgeStatus = "搜索失败：" + ex.Message; }
        finally { IsCurseForgeBusy = false; }
    }

    private void OpenCurseForgeItem(object? o)
        => OpenUrl(o is CurseForgeItemVM item
            ? CurseForgeService.BuildFilesPageUrl(item.Model)
            : CurseForgeService.WebBase);

    // ---- OptiFine one-click installer (round-60) ----
    private readonly OptiFineService _optiFine = new();
    public ObservableCollection<OptiFineItemVM> OptiFineBuilds { get; } = new();

    private OptiFineItemVM? _selectedOptiFineBuild;
    public OptiFineItemVM? SelectedOptiFineBuild
    {
        get => _selectedOptiFineBuild;
        set => SetProperty(ref _selectedOptiFineBuild, value);
    }

    private string _optiFineStatus = "从 OptiFine 官网获取版本列表；也可直接选择本地下载好的安装器。";
    public string OptiFineStatus { get => _optiFineStatus; set => SetProperty(ref _optiFineStatus, value); }

    private bool _isOptiFineBusy;
    public bool IsOptiFineBusy { get => _isOptiFineBusy; set => SetProperty(ref _isOptiFineBusy, value); }

    public async Task LoadOptiFineBuildsAsync()
    {
        if (IsOptiFineBusy || OptiFineBuilds.Count > 0) return;
        IsOptiFineBusy = true;
        OptiFineStatus = "正在获取 OptiFine 版本列表…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var html = await NetRouting.GetStringAsync(OptiFineService.WebUrl, null, timeout.Token,
                perRouteTimeoutSec: 8, rounds: 1, logTag: "OptiFine").ConfigureAwait(true);
            var builds = OptiFineService.ParseDownloadsPage(html);
            Dispatch(() =>
            {
                OptiFineBuilds.Clear();
                foreach (var b in builds) OptiFineBuilds.Add(new OptiFineItemVM(b));
                SelectedOptiFineBuild = OptiFineBuilds.FirstOrDefault();
            });
            OptiFineStatus = builds.Count == 0
                ? "官网未解析出版本（页面结构可能变化），可点「打开官网」或「选择本地安装器」"
                : $"找到 {builds.Count} 个版本 · 选一个后点「一键安装」";
        }
        catch (Exception ex)
        {
            OptiFineStatus = "获取失败：" + ex.Message + "（可改用「打开官网」或「选择本地安装器」）";
        }
        finally { IsOptiFineBusy = false; }
    }

    public async Task InstallOptiFineAsync()
    {
        if (SelectedOptiFineBuild is not { } item)
        {
            OptiFineStatus = "请先选择一个 OptiFine 版本";
            return;
        }
        if (IsOptiFineBusy) return;
        IsOptiFineBusy = true;
        try
        {
            OptiFineStatus = "正在下载 OptiFine 官方安装器…";
            var cache = Path.Combine(LauncherConfig.Instance.RootDirectory, "cache", "optifine");
            var jar = await _optiFine.DownloadInstallerAsync(item.Build, cache).ConfigureAwait(true);
            await LaunchInstallerAsync(jar, fromWebsite: false).ConfigureAwait(true);
        }
        catch (Exception ex) { OptiFineStatus = "安装失败：" + ex.Message; }
        finally { IsOptiFineBusy = false; }
    }

    public async Task PickLocalOptiFineAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 OptiFine 安装器",
            Filter = "OptiFine 安装器 (*.jar)|*.jar"
        };
        if (dlg.ShowDialog() != true) return;
        if (!OptiFineService.IsValidInstaller(dlg.FileName))
        {
            OptiFineStatus = "所选文件不是有效的 OptiFine 安装器（.jar）";
            return;
        }
        await LaunchInstallerAsync(dlg.FileName, fromWebsite: true).ConfigureAwait(true);
    }

    private async Task LaunchInstallerAsync(string jarPath, bool fromWebsite)
    {
        var java = (await _svc.FindJavaAsync().ConfigureAwait(true))
            .OrderByDescending(j => j.Major).FirstOrDefault();
        if (java == null)
            throw new InvalidOperationException("未检测到 Java，请先在 设置 → Java 环境 一键安装 Java 21");
        if (!OptiFineService.LaunchInstaller(java.JavaExecutable, jarPath))
            throw new InvalidOperationException("安装器启动失败，请检查 Java 是否可用");
        var root = LauncherConfig.Instance.RootDirectory;
        try { System.Windows.Clipboard.SetText(root); } catch { /* clipboard busy */ }
        OptiFineStatus = (fromWebsite ? "本地安装器" : "官方安装器") +
            $" 已打开：请点 Install，并把安装目录设为 {root}（路径已复制）。完成后用「实例管理 → 导入 .minecraft」导入。";
        ShowToast("OptiFine 官方安装器已打开，按提示点 Install 即可");
        AddLog("[OptiFine] " + OptiFineStatus);
    }

    private CoalescingSingleFlight<string>? _javaScanFlight;

    /// <summary>Same coalescing contract as loader cards (round-64): a refresh
    /// requested during a scan (e.g. right after one-click Java 21 install)
    /// collapses into one extra pass — never two concurrent sweeps of
    /// `java -version` processes.</summary>
    public Task LoadJavaListAsync()
    {
        _javaScanFlight ??= new CoalescingSingleFlight<string>(_ => LoadJavaListCoreAsync());
        return _javaScanFlight.RequestAsync("scan");
    }

    private async Task LoadJavaListCoreAsync()
    {
        try
        {
            var all = await _svc.FindJavaAsync().ConfigureAwait(true);
            var items = all.DistinctBy(j => j.JavaExecutable)
                .Select(j => new JavaInfoVM(j.Version, j.Vendor, j.JavaExecutable, j.Major)).ToList();
            Dispatch(() =>
            {
                JavaList.Replace(items);
                RefreshInstanceJavaChoices();
            });
        }
        catch (Exception ex) { Dispatch(() => Status = "Java 检测失败: " + ex.Message); }
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

    private bool _hasNavigated;

    /// <summary>Round-65: navigation changes ONLY CurrentPageKey. MainWindow
    /// listens for it, lazily creates the page once and flips Visibility —
    /// no visual-tree rebuild, no ContentControl.Content swap, no Loaded
    /// storm. Repeat clicks on the active page are ignored entirely.</summary>
    public void Navigate(string? key)
    {
        var target = NavigationPolicy.Normalize(key);
        if (!NavigationPolicy.ShouldNavigate(CurrentPageKey, target, _hasNavigated)) return;
        // Round-90: leaving the settings page with unsaved edits would silently
        // throw them away. Ask first; "取消" keeps the user where they are.
        if (!ConfirmLeaveSettings(target)) return;
        _hasNavigated = true;
        CurrentPageKey = target;
        // A pending loader-card debounce belongs to the Download page only.
        // Pages no longer raise Loaded on re-entry, so re-arm it explicitly.
        if (target != "Download") _loaderCardsDebounce?.Stop();
        else
        {
            EnsureLoaderCardsRefresh();
            // Round-85: measuring costs one HEAD per host and is cached for
            // 5 minutes — fire-and-forget here so the numbers under the
            // dropdown are current, never on the download path itself.
            EnsureNetworkProbe();
        }
        // Round-69: the Logs page is the only live feed; it was frozen while
        // hidden, so publish the backlog once the page is on screen.
        if (target == "Logs") Dispatch(RefreshLogsOnShow);
        // Round-87: the residue banner belongs to this page, so refresh the scan
        // when it comes into view (cheap; the walk is cached by the OS).
        if (target == "Downloads") ScanDownloadResidue();
        // Visiting the changelog page marks the "已更新" toast as read.
        if (target == "Changelog" && LauncherConfig.Instance.ChangelogSeenRound < HelpContent.LatestChangelogRound)
        {
            LauncherConfig.Instance.ChangelogSeenRound = HelpContent.LatestChangelogRound;
            LauncherConfig.Instance.Save();
        }
    }

    private int _memoryMb;
    /// <summary>Heap shown in settings. Round-83: raising the value recomputes the
    /// over-limit warning immediately — typing 99999 must turn red WHILE typing,
    /// not after saving.</summary>
    public int MemoryMb
    {
        get => _memoryMb;
        set
        {
            if (!SetProperty(ref _memoryMb, value)) return;
            RefreshMemoryWarning();
        }
    }
    public string JavaPath { get; set; } = "";
    private string _downloadPolicyText = DownloadRoutePolicy.DisplayName(DownloadPolicy.Auto);
    /// <summary>Round-85: three-state download policy (auto / official / mirror).
    /// Changing it updates the explanation line immediately — "强制官方" and
    /// "自动" behave very differently when the official host is blackholed.</summary>
    public string DownloadPolicyText
    {
        get => _downloadPolicyText;
        set
        {
            if (!SetProperty(ref _downloadPolicyText, value)) return;
            LauncherConfig.Instance.RoutePolicy = DownloadRoutePolicy.Parse(value);
            RefreshNetworkNote();
        }
    }

    /// <summary>Legacy binding name kept so older XAML/automation keeps working.</summary>
    public string DownloadSourceText
    {
        get => DownloadPolicyText;
        set => DownloadPolicyText = value;
    }
    public string ProxyUrl { get; set; } = "";
    public string MicrosoftClientIdText { get; set; } = "";
    /// <summary>安装 Fabric 实例时自动安装适配的 Fabric API 前置（默认开启）。</summary>
    public bool AutoInstallFabricApi { get; set; } = true;

    /// <summary>Round-77: 启动前检查模组依赖 / 冲突（默认开启）。</summary>
    public bool CheckModsBeforeLaunch { get; set; } = true;

    public int WindowW { get; set; } = 854;
    public int WindowH { get; set; } = 480;
    public string ExtraJvm { get; set; } = "";
    public string RootDirectoryText { get; set; } = "";

    // ---- round-74: portable mode + tray / close behaviour ----
    public ObservableCollection<string> CloseActionOptions { get; } = new()
    {
        CloseBehaviorPolicy.DisplayText(CloseAction.Ask),
        CloseBehaviorPolicy.DisplayText(CloseAction.Exit),
        CloseBehaviorPolicy.DisplayText(CloseAction.MinimizeToTray)
    };

    /// <summary>Bound to the combo box; ApplySettings() persists it.</summary>
    public string CloseActionText { get => _closeActionText; set => SetProperty(ref _closeActionText, value); }
    private string _closeActionText = CloseBehaviorPolicy.DisplayText(CloseAction.Ask);

    /// <summary>Tray icon on/off. Turning it OFF also disables "隐藏到托盘" (the
    /// close policy falls back to ask/exit), which is what the user expects.</summary>
    public bool TrayEnabled { get => _trayEnabled; set { if (SetProperty(ref _trayEnabled, value)) OnPropertyChanged(nameof(TrayNote)); } }
    private bool _trayEnabled = true;

    public string TrayNote => TrayEnabled
        ? "托盘图标已启用：关闭窗口时可选择最小化到托盘，下载继续在后台进行（需重启启动器生效）。"
        : "托盘图标已关闭：关闭窗口将直接退出（有下载时仍会先询问）。需重启启动器生效。";

    public bool IsPortableMode => LauncherConfig.Instance.IsPortableMode;
    public bool IsRootDirectoryEditable => !IsPortableMode;
    public string PortableModeText => PortableModePolicy.Describe(
        IsPortableMode, LauncherConfig.Instance.RootDirectory, Environment.ProcessPath);

    /// <summary>Creates portable.txt beside the EXE. Data is NOT migrated — the
    /// dialog says so explicitly and the change applies on the next start.</summary>
    public void EnablePortableMode()
    {
        if (IsPortableMode) { Status = "当前已经是便携模式"; return; }
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) { Status = "无法定位程序路径"; return; }

        var answer = System.Windows.MessageBox.Show(
            Application.Current.MainWindow,
            "将在程序所在目录创建 portable.txt。\n\n" +
            "下次启动后，配置、实例、缓存都会保存到程序目录，不再写入系统用户目录（适合 U 盘 / 移动硬盘）。\n\n" +
            $"当前已有的数据仍保留在原位置：{LauncherConfig.Instance.RootDirectory}\n" +
            "启动器不会自动移动这些文件——便携模式只改变以后的写入位置。\n\n确定创建吗？",
            "启用便携模式", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            var path = PortableModePolicy.CreateMarker(exe, "由设置页创建");
            Logger.Instance.Info("Config", "已创建便携模式标记: " + path);
            Status = "已创建 portable.txt，重启后进入便携模式";
            ShowToast("已创建 portable.txt，重启启动器后生效");
            OnPropertyChanged(nameof(PortableModeText));
        }
        catch (Exception ex)
        {
            Status = "创建 portable.txt 失败: " + ex.Message;
            System.Windows.MessageBox.Show(Application.Current.MainWindow,
                "无法创建 portable.txt：\n" + ex.Message + "\n\n如果启动器放在 Program Files 等受保护目录，请换到普通文件夹（如桌面或 U 盘）。",
                "启用便携模式失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private int _versionsRunning;

    /// <summary>Single-flight (round-63): page Loaded events, the refresh
    /// button and filter changes can all ask for the manifest. Only ONE fetch
    /// may be in flight — the rest return immediately instead of piling up
    /// continuations on the UI thread.</summary>
    public async Task LoadVersionsAsync()
    {
        if (Interlocked.Exchange(ref _versionsRunning, 1) != 0) return;
        try
        {
            IsBusy = true;
            var manifest = await _svc.GetManifestAsync();
            var list = VersionFilter == "all"
                ? manifest.Versions
                : manifest.Versions.Where(v => v.Type == VersionFilter).ToList();
            if (!string.IsNullOrWhiteSpace(VersionSearchText))
                list = list.Where(v => v.Id.Contains(VersionSearchText, StringComparison.OrdinalIgnoreCase)).ToList();
            // ONE Reset notification instead of Clear + 103 Add events.
            // Reset drops ListBox.SelectedItem, and Versions DOES have
            // selection semantics — restore it by stable id (round-64).
            var keepId = SelectedVersion?.Id;
            var fromCache = _svc.LastManifestFromCache;
            Dispatch(() =>
            {
                Versions.Replace(list);
                SelectedVersion = SelectionPolicy.RestoreByKey(list, v => v.Id, keepId);
                // Round-71: say WHY the list may be stale instead of pretending
                // everything is fine (or showing an empty page on failure).
                Status = fromCache
                    ? $"网络不可用，已加载本地缓存的 {list.Count} 个版本（安装/启动仍需联网）"
                    : $"已加载 {list.Count} 个版本";
            });
        }
        catch (Exception ex)
        {
            Dispatch(() => Status = "加载版本失败: " + ex.Message +
                              "（已同时尝试官方与镜像线路，仍无法取到版本清单；请检查网络或代理设置）");
        }
        finally { IsBusy = false; Volatile.Write(ref _versionsRunning, 0); }
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
        // Round-85: computed live from the probe + policy, not from a persisted
        // string — a stored sentence goes stale the moment the network changes.
        var policy = LauncherConfig.Instance.RoutePolicy;
        var latest = NetworkProbe.Latest;
        var text = DownloadRoutePolicy.Describe(policy, NetworkProbe.Preference,
            latest?.OfficialLatency, latest?.MirrorLatency, latest != null);
        Dispatch(() => NetworkNoteDisplay = text);
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

    /// <summary>Round-72: stage flow chart that belongs to ONE job.</summary>
    private void InitStages(DownloadJob job, IEnumerable<(string Key, string Label)> stages, string title)
    {
        job.SetStages(stages);
        InstallTitle = title;
        InstallError = null;
        InstallSuccess = null;
    }

    /// <summary>Round-72: per-job stage callback (several jobs can run at once).</summary>
    private void OnInstallStage(DownloadJob job, string key)
    {
        job.EnterStage(key);
        var label = job.Stages.FirstOrDefault(st => st.Key == key)?.Label ?? key;
        job.SetStageText(label);
        Status = $"正在安装… {label}";
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
        // Round-82: one decision table for "can we launch at all" — the button
        // label, the redirect target and the message all come from it.
        var block = LaunchGuardPolicy.Decide(Instances.Count, SelectedInstance != null, SelectedAccount != null);
        if (block != LaunchGuardPolicy.Block.None)
        {
            var (message, page) = LaunchGuardPolicy.Describe(block);
            Status = message;
            AddLog($"[Launch] 拦截（{block}）：{message}");
            if (page != null) Navigate(page);
            if (block == LaunchGuardPolicy.Block.NoInstances) ShowToast("还没有实例：先选一个游戏版本安装");
            return;
        }
        if (IsBusy) { Status = "有任务正在进行中，请等待完成或取消后再试"; return; }

        // Round-77: these two outlive the try block because the playtime settlement
        // happens in `finally` (it must run for crashes and cancels too).
        GameInstance? instance = null;
        var playedAt = DateTime.MinValue;
        LaunchOutcome? runOutcome = null;
        try
        {
            IsBusy = true;
            instance = SelectedInstance;
            _cts = new CancellationTokenSource();

            // ---- 启动前自动扫描：文件不完整才补全并跳转下载页；完整则直接启动 ----
            var missing = _svc.DescribeMissing(instance);
            if (_svc.NeedsRepair(instance))
            {
                Dispatch(() => AddLog("[Repair] 缺少文件: " + missing));
                await RunRepairJobAsync(instance).ConfigureAwait(true);
                InstallSuccess = $"实例「{instance.Name}」文件已补全，正在启动…";
            }
            else
            {
                Status = $"{instance.Name} 文件完整，直接启动...";
            }

            // Round-92: 补全之后仍然不完整就不能装作没问题地启动。
            // 这是「下载了整合包却只玩到原版」的成因：加载器没装上、或整合包
            // 的模组一个都没下到，而旧代码只检查原版文件，于是判定"完整"直接放行。
            var gate = _svc.CheckReadiness(instance);
            if (gate.Blocks)
            {
                Dispatch(() => AddLog($"[Launch] {gate.Headline}：{gate.Detail}"));
                if (!ConfirmLaunchGate(gate, instance))
                {
                    Status = gate.Headline + "（未启动）";
                    return;
                }
            }

            // Round-92: instances created before we recorded the pack expectation
            // cannot be verified. If such an instance has a loader but no mods at
            // all, say so — silently starting "Forge with 0 mods" is exactly the
            // experience the user complained about.
            if (!instance.IsModpack && instance.LoaderType != ModLoaderType.Vanilla &&
                ModpackIntegrity.CountModJars(instance.Directory) == 0)
            {
                AddLog("[Launch] 提示：这个实例的 mods 文件夹是空的（0 个模组）。" +
                       "如果它是整合包，说明当初没装完 —— 请重新导入整合包。");
                ShowToast("这个实例没有任何模组：如果它是整合包，请重新导入");
            }

            // Round-77: 启动前模组预检（缺前置 / 冲突 / 重复 / 加载器不符）。
            // 100ms 级别的本地扫描，能挡掉最常见的"一启动就崩"。
            if (!await RunPreflightAsync(instance, _cts.Token).ConfigureAwait(true)) return;

            // Round-79: snapshot saves BEFORE the game can touch them. Awaited on
            // purpose — a backup that finishes after the game started protects
            // nothing. Size-capped by SaveBackupPolicy, so this is seconds at most.
            if (SaveBackupPolicy.ShouldRun(LauncherConfig.Instance.SaveBackup, BackupTrigger.BeforeLaunch))
                await BackupSavesAsync(instance, BackupTrigger.BeforeLaunch, interactive: false).ConfigureAwait(true);

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

            // Round-77: 计时从真正启动进程前开始；会话同时落盘 + 心跳，
            // 启动器被强杀时下次启动能按最后一次心跳结算。
            playedAt = DateTime.Now;
            _playtime.Begin(instance.Id);
            var launched = _svc.Instances.RecordLaunchStart(instance.Id);
            if (launched != null) Dispatch(() => RefreshInstanceRow(launched));

            var outcome = await _svc.LaunchWithStatsAsync(spec, line => Dispatch(() => AddLog(line)), CancellationToken.None, contextLine);
            var exit = outcome.ExitCode;
            runOutcome = outcome;
            if (exit == 0)
            {
                LastCrashReport = null;
                Status = $"游戏已正常退出: {SelectedInstance.Name}";
                Dispatch(() => System.Windows.MessageBox.Show(
                    Application.Current.MainWindow,
                    $"《Minecraft》已正常退出。\n\n实例：{SelectedInstance.Name}\n账户：{SelectedAccount.Username}",
                    "Aurora Launcher", MessageBoxButton.OK, MessageBoxImage.Information));
            }
            else
            {
                // Round-60: a friendly graphical card replaces the old
                // "go read the black console" warning dialog.
                // Round-78: the card now carries diagnosis + repair actions,
                // and the analysis runs off the UI thread.
                await ShowCrashCardAsync(exit, SelectedInstance?.Name ?? "未知实例");
            }
        }
        catch (OperationCanceledException) { Status = "已取消（未完成的临时文件已清理）"; }
        catch (Exception ex) { Status = ReportLaunchFailure(ex); }
        finally
        {
            // Round-77: 结束计时并写入实例元数据（无论正常退出、崩溃还是取消）。
            var span = _playtime.End();
            if (span > TimeSpan.Zero && instance != null && playedAt != DateTime.MinValue)
            {
                var updated = _svc.Instances.AddPlaytime(instance.Id, span, playedAt);
                if (updated != null) Dispatch(() => RefreshInstanceRow(updated));

                // Round-79: performance snapshot + post-exit save backup. Both are
                // strictly after the process is gone, so nothing can be locked.
                if (runOutcome != null)
                {
                    var stamp = _svc.Instances.RecordPerformance(instance.Id, runOutcome.PeakWorkingSetMb,
                        runOutcome.Fps.Average, runOutcome.Fps.Samples, span, exitCode: runOutcome.ExitCode);
                    if (stamp != null) Dispatch(() =>
                    {
                        RefreshInstanceRow(stamp);
                        RefreshPerformanceCard();
                    });
                }

                if (SaveBackupPolicy.ShouldRun(LauncherConfig.Instance.SaveBackup, BackupTrigger.AfterExit))
                    await BackupSavesAsync(instance, BackupTrigger.AfterExit, interactive: false).ConfigureAwait(true);
            }
            IsBusy = false;
        }
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
            _lastBundlePath = dlg.FileName; // 「发送给作者」直接复用这份包
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

    /// <summary>Round-70: exports the current log as a sanitized .txt. The log
    /// page had no way to hand its content to a maintainer except the full
    /// diagnostics .zip — this is the one-click version users actually asked
    /// for. Same sanitizer as the bundle, so tokens/paths can never leak.</summary>
    public async Task ExportLogsAsync()
    {
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出日志",
                FileName = $"aurora-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                DefaultExt = ".txt",
                AddExtension = true
            };
            if (dlg.ShowDialog() != true) return;

            Status = "正在导出日志…";
            var (ok, chars, message) = await DiagnosticsExporter.ExportLogTextAsync(
                dlg.FileName, LauncherConfig.Instance.RootDirectory);
            if (ok)
            {
                Status = $"日志已导出: {dlg.FileName}";
                ShowToast($"日志已导出（{chars:n0} 字符，已脱敏）：{System.IO.Path.GetFileName(dlg.FileName)}");
                Logger.Instance.Info("ui", $"导出日志 → {dlg.FileName}（{chars} 字符，已脱敏）");
            }
            else
            {
                Status = "导出日志失败: " + message;
            }
        }
        catch (Exception ex)
        {
            Status = "导出日志失败: " + ex.Message;
        }
    }

    // ---------------- round-77: 整合包导出 / 导入 ----------------

    /// <summary>Exports the selected instance as a .mrpack (round-77). Mods that
    /// Modrinth already hosts become CDN references; the rest, plus config, go
    /// into overrides/.</summary>
    public async Task ExportMrpackAsync()
    {
        var instance = SelectedInstance;
        if (instance == null) { Status = "请先选择实例"; return; }
        if (IsBusy) { Status = "有任务正在进行中，请稍后再导出"; return; }

        var safeName = new string(instance.Name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出整合包",
            FileName = (safeName.Length == 0 ? "modpack" : safeName) + ".mrpack",
            Filter = "Modrinth 整合包 (*.mrpack)|*.mrpack",
            DefaultExt = ".mrpack",
            AddExtension = true
        };
        if (dlg.ShowDialog() != true) return;

        IsBusy = true;
        Status = "正在导出整合包…";
        try
        {
            var target = dlg.FileName;
            var result = await Task.Run(() => MrpackExporter.ExportAsync(
                instance, target, instance.Name, null, null,
                p => Dispatch(() => Status = p))).ConfigureAwait(true);

            Status = "整合包已导出: " + result.Summary;
            Logger.Instance.Info("Export", $"导出整合包 {dlg.FileName} —— {result.Summary}");

            var unresolvedNote = result.HasUnresolved
                ? "\n\n以下模组 Modrinth 未收录，已一并打包进 overrides（会让文件变大）：\n" +
                  string.Join("、", result.Unresolved.Take(8)) + (result.Unresolved.Count > 8 ? " …" : "")
                : "";
            System.Windows.MessageBox.Show(Application.Current.MainWindow,
                $"已导出整合包：\n{dlg.FileName}\n\n{result.Summary}{unresolvedNote}\n\n" +
                "把这个文件发给朋友，直接拖进启动器窗口即可一键安装。",
                "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Status = "导出失败: " + ex.Message;
            Logger.Instance.Warn("Export", "导出整合包失败: " + ex.Message);
        }
        finally { IsBusy = false; }
    }

    private void ChooseAndImportMrpack()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入整合包",
            Filter = "Modrinth 整合包 (*.mrpack)|*.mrpack|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog() != true) return;
        _ = ImportMrpackAsync(dlg.FileName);
    }

    /// <summary>Imports a local .mrpack: reads its target MC version + loader from
    /// modrinth.index.json, creates a matching instance, installs the loader and
    /// then deploys the pack (round-77).</summary>
    public async Task ImportMrpackAsync(string path)
    {
        if (!File.Exists(path)) { Status = "找不到该整合包文件"; return; }
        if (!path.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
        {
            Status = "只支持 .mrpack 格式的整合包";
            Dispatch(() => ShowToast("只支持 .mrpack 格式的整合包"));
            return;
        }
        if (IsBusy) { Status = "有任务正在进行中，请等待完成或取消后再导入"; return; }

        MrpackInfo info;
        try { info = ModManager.ReadMrpackInfo(path); }
        catch (Exception ex)
        {
            Status = "整合包解析失败: " + ex.Message;
            Dispatch(() => System.Windows.MessageBox.Show(Application.Current.MainWindow,
                $"无法读取该整合包：\n{ex.Message}\n\n请确认它是 Modrinth 格式（内含 modrinth.index.json）。",
                "导入失败", MessageBoxButton.OK, MessageBoxImage.Warning));
            return;
        }
        if (string.IsNullOrWhiteSpace(info.GameVersion))
        {
            Status = "整合包没有声明游戏版本，无法自动创建实例";
            return;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        DownloadJob? job = null;
        try
        {
            IsBusy = true;
            var inst = _svc.Instances.Create(name, info.GameVersion, info.Loader, info.LoaderVersion);
            Instances.Add(inst);
            SelectedInstance = inst;

            // Round-92: record WHAT the pack should contain before the first
            // byte is downloaded. An import that dies halfway then leaves an
            // instance the launcher can recognise as incomplete — the old code
            // only wrote this at the very end, so a failure left a "normal"
            // instance behind.
            RecordModpackExpectation(inst, path, name, info);
            Navigate("Instances");
            Logger.Instance.Info("Import", $"导入整合包 {path}（MC {info.GameVersion} / {info.Loader}）");

            job = await BeginJobAsync("整合包", $"{name}（导入）", inst.Name);
            var stages = BuildStages(info.Loader);
            stages.Add(("modpack", "部署整合包文件"));
            InitStages(job, stages, $"{name}（MC {info.GameVersion} / {info.Loader}）导入");

            await _svc.InstallWithLoaderAsync(inst, job.Report,
                s => Dispatch(() => AddLog("[Install] " + s)), job.Token, job.EnterStage);
            // Round-92: the loader step is what fills ResolvedVersionId — save it.
            // Without this the id lived only in memory and a restart turned the
            // instance back into "loader never installed" (silent vanilla launch).
            _svc.Instances.Update(inst);
            OnInstallStage(job, "modpack");
            await _svc.ModManager.InstallMrpackAsync(path, inst.Directory, job.Report, job.Token);
            ScanMods();
            var packScan = ModpackIntegrity.Evaluate(inst);
            if (packScan.IsBroken)
                Logger.Instance.Warn("Import", $"{inst.Name}: 导入结束但模组不齐（{packScan.Describe()}）");
            _svc.Instances.Update(inst);
            FinishJob(job, null, false,
                $"整合包「{name}」已导入：实例「{inst.Name}」创建完成（{info.ClientFiles} 个文件已就位）");
            Status = $"整合包已导入: {inst.Name}";
        }
        catch (OperationCanceledException) { FinishJob(job, null, true); Status = "已取消导入（未完成的临时文件已清理）"; }
        catch (Exception ex)
        {
            FinishJob(job, ex, false);
            Status = "导入失败: " + ex.Message;
            Logger.Instance.Warn("Import", "导入整合包失败: " + ex.Message);
        }
        finally { IsBusy = false; RefreshDownloadAggregate(force: true); }
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

    /// <summary>Round-84: "新建实例" is a NAVIGATION, not a creation.
    ///
    /// It used to call <c>Instances.Create(SelectedVersion?.Id ?? "1.21.1")</c>,
    /// which produced an empty record for a version that was never installed:
    /// a bogus entry appeared in the list, had no jar/libraries, and every launch
    /// attempt failed. Instances now come into existence only at the end of a
    /// real install (or an import), where the files actually exist.</summary>
    public void CreateInstance()
    {
        Status = "去「游戏下载」挑一个版本，点安装；装完会自动出现在实例列表里";
        AddLog("[Instance] 新建实例 → 跳转游戏下载页（不再直接生成空实例）");
        ShowToast("先选一个游戏版本安装，装完就是新实例");
        Navigate("Download");
    }

    /// <summary>Round-84: deletes the FOLDER, and only then the record.
    ///
    /// Two hard requirements from the user reports:
    /// 1. Confirmation with the real size — "删除" must mean "delete 12.4 GB from
    ///    my disk", not "hide a row".
    /// 2. If the folder cannot be removed (game still exiting, Explorer holding
    ///    it, antivirus scanning), the record MUST stay and the user MUST be told.
    ///    Removing the record while the folder survives is exactly how a folder
    ///    becomes invisible and unremovable.
    /// Runs off the UI thread; the recursive delete of a big instance takes seconds.</summary>
    public async Task DeleteSelectedInstanceAsync()
    {
        var removed = SelectedInstance;
        if (removed == null) { Status = "请先选择要删除的实例"; return; }
        if (IsBusy) { Status = "有任务正在进行中，请等待完成后再删除"; return; }

        var mb = await Task.Run(() => FolderSizeMb(removed.Directory)).ConfigureAwait(true);
        var sizeText = mb > 0 ? JobProgressPolicy.FormatBytes(mb * 1024L * 1024L) : "未知";

        var answer = System.Windows.MessageBox.Show(Application.Current.MainWindow,
            $"确定要彻底删除实例「{removed.Name}」吗？\n\n" +
            $"会删除整个文件夹（约 {sizeText}，含存档、模组、配置、日志）：\n{removed.Directory}\n\n" +
            "此操作不可恢复。如果只是想修复游戏文件，请用「补全文件」。",
            "彻底删除实例", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            Status = "已取消删除";
            return;
        }

        IsBusy = true;
        Status = $"正在删除实例「{removed.Name}」（含文件夹）…";
        AddLog($"[Instance] 开始彻底删除：{removed.Name}（{removed.Id}）→ {removed.Directory}");
        try
        {
            var result = await Task.Run(() => _svc.Instances.DeleteChecked(removed.Id)).ConfigureAwait(true);
            if (result.Ok)
            {
                Instances.Remove(removed);
                if (ReferenceEquals(SelectedInstance, removed))
                    SelectedInstance = Instances.FirstOrDefault();
                Status = "已彻底删除实例: " + removed.Name + "（文件夹 + 列表记录；未被引用的版本文件已自动清理）";
                AddLog("[Instance] 删除完成：" + Status);
                ShowToast("实例已彻底删除");
                return;
            }

            // NOT deleted → the record stays on purpose.
            Status = result.Describe(removed.Name);
            AddLog("[Instance] 删除未完成：" + Status);
            System.Windows.MessageBox.Show(Application.Current.MainWindow,
                $"无法删除实例「{removed.Name}」的文件夹。\n\n" +
                $"原因：{result.Message}\n\n" +
                "列表记录已保留（不会出现“列表里没了、硬盘里还在”的幽灵实例）。\n" +
                "请先关闭游戏 / 资源管理器窗口 / 杀毒软件扫描，然后重试。",
                "删除失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            // Same rule: an exception here means the folder may still exist, so
            // nothing was removed from the list.
            Status = "删除失败: " + ex.Message + "（列表记录已保留，请关闭占用后重试）";
            AddLog("[Instance] 删除异常: " + ex);
            System.Windows.MessageBox.Show(Application.Current.MainWindow,
                "删除过程中出错，实例记录已保留：\n\n" + ex.Message,
                "删除失败", MessageBoxButton.OK, MessageBoxImage.Error);
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
        DownloadJob? job = null;
        try
        {
            job = await BeginJobAsync("修复", $"{inst.Name} 文件补全", inst.Name);
            InitStages(job, BuildStages(inst.LoaderType), $"{inst.Name} 文件补全");
            await _svc.RepairAsync(inst, job.Report,
                s => Dispatch(() => AddLog("[Repair] " + s)), job.Token, job.EnterStage);
            _svc.Instances.Update(inst);
            FinishJob(job, null, false, $"实例 {inst.Name} 文件补全完成");
            Status = $"实例 {inst.Name} 文件补全完成";
        }
        catch (OperationCanceledException) { FinishJob(job, null, true); Status = "已取消（未完成的临时文件已清理）"; }
        catch (Exception ex) { FinishJob(job, ex, false); Status = "补全失败: " + ex.Message; }
        finally { RefreshDownloadAggregate(force: true); }
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
    private const int MaxPendingLogLines = 2000; // backlog kept while the page is closed

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingLogLines = new();
    private DispatcherTimer? _logFlushTimer;

    /// <summary>Queues a log line for batched UI delivery.</summary>
    private void EnqueueLog(string line)
    {
        _pendingLogLines.Enqueue(line);
        // Bounded backlog: an install of a few thousand files while the Logs page
        // is closed must not grow this queue without limit.
        while (_pendingLogLines.Count > MaxPendingLogLines && _pendingLogLines.TryDequeue(out _)) { }
    }

    private bool IsLogsPageActive =>
        string.Equals(CurrentPageKey, "Logs", StringComparison.Ordinal);

    /// <summary>Round-69: while the Logs page is hidden, nobody can read these
    /// lines — so they stay queued instead of being pushed into the bound
    /// collection. That keeps installs/downloads from paying for hundreds of
    /// collection notifications per second that no one sees. Entering the page
    /// publishes the backlog through <see cref="RefreshLogsOnShow"/>.</summary>
    private void FlushPendingLogs()
    {
        if (_pendingLogLines.IsEmpty) return;
        if (!IsLogsPageActive) return;
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

    /// <summary>Round-69: one Reset instead of N per-line updates when the Logs
    /// page becomes visible again. 600 lines arrive at once, but the ListBox
    /// measures its viewport exactly once.</summary>
    private void RefreshLogsOnShow()
    {
        if (_pendingLogLines.IsEmpty) return;
        var snapshot = new List<string>(Logs);
        while (_pendingLogLines.TryDequeue(out var l)) snapshot.Add(l);
        if (snapshot.Count > MaxLogLines)
            snapshot.RemoveRange(0, snapshot.Count - MaxLogLines);
        Logs.Replace(snapshot);
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
            // Round-69: the old code cleared the filter whenever SetProperty
            // reported "no change", so a redundant `= true` silently dropped the
            // filter while the toggle still read "只看错误".
            SetProperty(ref _showErrorsOnly, value);
            LogsView.Filter = value ? ErrorsOnlyFilter : null;
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
        DownloadJob? job = null;
        try
        {
            var project = SelectedContentItem.Project;
            if (type == ContentType.Modpack) { await InstallModpackAsync(project, vh.Version); return; }

            var inst = SelectedInstance ?? throw new InvalidOperationException("请先选择实例");
            job = await BeginJobAsync(ContentKind, $"{project.Title} {vh.Version.VersionNumber}", inst.Name);
            Status = $"正在下载 {project.Title} {vh.Version.VersionNumber} ...";
            job.SetStageText($"下载 {ContentKind}：{project.Title} {vh.Version.VersionNumber}");
            await _svc.ModManager.DownloadContentAsync(project, vh.Version, type, inst.Directory,
                type == ContentType.Datapack ? SelectedWorld : null, job.Report, job.Token);
            if (type == ContentType.Mod)
                await EnsureFabricApiAsync(inst, job.Token, $"安装模组 {project.Title}");
            ScanMods();
            ShowContentDetail = false;
            FinishJob(job, null, false, $"已安装 {project.Title} {vh.Version.VersionNumber} 到 {inst.Name}");
            Status = $"已安装 {project.Title} {vh.Version.VersionNumber} 到 {inst.Name}";
        }
        catch (OperationCanceledException) { FinishJob(job, null, true); Status = "已取消下载（未完成的临时文件已清理）"; }
        catch (Exception ex) { FinishJob(job, ex, false); Status = "安装失败: " + ex.Message; }
        finally { RefreshDownloadAggregate(force: true); }
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

    private int _searchRunning;

    /// <summary>Single-flight (round-63): the cached page's Loaded hook used
    /// to start a fresh Modrinth search on EVERY visit while results were
    /// empty. Rapid switching produced several concurrent searches that all
    /// returned to the UI thread to rebuild the result list.</summary>
    public async Task SearchContentAsync()
    {
        if (Interlocked.Exchange(ref _searchRunning, 1) != 0)
        {
            Status = "正在搜索中，请稍候…";
            return;
        }
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
            Dispatch(() => ModSearchResults.Replace(items));
            HasSearched = true;
            NotifyPaging();
            Status = $"「{ContentKind}」搜索到 {result.TotalHits:N0} 个结果（第 {_searchPage + 1} 页，已按实例自动适配）";
        }
        catch (Exception ex)
        {
            HasSearched = true;
            Status = "Modrinth 搜索失败: " + FriendlyNetworkError(ex);
        }
        finally { IsSearching = false; Volatile.Write(ref _searchRunning, 0); }
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

        DownloadJob? job = null;
        try
        {
            var inst = SelectedInstance ?? throw new InvalidOperationException("请先选择实例（用于自动适配版本）");
            job = await BeginJobAsync(ContentKind, $"{project.Title}（{ContentKind}）", inst.Name);
            Status = $"正在为 {inst.Name} 查找适配的{ContentKind}...";
            var loader = type == ContentType.Mod && inst.LoaderType != ModLoaderType.Vanilla
                ? inst.LoaderType.ToString().ToLowerInvariant() : null;
            var versions = await _svc.ModManager.GetVersionsAsync(project.ProjectId, type, inst.MinecraftVersion, loader, job.Token);
            if (versions.Count == 0)
            {
                var reason = $"未找到适配 {inst.Name}（MC {inst.MinecraftVersion}{(loader != null ? " / " + inst.LoaderType : "")}）的{ContentKind}版本";
                FinishJob(job, new InvalidOperationException(reason), false);
                Status = reason;
                return;
            }
            Status = $"正在下载 {project.Title} {versions[0].VersionNumber} ...";
            job.SetStageText($"下载 {ContentKind}：{project.Title} {versions[0].VersionNumber}");
            await _svc.ModManager.DownloadContentAsync(project, versions[0], type, inst.Directory,
                type == ContentType.Datapack ? SelectedWorld : null, job.Report, job.Token);
            if (type == ContentType.Mod)
                await EnsureFabricApiAsync(inst, job.Token, $"安装模组 {project.Title}");
            ScanMods();
            FinishJob(job, null, false, $"已安装 {project.Title} {versions[0].VersionNumber} 到 {inst.Name}");
            Status = $"已安装 {project.Title} {versions[0].VersionNumber} 到 {inst.Name}";
        }
        catch (OperationCanceledException) { FinishJob(job, null, true); Status = "已取消下载（未完成的临时文件已清理）"; }
        catch (Exception ex) { FinishJob(job, ex, false); Status = "安装失败: " + ex.Message; }
        finally { RefreshDownloadAggregate(force: true); }
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
        DownloadJob? job = null;
        try
        {
            ModrinthVersion packVer;
            if (specific != null)
            {
                packVer = specific;
            }
            else
            {
                Status = $"正在获取整合包 {project.Title} 的版本信息...";
                var versions = await _svc.ModManager.GetVersionsAsync(project.ProjectId, ContentType.Modpack, null, null);
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
            job = await BeginJobAsync("整合包", $"{project.Title}（{packVer.FileName}）", "");
            InitStages(job, new List<(string, string)> { ("modpack", "下载整合包文件") },
                $"{project.Title}（{packVer.FileName}）");
            OnInstallStage(job, "modpack");

            var tempDir = Path.Combine(Path.GetTempPath(), "AuroraLauncher", "mrpack");
            Directory.CreateDirectory(tempDir);
            var tempFile = Path.Combine(tempDir, project.ProjectId + ".mrpack");
            Status = $"正在下载整合包 {packVer.FileName} ...";
            await _svc.ModManager.DownloadMrpackAsync(project, packVer, tempFile, job.Report, job.Token);

            var info = ModManager.ReadMrpackInfo(tempFile);
            var inst = _svc.Instances.Create($"{project.Title}", info.GameVersion, info.Loader, info.LoaderVersion);
            Dispatch(() => { Instances.Add(inst); SelectedInstance = inst; });
            // Round-92: same expectation-first rule as the local import path.
            RecordModpackExpectation(inst, tempFile, project.Title, info);

            // Phase 2: full pipeline stages (game + loader + deploy pack files).
            var stages = BuildStages(info.Loader);
            stages.Add(("modpack", "部署整合包文件"));
            InitStages(job, stages, $"{project.Title}（MC {info.GameVersion} / {info.Loader}）安装");

            Status = $"正在安装整合包 {project.Title}（MC {info.GameVersion} / {info.Loader}）...";
            await _svc.InstallWithLoaderAsync(inst, job.Report, s => Dispatch(() => AddLog("[Install] " + s)), job.Token, job.EnterStage);
            _svc.Instances.Update(inst); // Round-92: persist ResolvedVersionId
            OnInstallStage(job, "modpack");
            await _svc.ModManager.InstallMrpackAsync(tempFile, inst.Directory, job.Report, job.Token);
            ScanMods();
            _svc.Instances.Update(inst);
            var done = $"整合包 {project.Title} 安装完成，已创建实例「{inst.Name}」（{info.ClientFiles} 个文件已就位，启动时自动补齐依赖）";
            FinishJob(job, null, false, done);
            Status = done;
        }
        catch (OperationCanceledException) { FinishJob(job, null, true); Status = "已取消下载（未完成的临时文件已清理）"; }
        catch (Exception ex) { FinishJob(job, ex, false); Status = "整合包安装失败: " + ex.Message; }
        finally { RefreshDownloadAggregate(force: true); }
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
        // Round-83: auto mode is a flag, not a number. Keep MaxMemoryMb as the
        // player's hand-typed value, so turning auto off restores THEIR number
        // instead of whatever the recommendation happened to be today.
        c.AutoMemory = AutoMemory;
        c.ReleaseIdleMemory = ReleaseIdleMemory;
        if (!AutoMemory)
        {
            c.MaxMemoryMb = MemoryMb;
            c.MinMemoryMb = Math.Max(512, MemoryMb / 2);
        }
        c.JavaPath = string.IsNullOrWhiteSpace(JavaPath) ? null : JavaPath;
        c.RoutePolicy = DownloadRoutePolicy.Parse(DownloadPolicyText);
        c.ProxyUrl = string.IsNullOrWhiteSpace(ProxyUrl) ? null : ProxyUrl;
        c.MicrosoftClientId = string.IsNullOrWhiteSpace(MicrosoftClientIdText) ? null : MicrosoftClientIdText.Trim();
        c.AutoInstallFabricApi = AutoInstallFabricApi;
        c.CheckModsBeforeLaunch = CheckModsBeforeLaunch;
        c.UpdateMode = UpdatePolicy.FromDisplayText(UpdateModeText);
        c.UpdateFeedUrl = string.IsNullOrWhiteSpace(UpdateFeedUrlText) ? null : UpdateFeedUrlText.Trim();
        RefreshMicrosoftLoginState();
        c.WindowWidth = WindowW; c.WindowHeight = WindowH;
        c.ExtraJvmArgs = ExtraJvm;
        c.Theme = DarkTheme ? ThemePreference.Dark : ThemePreference.Light;
        c.CloseAction = CloseBehaviorPolicy.FromDisplayText(CloseActionText);
        c.MinimizeToTrayEnabled = TrayEnabled;

        // ---- storage directory change (requires restart to take effect) ----
        var newRoot = (RootDirectoryText ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(newRoot))
        {
            try { newRoot = Path.GetFullPath(newRoot); }
            catch { Status = "存储目录不是有效路径"; return; }
        }
        if (IsPortableMode)
        {
            // Portable mode owns the path: the folder wins on every start, so a
            // changed value here would silently revert on the next launch.
            c.RootDirectory = LauncherConfig.Instance.RootDirectory;
        }
        else if (!string.IsNullOrWhiteSpace(newRoot) &&
            !string.Equals(newRoot, Path.GetFullPath(c.RootDirectory), StringComparison.OrdinalIgnoreCase))
        {
            var oldRoot = c.RootDirectory;
            c.RootDirectory = newRoot;
            // Persist the new value into BOTH locations: the old config file is
            // the one read on next startup, so it must carry the new root too.
            c.Save();
            try { File.WriteAllText(Path.Combine(oldRoot, "config.json"), JsonHelper.SerializeIndented(c)); }
            catch { /* old dir may be gone; new location config is already written */ }
            MarkSettingsSaved(); // round-90: the baseline must follow a real save

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
        MarkSettingsSaved();
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

    public void CancelDownload()
    {
        // Round-72: cancel EVERY job, then the legacy single token if some flow
        // still uses it (launch-time repair / script export).
        _jobManager.CancelAll();
        _cts?.Cancel();
        RefreshDownloadAggregate(force: true);
        Status = "已取消下载";
    }
}
