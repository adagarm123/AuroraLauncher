using System.IO;
using System.Threading.Tasks;
using System.Windows;
using AuroraCore;
using AuroraCore.Download;
using AuroraCore.Services;
using AuroraCore.Utils;
using AuroraLauncher.ViewModels;

namespace AuroraLauncher.Desktop.ViewModels;

/// <summary>
/// Round-87: leftover <c>.part</c> files, made visible and removable.
///
/// Two entry points, one shared scan:
///  · 设置 → 存储 → 「清理残留下载文件」 (user-initiated, reports what it freed)
///  · 下载管理页顶部的一条非阻塞提示, shown only above the configured thresholds
/// A live download is never touched — <see cref="ActiveDownloads"/> is the
/// authority on what a running job owns.
/// </summary>
public partial class MainViewModel
{
    private ResidueScan? _residueScan;
    private bool _residueScanning;

    private bool _hasResidueNotice;
    /// <summary>Download-page banner visibility (thresholds exceeded).</summary>
    public bool HasResidueNotice { get => _hasResidueNotice; private set => SetProperty(ref _hasResidueNotice, value); }

    private string _residueNoticeText = "";
    public string ResidueNoticeText { get => _residueNoticeText; private set => SetProperty(ref _residueNoticeText, value); }

    private string _residueStatusText = "";
    /// <summary>Settings page: last scan/cleanup result.</summary>
    public string ResidueStatusText { get => _residueStatusText; private set => SetProperty(ref _residueStatusText, value); }

    private bool _isCleaningResidue;
    public bool IsCleaningResidue { get => _isCleaningResidue; private set => SetProperty(ref _isCleaningResidue, value); }

    /// <summary>Dismissed by the user for this session — the scan still runs, it
    /// just stops nagging. A restart or a fresh download brings it back.</summary>
    private bool _residueNoticeDismissed;

    public RelayCommand CleanResidueCommand { get; private set; } = null!;
    public RelayCommand DismissResidueNoticeCommand { get; private set; } = null!;
    public RelayCommand OpenResidueSettingsCommand { get; private set; } = null!;

    private void InitRound87Commands()
    {
        CleanResidueCommand = new RelayCommand(async _ => await CleanResidueAsync(), _ => !IsCleaningResidue);
        DismissResidueNoticeCommand = new RelayCommand(_ =>
        {
            _residueNoticeDismissed = true;
            HasResidueNotice = false;
            Logger.Instance.Info("Download", "用户关闭了残留下载提示（本次运行不再显示）");
        });
        OpenResidueSettingsCommand = new RelayCommand(_ => Navigate("Settings"));
    }

    /// <summary>Startup / on-demand scan. Runs off the UI thread: it walks the
    /// whole data root (assets alone are tens of thousands of files) and must not
    /// delay the first frame.</summary>
    private void ScanDownloadResidue()
    {
        if (_residueScanning) return;
        _residueScanning = true;
        _ = Task.Run(() =>
        {
            try
            {
                var scan = DownloadResidue.Scan(LauncherConfig.Instance.RootDirectory);
                Dispatch(() => ApplyResidueScan(scan, fromStartup: true));
            }
            catch (Exception ex)
            {
                Logger.Instance.Trace("Download", "扫描下载残留失败（忽略）: " + ex.Message);
            }
            finally { _residueScanning = false; }
        });
    }

    private void ApplyResidueScan(ResidueScan scan, bool fromStartup)
    {
        _residueScan = scan;
        var cfg = LauncherConfig.Instance;
        var warn = !_residueNoticeDismissed &&
                   DownloadResidue.ShouldWarnFromConfig(scan, cfg.ResidueWarnBytesMb, cfg.ResidueWarnCount);

        HasResidueNotice = warn;
        if (warn)
        {
            ResidueNoticeText = $"检测到 {scan.ReclaimableCount} 个未完成的下载残留" +
                                $"（约 {JobProgressPolicy.FormatBytes(scan.ReclaimableBytes)}），可在设置中清理";
            Logger.Instance.Info("Download",
                $"残留下载提示：{scan.ReclaimableCount} 个文件 / {JobProgressPolicy.FormatBytes(scan.ReclaimableBytes)}" +
                $"（阈值 {cfg.ResidueWarnBytesMb} MB 或 {cfg.ResidueWarnCount} 个；其中 {scan.ActiveCount} 个正在下载）");
        }
        else if (scan.IsEmpty)
        {
            ResidueNoticeText = "";
        }

        if (fromStartup)
            ResidueStatusText = scan.IsEmpty
                ? "没有未完成的下载残留。"
                : DownloadResidue.Describe(scan);
    }

    /// <summary>「清理残留下载文件」. Junk (no owner, or an update/ leftover) is
    /// removed straight away; a file the journal still knows about is what
    /// 「继续下载」 would resume, so that case asks first.</summary>
    private async Task CleanResidueAsync()
    {
        if (IsCleaningResidue) return;
        IsCleaningResidue = true;
        try
        {
            var scan = await Task.Run(() => DownloadResidue.Scan(LauncherConfig.Instance.RootDirectory));
            _residueScan = scan;

            if (scan.ReclaimableCount == 0)
            {
                ResidueStatusText = scan.ActiveCount > 0
                    ? $"没有可清理的残留：{scan.ActiveCount} 个未完成文件正在下载中，不会被清理。"
                    : "没有发现未完成的下载残留。";
                HasResidueNotice = false;
                return;
            }

            if (scan.JournaledCount > 0)
            {
                var answer = MessageBox.Show(Application.Current.MainWindow,
                    $"其中 {scan.JournaledCount} 个文件（约 {JobProgressPolicy.FormatBytes(scan.JournaledBytes)}）是上次没有下完、" +
                    "可以在「下载管理」里继续的下载。\n\n" +
                    "清理后这部分需要重新下载（进度归零）。\n\n" +
                    $"其余 {scan.UnownedCount} 个是无人认领的临时文件，可以直接删除。\n\n" +
                    "确定要清理全部残留吗？",
                    "清理残留下载文件", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes)
                {
                    ResidueStatusText = "已取消清理（未删除任何文件）。";
                    return;
                }
            }

            Status = "正在清理下载残留…";
            var result = await Task.Run(() => DownloadResidue.Clean(scan));
            var text = result.AnythingDeleted
                ? $"已清理 {result.DeletedCount} 个残留文件，释放 {JobProgressPolicy.FormatBytes(result.FreedBytes)}"
                : "没有删除任何文件";
            if (result.KeptCount > 0)
                text += $"；保留 {result.KeptCount} 个正在下载的文件（{JobProgressPolicy.FormatBytes(result.KeptBytes)}）";
            if (result.Failures.Count > 0)
                text += $"；{result.Failures.Count} 个文件被占用，无法删除（关闭游戏后重试）";

            ResidueStatusText = text;
            Status = text;
            // The journal's entries point at files that no longer exist; replaying
            // it would resurrect a banner about downloads that are gone.
            ResidueNoticeText = "";
            HasResidueNotice = false;
            _residueNoticeDismissed = false;

            var after = await Task.Run(() => DownloadResidue.Scan(LauncherConfig.Instance.RootDirectory));
            _residueScan = after;
            if (!after.IsEmpty) ResidueStatusText += $"；仍剩 {after.ReclaimableCount} 个（约 {JobProgressPolicy.FormatBytes(after.ReclaimableBytes)}）";
            _ = RefreshPendingDownloadsAfterResidueAsync();
        }
        catch (Exception ex)
        {
            ResidueStatusText = "清理失败：" + ex.Message;
            Status = ResidueStatusText;
            Logger.Instance.Warn("Download", "清理下载残留失败: " + ex);
        }
        finally
        {
            IsCleaningResidue = false;
            CleanResidueCommand?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>After a cleanup the resume banner must not offer files we just
    /// deleted. Re-runs the journal replay (it drops entries whose .part is gone).</summary>
    private async Task RefreshPendingDownloadsAfterResidueAsync()
    {
        try
        {
            var list = await Task.Run(() =>
            {
                DownloadJournal.Flush(400);
                return DownloadJournal.Load(LauncherConfig.Instance.RootDirectory);
            });
            Dispatch(() => ApplyPendingDownloads(list));
        }
        catch (Exception ex)
        {
            Logger.Instance.Trace("Download", "清理后刷新未完成下载列表失败（忽略）: " + ex.Message);
        }
    }

    /// <summary>Cheap accessor for tests/diagnostics.</summary>
    internal ResidueScan? LastResidueScan => _residueScan;
}
