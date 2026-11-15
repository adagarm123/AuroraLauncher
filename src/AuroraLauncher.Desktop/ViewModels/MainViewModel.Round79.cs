using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using AuroraCore;
using AuroraCore.Models;
using AuroraCore.Services;

namespace AuroraLauncher.Desktop.ViewModels;

/// <summary>
/// Round-79: 存档自动备份 · 性能仪表盘 · Ctrl+K 全局搜索.
///
/// Kept in a partial file so the three features stay readable next to each
/// other, while every <c>RelayCommand</c> property stays in MainViewModel.cs
/// (the XAML-binding guard scans that file for command properties).
/// </summary>
public partial class MainViewModel
{
    // ==================== 1. 存档自动备份 ====================

    /// <summary>Human labels for the mode combo box (order = menu order).</summary>
    public List<string> SaveBackupModeOptions { get; } = new()
    {
        SaveBackupPolicy.DescribeMode(SaveBackupMode.Off),
        SaveBackupPolicy.DescribeMode(SaveBackupMode.BeforeLaunch),
        SaveBackupPolicy.DescribeMode(SaveBackupMode.AfterExit),
        SaveBackupPolicy.DescribeMode(SaveBackupMode.Both)
    };

    public List<string> SaveBackupKeepOptions { get; } = new() { "3 份", "5 份", "10 份" };

    private string _saveBackupText = SaveBackupPolicy.DescribeMode(LauncherConfig.Instance.SaveBackup);

    /// <summary>Bound to the combo box; persists immediately (no Apply needed —
    /// a safety switch that silently forgets itself is a trap).</summary>
    public string SaveBackupText
    {
        get => _saveBackupText;
        set
        {
            if (!SetProperty(ref _saveBackupText, value)) return;
            var cfg = LauncherConfig.Instance;
            cfg.SaveBackup = SaveBackupPolicy.ParseMode(value);
            cfg.Save();
            OnPropertyChanged(nameof(SaveBackupNote));
            AddLog($"[Backup] 自动备份策略改为：{value}");
        }
    }

    private string _saveBackupKeepText = $"{SaveBackupPolicy.ClampKeep(LauncherConfig.Instance.SaveBackupKeep)} 份";

    public string SaveBackupKeepText
    {
        get => _saveBackupKeepText;
        set
        {
            if (!SetProperty(ref _saveBackupKeepText, value)) return;
            var digits = new string(value.TakeWhile(char.IsDigit).ToArray());
            if (!int.TryParse(digits, out var keep)) return;
            var cfg = LauncherConfig.Instance;
            cfg.SaveBackupKeep = SaveBackupPolicy.ClampKeep(keep);
            cfg.Save();
            OnPropertyChanged(nameof(SaveBackupNote));
        }
    }

    public string SaveBackupNote => LauncherConfig.Instance.SaveBackup == SaveBackupMode.Off
        ? "自动备份已关闭。手动备份仍然可用，但蓝屏/误删时不会有人替你留底。"
        : $"每次{(LauncherConfig.Instance.SaveBackup == SaveBackupMode.BeforeLaunch ? "启动前" :
            LauncherConfig.Instance.SaveBackup == SaveBackupMode.AfterExit ? "退出后" : "启动前与退出后")}" +
          $"把 saves/ 打包到 backups/ 目录，只保留最近 {SaveBackupPolicy.ClampKeep(LauncherConfig.Instance.SaveBackupKeep)} 份。" +
          "备份只读取存档，不会修改或删除任何存档文件。";

    private string _backupInfoText = "还没有备份";
    public string BackupInfoText
    {
        get => _backupInfoText;
        private set => SetProperty(ref _backupInfoText, value);
    }

    private void RefreshBackupInfo()
    {
        var inst = SelectedInstance;
        if (inst == null) { BackupInfoText = "先选择一个实例"; return; }
        var dir = SaveBackupService.BackupRootOf(LauncherConfig.Instance.RootDirectory, inst.Id);
        var all = SaveBackupService.List(dir);
        if (all.Count == 0) { BackupInfoText = "这个实例还没有备份"; return; }
        var newest = all[0];
        BackupInfoText = $"最近一次：{newest.CreatedUtc.ToLocalTime():MM-dd HH:mm} · " +
                         $"{newest.Bytes / 1024} KB · 共 {all.Count} 份";
    }

    private async Task<SaveBackupResult?> BackupSavesAsync(GameInstance? inst, BackupTrigger trigger, bool interactive)
    {
        if (inst == null) return null;
        if (interactive) Status = "正在备份存档…";
        var cfg = LauncherConfig.Instance;
        var dir = SaveBackupService.BackupRootOf(cfg.RootDirectory, inst.Id);
        var saves = SaveBackupService.SavesOf(inst.Directory);

        var result = await Task.Run(() => SaveBackupService.CreateAsync(
            saves, dir, inst.Name, trigger, cfg.SaveBackupKeep)).ConfigureAwait(true);

        if (result.Ok)
        {
            AddLog($"[Backup] {inst.Name}：{result.Message}");
            if (interactive)
            {
                Status = result.Message;
                ShowToast(result.Message);
            }
            RefreshBackupInfo();
        }
        else
        {
            AddLog($"[Backup] 跳过/失败（{trigger}）：{result.Message}");
            if (interactive)
            {
                Status = result.Message;
                System.Windows.MessageBox.Show(Application.Current.MainWindow, result.Message,
                    "存档备份", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
        }
        return result;
    }

    public async Task BackupSavesNowAsync()
    {
        var inst = SelectedInstance;
        if (inst == null) { Status = "请先选择一个实例"; return; }
        await BackupSavesAsync(inst, BackupTrigger.Manual, interactive: true).ConfigureAwait(true);
    }

    public void OpenBackupFolder()
    {
        var inst = SelectedInstance;
        if (inst == null) { Status = "请先选择一个实例"; return; }
        OpenFolder(SaveBackupService.BackupRootOf(LauncherConfig.Instance.RootDirectory, inst.Id));
    }

    // ==================== 2. 性能仪表盘 ====================

    private readonly SystemLoadProbe _loadProbe = new();
    private DispatcherTimer? _loadTimer;

    private string _systemCpuText = "—";
    public string SystemCpuText { get => _systemCpuText; private set => SetProperty(ref _systemCpuText, value); }

    private string _systemMemoryText = "—";
    public string SystemMemoryText { get => _systemMemoryText; private set => SetProperty(ref _systemMemoryText, value); }

    private string _systemLoadNote = "正在读取系统状态…";
    public string SystemLoadNote { get => _systemLoadNote; private set => SetProperty(ref _systemLoadNote, value); }

    private string _runPerfText = "还没有运行记录";
    public string RunPerfText { get => _runPerfText; private set => SetProperty(ref _runPerfText, value); }

    private string _runVerdictLevel = "无数据";
    public string RunVerdictLevel { get => _runVerdictLevel; private set => SetProperty(ref _runVerdictLevel, value); }

    private string _runVerdictText = "启动一次游戏后，这里会根据那次的峰值内存和日志里的 FPS 给出结论。";
    public string RunVerdictText { get => _runVerdictText; private set => SetProperty(ref _runVerdictText, value); }

    private void InitRound79()
    {
        // 3 s is slow enough to be invisible in Task Manager and fast enough that
        // "CPU 95%" is still true when the user looks at it. Background priority
        // so it never competes with input/layout.
        _loadTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _loadTimer.Tick += async (_, _) => await RefreshSystemLoadAsync().ConfigureAwait(true);
        _loadTimer.Start();
        _ = RefreshSystemLoadAsync();
        RefreshPerformanceCard();
    }

    private async Task RefreshSystemLoadAsync()
    {
        try
        {
            var snap = await Task.Run(() => _loadProbe.Read()).ConfigureAwait(true);
            if (!snap.Ok)
            {
                SystemCpuText = "—";
                SystemMemoryText = "—";
                SystemLoadNote = "无法读取系统状态（受限环境或非 Windows）";
                return;
            }
            SystemCpuText = $"{snap.CpuPercent:0}%";
            SystemMemoryText = $"{snap.AvailableMb / 1024.0:0.0} GB 可用 / {snap.TotalMb / 1024.0:0.0} GB";
            SystemLoadNote = snap.UsedPercent >= 88
                ? $"内存已占用 {snap.UsedPercent}%：启动游戏前建议先关掉一些程序"
                : $"内存占用 {snap.UsedPercent}%";
        }
        catch
        {
            SystemLoadNote = "无法读取系统状态";
        }
    }

    /// <summary>Refreshes the "last run" half of the dashboard. Called when the
    /// selected instance changes and right after a run finishes.</summary>
    private void RefreshPerformanceCard()
    {
        var inst = SelectedInstance;
        // Round-83: auto mode means the stored number is not what the game got —
        // judge the peak against the heap that will actually be used.
        var maxMemory = EffectiveMemoryMb();
        var fps = inst == null || inst.LastFpsSamples == 0
            ? FpsSummary.None
            : new FpsSummary(inst.LastAverageFps, inst.LastAverageFps, inst.LastFpsSamples);
        // Round-92: the verdict also knows how the run ENDED. Judging memory
        // alone is what made a 2-second crash read as「健康」.
        var (level, text) = PerformanceReport.RunVerdict(
            inst == null || inst.LastPerfAt == null ? 0 : inst.LastExitCode,
            inst?.LastRunSeconds ?? 0,
            inst?.LastPeakMemoryMb ?? 0, maxMemory, fps);
        RunVerdictLevel = level;
        RunVerdictText = text;
        RunPerfText = inst?.PerfSummaryText ?? "还没有运行记录";
    }

    // ==================== 3. Ctrl+K 全局搜索 ====================

    private bool _isPaletteOpen;
    public bool IsPaletteOpen
    {
        get => _isPaletteOpen;
        private set => SetProperty(ref _isPaletteOpen, value);
    }

    private string _paletteQuery = "";
    public string PaletteQuery
    {
        get => _paletteQuery;
        set
        {
            if (!SetProperty(ref _paletteQuery, value)) return;
            RebuildPalette();
        }
    }

    public ObservableCollection<PaletteItem> PaletteResults { get; } = new();

    private PaletteItem? _paletteSelected;
    public PaletteItem? PaletteSelected
    {
        get => _paletteSelected;
        set => SetProperty(ref _paletteSelected, value);
    }

    public string PaletteNote => PaletteResults.Count == 0
        ? "没有匹配的结果——换个关键词，或直接用「在 Modrinth 搜索」"
        : $"{PaletteResults.Count} 个结果 · ↑↓ 选择 · Enter 打开 · Esc 关闭";

    /// <summary>Opens (or closes) the command palette. Bound to the status-bar
    /// 「搜索」button and to Ctrl+K in MainWindow.</summary>
    public void TogglePalette()
    {
        if (IsPaletteOpen) { ClosePalette(); return; }
        IsPaletteOpen = true;
        PaletteQuery = "";       // resets the list to the "most useful head"
        RebuildPalette();
    }

    public void ClosePalette()
    {
        if (!IsPaletteOpen) return;
        IsPaletteOpen = false;
        PaletteQuery = "";
    }

    /// <summary>Index = pages/settings/actions (static) + instances + installed
    /// mods + a live "search Modrinth" row. Rebuilt on every keystroke because
    /// the web row depends on the query; 200 short records make that free.</summary>
    private void RebuildPalette()
    {
        try
        {
            var instances = Instances.Select(i =>
                (i.Id, i.Name, i.MinecraftVersion, i.LoaderType.ToString()));
            var mods = Mods.Select(m => (m.Name, m.FileName));
            var index = CommandPalette.BuildIndex(instances, mods, PaletteQuery);
            var results = CommandPalette.Search(index, PaletteQuery);
            PaletteResults.Clear();
            foreach (var item in results) PaletteResults.Add(item);
            PaletteSelected = PaletteResults.FirstOrDefault();
            OnPropertyChanged(nameof(PaletteNote));
        }
        catch (Exception ex)
        {
            AddLog("[Palette] 构建搜索索引失败：" + ex.Message);
        }
    }

    /// <summary>Runs one palette row. Called by MainWindow's Enter / double-click
    /// handler — a method, not a command, because the same row can be triggered
    /// from the keyboard without a CommandParameter.</summary>
    public void RunPalette(PaletteItem? item)
    {
        if (item == null) return;
        ClosePalette();
        switch (item.Action)
        {
            case "page":
                Navigate(item.Payload);
                Status = $"已打开「{item.Title}」";
                break;

            case "setting":
                Navigate("Settings");
                Status = $"设置页里可以调整「{item.Title}」";
                ShowToast($"已在设置页：{item.Title}");
                break;

            case "instance":
            {
                var inst = Instances.FirstOrDefault(i => i.Id == item.Payload);
                if (inst == null) { Status = "这个实例已经不在了"; return; }
                SelectedInstance = inst;
                Navigate("Instances");
                Status = $"已切换到实例「{inst.Name}」";
                break;
            }

            case "mod":
                ContentKind = "本地管理";
                Navigate("Mods");
                ScanMods();
                Status = $"已在本地模组里定位「{item.Title}」";
                break;

            case "web":
                ModSearchQuery = item.Payload ?? item.Title;
                ContentKind = "模组";
                Navigate("Mods");
                _ = SearchContentAsync();
                break;

            case CommandPalette.ActionLaunch:
                if (LaunchCommand.CanExecute(null)) LaunchCommand.Execute(null);
                break;

            case CommandPalette.ActionBackupNow:
                _ = BackupSavesNowAsync();
                break;

            case CommandPalette.ActionCheckUpdate:
                CheckUpdateCommand.Execute(null);
                break;

            case CommandPalette.ActionExportDiagnostics:
                ExportDiagnosticsCommand.Execute(null);
                break;

            case CommandPalette.ActionExportLogs:
                ExportLogsCommand.Execute(null);
                break;

            case CommandPalette.ActionSwitchTheme:
                SwitchTheme();
                break;

            case CommandPalette.ActionOpenModsFolder:
                OpenModsFolder();
                break;

            case CommandPalette.ActionOpenSavesFolder:
                OpenSavesFolder();
                break;

            case CommandPalette.ActionInstallJava:
                InstallJava21Command.Execute(null);
                break;

            case CommandPalette.ActionOpenRoot:
                OpenRootDirectory();
                break;

            default:
                Status = "这个入口暂时不可用";
                break;
        }
    }

    /// <summary>Keyboard navigation helper for MainWindow: moves the selection
    /// by ±1 and keeps it inside the list.</summary>
    public void MovePaletteSelection(int delta)
    {
        if (PaletteResults.Count == 0) return;
        var index = PaletteSelected == null ? -1 : PaletteResults.IndexOf(PaletteSelected);
        var next = Math.Clamp(index + delta, 0, PaletteResults.Count - 1);
        if (index < 0) next = delta > 0 ? 0 : PaletteResults.Count - 1;
        PaletteSelected = PaletteResults[next];
    }
}
