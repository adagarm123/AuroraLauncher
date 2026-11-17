using System.ComponentModel;
using System.Windows;
using AuroraCore.Services;
using AuroraLauncher.Desktop.ViewModels;

namespace AuroraLauncher.Desktop.Views;

/// <summary>Round-101: visible update-download progress with a real cancel.
/// Closing the window means cancel too — the VM owns the token, this window
/// only makes the choice obvious and keeps both paths in sync.</summary>
public partial class UpdateDownloadWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _allowClose;

    public UpdateDownloadWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        HintText.Text = UpdateUiPolicy.ApplyStageHint;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose) _vm.CancelUpdate();
        base.OnClosing(e);
    }

    /// <summary>Called by the VM after the download has finished or failed,
    /// so the window never cancels work that already ended.</summary>
    public void AllowClose()
    {
        _allowClose = true;
        Close();
    }
}
