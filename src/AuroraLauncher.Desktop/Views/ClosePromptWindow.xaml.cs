using System.Windows;
using AuroraCore.Services;

namespace AuroraLauncher.Desktop.Views;

/// <summary>What the user answered in the close prompt.</summary>
public enum CloseChoice { Cancel, Exit, HideToTray }

/// <summary>
/// Round-74: the round-73 MessageBox could only say yes/no. Choosing between
/// "退出" and "最小化到托盘" — and remembering that choice — needs a real dialog
/// with a checkbox, so the policy lives in Core and the UI is just a view of it.
/// </summary>
public partial class ClosePromptWindow : Window
{
    public CloseChoice Choice { get; private set; } = CloseChoice.Cancel;

    /// <summary>"记住我的选择" — the caller persists it as
    /// <see cref="LauncherConfig.CloseAction"/> (Ask never gets persisted this
    /// way: rerunning the dialog would be pointless).</summary>
    public bool Remember { get; private set; }

    public ClosePromptWindow(CloseDecision decision)
    {
        InitializeComponent();
        MessageText.Text = CloseBehaviorPolicy.BuildMessage(decision);
        // Hiding is only an option when a tray icon actually exists.
        if (!decision.OfferTray) HideButton.Visibility = Visibility.Collapsed;
        RememberCheck.IsChecked = false;
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.Exit;
        Remember = RememberCheck.IsChecked == true;
        DialogResult = true;
    }

    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.HideToTray;
        Remember = RememberCheck.IsChecked == true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.Cancel;
        DialogResult = false;
    }
}
