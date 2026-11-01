using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AuroraLauncher.Desktop.ViewModels;

namespace AuroraLauncher.Desktop.Views;

public partial class ModsView : UserControl
{
    public ModsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookVm(DataContext as MainViewModel);
        Loaded += async (_, _) =>
        {
            HookVm(DataContext as MainViewModel);
            if (DataContext is MainViewModel vm && vm.ModSearchResults.Count == 0 && vm.ContentKind != "本地管理")
                await vm.SearchContentAsync();
        };
        Unloaded += (_, _) => HookVm(null);
    }

    private MainViewModel? _hooked;

    private void HookVm(MainViewModel? vm)
    {
        if (_hooked == vm) return;
        if (_hooked != null) _hooked.PropertyChanged -= VmPropertyChanged;
        _hooked = vm;
        if (vm != null) vm.PropertyChanged += VmPropertyChanged;
    }

    private void VmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // When the detail page opens, auto-scroll to the first group that
        // contains a version compatible with the selected instance.
        if (e.PropertyName == nameof(MainViewModel.ShowContentDetail)
            && sender is MainViewModel vm && vm.ShowContentDetail)
        {
            Dispatcher.BeginInvoke(ScrollToCompatibleGroup, DispatcherPriority.Loaded);
        }
    }

    private void ScrollToCompatibleGroup()
    {
        if (DataContext is not MainViewModel vm) return;
        if (!vm.ShowContentDetail || vm.DetailRows.Count == 0) return;

        var idx = vm.DetailScrollTargetIndex;
        if (idx < 0 || idx >= DetailList.Items.Count)
        {
            FindScroll(DetailList)?.ScrollToTop();
            return;
        }

        // Realize containers around the anchor first.
        DetailList.ScrollIntoView(DetailList.Items[idx]);
        DetailList.UpdateLayout();
        DetailList.ScrollIntoView(DetailList.Items[idx]);
        DetailList.UpdateLayout();

        // Prefer anchoring one row higher when it is the group header, so the
        // "Minecraft X" context stays visible above the installable version.
        int anchorIdx = idx;
        if (idx > 0 && DetailList.Items[idx - 1] is ViewModels.ContentGroupHeaderVM)
            anchorIdx = idx - 1;

        // ScrollIntoView only guarantees the item is *visible* — when coming
        // from above it lands at the BOTTOM edge of the viewport. Compute the
        // anchor's actual position and bring it to the TOP instead.
        var container = DetailList.ItemContainerGenerator.ContainerFromIndex(anchorIdx) as FrameworkElement;
        var sv = FindScroll(DetailList);
        if (container == null || sv == null) return;

        var pos = container.TransformToVisual(sv).Transform(new Point(0, 0));
        var target = sv.VerticalOffset + pos.Y - 12; // 12px breathing room
        sv.ScrollToVerticalOffset(Math.Max(0, target));
    }

    /// <summary>Click handler for the "定位适配版本" button.</summary>
    private void OnJumpToCompatible(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (!vm.ShowContentDetail) return;
        Dispatcher.BeginInvoke(ScrollToCompatibleGroup, DispatcherPriority.Loaded);
    }

    private static System.Windows.Controls.ScrollViewer? FindScroll(DependencyObject d)
    {
        if (d is System.Windows.Controls.ScrollViewer sv) return sv;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var found = FindScroll(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
            if (found != null) return found;
        }
        return null;
    }
}
