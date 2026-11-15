using System.Windows.Controls;

namespace AuroraLauncher.Desktop.Views;

/// <summary>Round-91: the home page scrolls when the window is short.
///
/// No code-behind needed: <c>CardArea.MinHeight</c> is bound to the ScrollViewer's
/// <c>ActualHeight</c> in XAML. That reproduces the old "cards fill the window"
/// look when there is room, and is a no-op (a floor only) when the content is
/// taller. On a window where the content just fits, the content height equals the
/// viewport exactly — so no scrollbar appears and there is no slack.
/// </summary>
public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();
}
