using System.Windows.Controls;
using System.Windows.Threading;
using AuroraLauncher.Desktop.ViewModels;

namespace AuroraLauncher.Desktop.Views;

public partial class DownloadView : UserControl
{
    public DownloadView()
    {
        InitializeComponent();
        // Round-63: page instances are cached, so init MUST be single-shot;
        // rapid page switching used to start several manifest fetches + four
        // loader-route batches at once.
        // Round-68: the trigger is "first actually shown", not "first attached"
        // — pages are pre-warmed during idle, and a warm-up must never kick off
        // a network fetch for a page nobody opened.
        PageLifecycle.OnFirstShown(this, () =>
        {
            if (DataContext is not MainViewModel vm) return;
            // Cheap, idempotent, NO network by itself: if the previous
            // debounce was cancelled when the user left this page, re-arm it
            // so the cards never stay at「加载中…」forever.
            vm.EnsureLoaderCardsRefresh();
            // Background priority: let the page finish its layout/render pass
            // before any network work queues up on the dispatcher.
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (vm.Versions.Count == 0)
                    await vm.LoadVersionsAsync();
                if (vm.LoaderCards.Count == 0)
                    await vm.LoadLoaderCardsAsync();
            }), DispatcherPriority.Background);
        });
    }
}
