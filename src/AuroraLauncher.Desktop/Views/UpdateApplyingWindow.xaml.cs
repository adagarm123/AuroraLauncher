using System.Windows;
using AuroraCore.Services;

namespace AuroraLauncher.Desktop.Views;

/// <summary>Borderless progress window shown by the updater HOST for the
/// entire swap. Deliberately has no close button while working: closing it
/// would leave a half-copied exe. On failure it becomes an actionable error
/// dialog (retry / close) instead of disappearing silently.</summary>
public partial class UpdateApplyingWindow : Window
{
    private readonly UpdateApplyPlan _plan;
    private bool _running;

    public UpdateApplyingWindow(UpdateApplyPlan plan)
    {
        InitializeComponent();
        _plan = plan;
        Loaded += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        if (_running) return;
        _running = true;
        ErrorPanel.Visibility = Visibility.Collapsed;
        StageText.Text = "正在等待旧版本退出…";
        Progress.Value = 8;

        void Report(string text, int pct) =>
            Application.Current?.Dispatcher.Invoke(() =>
            {
                StageText.Text = text;
                Progress.Value = pct;
            });

        try
        {
            await UpdateApplier.ApplyAsync(_plan, Report).ConfigureAwait(true);
            StageText.Text = "更新完成，启动器正在重新启动…";
            Progress.Value = 100;
            await Task.Delay(700).ConfigureAwait(true);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            StageText.Text = "更新未能自动完成";
            ErrorText.Text = ex.Message +
                "\n\n可点「重试」再试一次；若反复失败，请手动用启动器 update 文件夹里的新版本替换旧文件。";
            ErrorPanel.Visibility = Visibility.Visible;
            _running = false;
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await RunAsync();

    private void Close_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
