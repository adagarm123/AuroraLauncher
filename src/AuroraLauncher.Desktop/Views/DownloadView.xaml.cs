using System.Windows.Controls;
using AuroraLauncher.Desktop.ViewModels;

namespace AuroraLauncher.Desktop.Views;

public partial class DownloadView : UserControl
{
    public DownloadView()
    {
        InitializeComponent();
        // Auto-load on page open: official version list (once) + loader cards.
        Loaded += async (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            if (vm.Versions.Count == 0)
                await vm.LoadVersionsAsync();
            if (vm.LoaderCards.Count == 0)
                await vm.LoadLoaderCardsAsync();
        };
    }
}
