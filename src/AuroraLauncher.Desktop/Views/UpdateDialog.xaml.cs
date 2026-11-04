using System.Windows;
using AuroraCore.Services;

namespace AuroraLauncher.Desktop.Views;

/// <summary>Startup update reminder (round-58). Exactly two choices —
/// 「立即更新」 starts the update, 「关闭」 dismisses it. No third
/// "ask me later" / "skip this version" button by design.</summary>
public partial class UpdateDialog : Window
{
    public UpdateDialog(UpdateManifest info)
    {
        InitializeComponent();
        VersionText.Text = $"第 {info.Round} 轮 · v{info.Version}" +
                           (string.IsNullOrWhiteSpace(info.ReleaseTime) ? "" : $" · {info.ReleaseTime}") +
                           $"（当前第 {AuroraCore.BuildInfo.CurrentRound} 轮）";
        NotesText.Text = string.IsNullOrWhiteSpace(info.Notes)
            ? "本次更新包含稳定性与体验改进，建议及时更新。\n\n点击「立即更新」会开始下载，完成后启动器自动重启；点击「关闭」则本次不更新。"
            : info.Notes + "\n\n点击「立即更新」会开始下载，完成后启动器自动重启。";
    }

    /// <summary>True when the user chose 「立即更新」.</summary>
    public bool UpdateNow { get; private set; }

    private void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        UpdateNow = true;
        DialogResult = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        UpdateNow = false;
        DialogResult = false;
        Close();
    }
}
