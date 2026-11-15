using System.Windows;

namespace AuroraLauncher.Desktop.Views;

/// <summary>
/// Round-80: red performance gate shown BEFORE any glass mode is enabled.
/// The text comes from <c>GlassEffectPolicy</c> so the warning and the policy
/// cannot drift apart.
/// </summary>
public partial class GlassWarningWindow : Window
{
    /// <summary>True only when the user explicitly accepted the risk.</summary>
    public bool Confirmed { get; private set; }

    public GlassWarningWindow(string title, string body)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = body;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
    }
}
