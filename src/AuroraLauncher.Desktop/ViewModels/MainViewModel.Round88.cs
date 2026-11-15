using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using AuroraCore;
using AuroraCore.Services;
using AuroraCore.Utils;
using AuroraLauncher.ViewModels;

namespace AuroraLauncher.Desktop.ViewModels;

/// <summary>Round-88: one row of the storage-cleanup report (a checkbox).</summary>
public sealed class CleanupCategoryVM : INotifyPropertyChanged
{
    private bool _selected;

    public CleanupCategoryVM(CleanupGroup group, Action onSelectionChanged)
    {
        Group = group;
        Info = group.Info;
        _selected = group.Info.DefaultSelected && group.Items.Count > 0; // never the hardcoded default
        _onSelectionChanged = onSelectionChanged;
    }

    private readonly Action _onSelectionChanged;

    public CleanupGroup Group { get; }
    public CleanupCategoryInfo Info { get; }
    public CleanupCategory Category => Info.Category;

    public string DisplayName => Info.DisplayName;
    public string Description => Info.Description;
    public string AmountText => Group.AmountText;
    public bool CostsOffline => Info.Risk == CleanupRisk.CostsOffline;

    /// <summary>Row text: "历史日志 · 9 个文件 · 10.6 MB（清理后断网时加载器列表不可用）".</summary>
    public string Label => $"{DisplayName} · {AmountText}" + (CostsOffline ? "（会牺牲离线能力）" : "");

    public bool HasSelection => _selected;
    public bool Selected    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
            _onSelectionChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Round-88: "扫描整个存储目录并清理垃圾缓存".
///
/// Interaction is deliberately the same as round-84's instance reconcile: scan →
/// show what was found → the user checks → confirm → delete → report honestly.
/// The default check state comes from <see cref="CleanupCategoryInfo.DefaultSelected"/>
/// (a CostsOffline category is never preselected), so a future change of mind
/// means editing the policy, not the XAML.
/// </summary>
public partial class MainViewModel
{
    private StorageCleanupService? _cleanupService;

    /// <summary>Test seam only: a modal confirmation would hang a headless smoke
    /// (there is nobody to click 「是」). Production never sets this, so the button
    /// always asks — the guard test asserts the default and the single read.</summary>
    public bool SuppressCleanupConfirmForTests { get; set; }

    public ObservableCollection<CleanupCategoryVM> StorageCleanupGroups { get; } = new();

    private bool _isScanningStorage;
    public bool IsScanningStorage
    {
        get => _isScanningStorage;
        private set
        {
            if (!SetProperty(ref _isScanningStorage, value)) return;
            OnPropertyChanged(nameof(StorageScanButtonText));
            OnPropertyChanged(nameof(CanEditStorageSelection));
        }
    }

    private bool _isCleaningStorage;
    public bool IsCleaningStorage
    {
        get => _isCleaningStorage;
        private set
        {
            if (!SetProperty(ref _isCleaningStorage, value)) return;
            OnPropertyChanged(nameof(StorageCleanButtonText));
            OnPropertyChanged(nameof(CanEditStorageSelection));
        }
    }

    private bool _hasStorageCleanupReport;
    public bool HasStorageCleanupReport { get => _hasStorageCleanupReport; private set => SetProperty(ref _hasStorageCleanupReport, value); }

    private string _storageCleanupSummary = "";
    /// <summary>"共 9 类 · 34 个文件 · 24.1 MB，勾选后点「清理选中项」"</summary>
    public string StorageCleanupSummary { get => _storageCleanupSummary; private set => SetProperty(ref _storageCleanupSummary, value); }

    private string _storageCleanupResult = "";
    public string StorageCleanupResult { get => _storageCleanupResult; private set => SetProperty(ref _storageCleanupResult, value); }

    private string _storageCleanupSelectionText = "";
    public string StorageCleanupSelectionText { get => _storageCleanupSelectionText; private set => SetProperty(ref _storageCleanupSelectionText, value); }

    /// <summary>The "we never touch these" line, straight from the policy.</summary>
    public string StorageCleanupHint => StorageCleanupPolicy.ProtectedSummary;

    /// <summary>Button captions flip while busy, so a slow run on 10k+ files is
    /// visibly "working" instead of looking like a dead click.</summary>
    public string StorageScanButtonText => IsScanningStorage ? "扫描中…" : "扫描垃圾文件";
    public string StorageCleanButtonText => IsCleaningStorage ? "清理中…" : "清理选中项";

    /// <summary>Checkboxes are frozen while a scan/cleanup runs (toggling them
    /// mid-run would change nothing — the selection was already snapshotted —
    /// and it looks like the app ignored the click).</summary>
    public bool CanEditStorageSelection => !IsScanningStorage && !IsCleaningStorage;

    public RelayCommand ScanStorageCleanupCommand { get; private set; } = null!;
    public RelayCommand ApplyStorageCleanupCommand { get; private set; } = null!;

    private void InitRound88Commands()
    {
        _cleanupService = new StorageCleanupService(LauncherConfig.Instance.RootDirectory);
        ScanStorageCleanupCommand = new RelayCommand(async _ => await ScanStorageCleanupAsync(), _ => !IsScanningStorage && !IsCleaningStorage);
        ApplyStorageCleanupCommand = new RelayCommand(async _ => await ApplyStorageCleanupAsync(),
            _ => !IsScanningStorage && !IsCleaningStorage && StorageCleanupGroups.Any(g => g.Selected));
    }

    private async Task ScanStorageCleanupAsync()
    {
        if (IsScanningStorage || IsCleaningStorage) return;
        IsScanningStorage = true;
        RaiseStorageCommands();
        StorageCleanupResult = "";
        try
        {
            StorageCleanupSummary = "正在扫描…";
            var report = await (new StorageCleanupService(LauncherConfig.Instance.RootDirectory)).ScanAsync();

            StorageCleanupGroups.Clear();
            foreach (var group in report.Groups)
                StorageCleanupGroups.Add(new CleanupCategoryVM(group, RefreshStorageSelection));

            HasStorageCleanupReport = report.Groups.Count > 0;
            if (report.IsEmpty)
            {
                StorageCleanupSummary = "扫描完成：没有发现可以清理的缓存或临时文件。";
                StorageCleanupSelectionText = "";
                return;
            }

            StorageCleanupSummary =
                $"共 {report.Groups.Count} 类 · {report.TotalFiles} 个文件" +
                (report.TotalDirs > 0 ? $" + {report.TotalDirs} 个空目录" : "") +
                $" · {JobProgressPolicy.FormatBytes(report.TotalBytes)}" +
                (report.ActivePartCount > 0 ? $"（另有 {report.ActivePartCount} 个残片正在下载，已跳过）" : "") +
                "。确认无误后点「清理选中项」。";
            RefreshStorageSelection();
            Logger.Instance.Info("Cleanup",
                $"存储清理扫描：{report.Groups.Count} 类 / {report.TotalFiles} 个文件 / " +
                $"{JobProgressPolicy.FormatBytes(report.TotalBytes)}");
        }
        catch (Exception ex)
        {
            StorageCleanupSummary = "扫描失败：" + ex.Message;
            Logger.Instance.Warn("Cleanup", "存储清理扫描失败: " + ex);
        }
        finally
        {
            IsScanningStorage = false;
            RaiseStorageCommands();
        }
    }

    private async Task ApplyStorageCleanupAsync()
    {
        if (IsScanningStorage || IsCleaningStorage) return;

        var selected = StorageCleanupGroups.Where(g => g.Selected).ToDictionary(g => g.Category, g => g.Group);
        if (selected.Count == 0) { StorageCleanupResult = "请先勾选要清理的项目。"; return; }

        var fileCount = selected.Values.Sum(g => g.Items.Count);
        var bytes = selected.Values.Sum(g => g.Bytes);
        var costsOffline = StorageCleanupGroups.Where(g => g.Selected && g.CostsOffline).Select(g => g.DisplayName).ToList();

        var warn = costsOffline.Count > 0
            ? "\n\n注意：\n· " + string.Join("\n· ", costsOffline) + "\n清理后这部分需要联网重新获取（断网时可能不可用）。"
            : "";
        var answer = SuppressCleanupConfirmForTests
            ? MessageBoxResult.Yes
            : MessageBox.Show(Application.Current.MainWindow,
                $"将删除 {fileCount} 项（约 {JobProgressPolicy.FormatBytes(bytes)}），此操作不可恢复。\n\n" +
                StorageCleanupPolicy.ProtectedSummary +
                warn + "\n\n确定开始清理吗？",
                "清理存储垃圾", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) { StorageCleanupResult = "已取消清理（未删除任何文件）。"; return; }

        IsCleaningStorage = true;
        RaiseStorageCommands();
        try
        {
            StorageCleanupResult = "正在清理…";
            var report = new CleanupReport
            {
                Groups = selected.Values.ToList(),
                RootDirectory = LauncherConfig.Instance.RootDirectory
            };
            var result = await (new StorageCleanupService(LauncherConfig.Instance.RootDirectory))
                .ExecuteAsync(report, selected.Keys.ToHashSet());

            StorageCleanupResult = result.Describe();
            Status = result.Describe();
            // What survived (locked files, skipped directories) must not look
            // like it was deleted — rescan so the list tells the truth.
            CleanupReport? after = null;
            try { after = await new StorageCleanupService(LauncherConfig.Instance.RootDirectory).ScanAsync(); }
            catch { }
            if (after != null)
            {
                StorageCleanupGroups.Clear();
                foreach (var group in after.Groups)
                    StorageCleanupGroups.Add(new CleanupCategoryVM(group, RefreshStorageSelection));
                HasStorageCleanupReport = after.Groups.Count > 0;
                if (after.IsEmpty) StorageCleanupSummary = "清理完成：已经没有可清理的内容了。";
                else StorageCleanupSummary = $"清理完成，仍剩 {after.TotalFiles + after.TotalDirs} 项 · {JobProgressPolicy.FormatBytes(after.TotalBytes)}。";
                RefreshStorageSelection();
            }
            // 可续传残片可能刚被清掉：台账条目已同步移除，页面的「继续下载」横幅
            // 也必须跟着重新加载，否则它还举着已经不存在的文件。
            _ = RefreshPendingDownloadsAfterResidueAsync();
        }
        catch (Exception ex)
        {
            StorageCleanupResult = "清理失败：" + ex.Message;
            Logger.Instance.Warn("Cleanup", "存储清理执行失败: " + ex);
        }
        finally
        {
            IsCleaningStorage = false;
            RaiseStorageCommands();
        }
    }

    private void RefreshStorageSelection()
    {
        var chosen = StorageCleanupGroups.Where(g => g.Selected).ToList();
        StorageCleanupSelectionText = chosen.Count == 0
            ? ""
            : $"已选中 {chosen.Count} 类 · {chosen.Sum(g => g.Group.Items.Count)} 项 · " +
              $"{JobProgressPolicy.FormatBytes(chosen.Sum(g => g.Group.Bytes))}";
        RaiseStorageCommands();
    }

    private void RaiseStorageCommands()
    {
        ScanStorageCleanupCommand?.RaiseCanExecuteChanged();
        ApplyStorageCleanupCommand?.RaiseCanExecuteChanged();
    }
}
