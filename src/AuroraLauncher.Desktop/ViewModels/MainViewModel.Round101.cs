using System.Windows;
using AuroraCore;
using AuroraCore.Services;
using AuroraLauncher.Desktop.Views;
using AuroraLauncher.ViewModels;

namespace AuroraLauncher.Desktop.ViewModels;

public partial class MainViewModel
{
    // ==================== 第 101 轮：更新下载可见、可取消 ====================
    private UpdateDownloadWindow? _updateWindow;

    public string UpdateWindowTitle { get; private set; } = "正在下载启动器更新";

    private bool _isUpdateDownloadActive;
    public bool IsUpdateDownloadActive
    {
        get => _isUpdateDownloadActive;
        private set
        {
            if (SetProperty(ref _isUpdateDownloadActive, value))
            {
                OnPropertyChanged(nameof(CanCancelUpdate));
                CancelUpdateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private bool _updateCancelRequested;
    public bool CanCancelUpdate => UpdateUiPolicy.CanCancel(IsUpdateDownloadActive, _updateCancelRequested);

    /// <summary>Async command with its own gate: the button greys out while
    /// the cancel is being delivered, so a double click cannot race itself.</summary>
    public RelayCommand CancelUpdateCommand { get; }

    public void PrepareUpdateWindow(UpdateManifest info)
    {
        _updateCancelRequested = false;
        UpdateWindowTitle = UpdateUiPolicy.DownloadTitle(info.Round);
        OnPropertyChanged(nameof(UpdateWindowTitle));
        IsUpdateDownloadActive = true;
        UpdateProgressIndeterminate = false;
        UpdateProgress = 0;
        UpdateStatusText = "正在下载更新…";
        Status = UpdateStatusText;

        try
        {
            _updateWindow = new UpdateDownloadWindow(this);
            if (Application.Current.MainWindow is { IsLoaded: true } main) _updateWindow.Owner = main;
            _updateWindow.Show();
        }
        catch (Exception ex)
        {
            // Never let a window failure block the update itself.
            AddLog("[Update] 进度窗口显示失败，仍继续下载: " + ex.Message);
            _updateWindow = null;
        }
    }

    public void CloseUpdateWindow()
    {
        IsUpdateDownloadActive = false;
        OnPropertyChanged(nameof(CanCancelUpdate));
        CancelUpdateCommand.RaiseCanExecuteChanged();
        var window = _updateWindow;
        _updateWindow = null;
        try { window?.AllowClose(); } catch { /* window already closed */ }
    }

    /// <summary>Cancel goes through the SAME token the download engine uses.
    /// Cleanup of partial files happens in DownloadUpdateAsync's cancellation
    /// path, where we still know whether an older ready update existed.</summary>
    public void CancelUpdate()
    {
        if (!CanCancelUpdate) return;
        _updateCancelRequested = true;
        OnPropertyChanged(nameof(CanCancelUpdate));
        CancelUpdateCommand.RaiseCanExecuteChanged();
        _updateCts?.Cancel();
        UpdateStatusText = "正在取消更新…";
        Status = UpdateStatusText;
        AddLog("[Update] 用户取消下载更新");
    }

    // ==================== 第 101 轮：下载前先选文件放哪儿 ====================
    /// <summary>Mirrors the portable checkbox for the wizard's radio pair;
    /// both directions notify so the two choices stay one state.</summary>
    public bool FirstRunUseAppData
    {
        get => !FirstRunPortable;
        set
        {
            if (!value) return;
            FirstRunPortable = false;
            OnPropertyChanged(nameof(FirstRunUseAppData));
            OnPropertyChanged(nameof(FirstRunStorageSummary));
        }
    }

    public bool FirstRunUseExeDir
    {
        get => FirstRunPortable;
        set
        {
            if (!value) return;
            FirstRunPortable = true;
            OnPropertyChanged(nameof(FirstRunUseExeDir));
            OnPropertyChanged(nameof(FirstRunStorageSummary));
        }
    }

    public string FirstRunStorageSummary => FirstRunStoragePolicy.Describe(
        FirstRunStoragePolicy.FromPortable(FirstRunPortable),
        LauncherConfig.Instance.RootDirectory,
        Environment.ProcessPath ?? "");

    /// <summary>Step 4/4 exists as a real state so "一键安装" is unambiguously
    /// the step AFTER the storage choice, never a hidden side effect.</summary>
    public bool FirstRunStep3Visible => FirstRunStep == 3;

    /// <summary>Step 1/3 → the new storage step. Nothing is applied yet.</summary>
    public void GoToFirstRunStorageStep()
    {
        FirstRunStep = 1;
        OnPropertyChanged(nameof(FirstRunStorageSummary));
    }

    /// <summary>Called by the existing first-run "下一步" command on the
    /// storage step: apply the explicit choice, then move to the download
    /// step. The marker is written here, BEFORE any game file is downloaded.</summary>
    public void ApplyFirstRunStorageChoice()
    {
        // The existing first-run core owns the actual marker write; this is
        // just the named hook for the new storage step's button.
        OnFirstRunNext();
        OnPropertyChanged(nameof(FirstRunStorageSummary));
    }

    public void MarkFirstRunDoneStep()
    {
        FirstRunStep = 3;
    }
}
