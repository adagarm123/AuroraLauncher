using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AuroraLauncher.Desktop.ViewModels;

namespace AuroraLauncher.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is MainViewModel vm && vm.JavaList.Count == 0)
                await vm.LoadJavaListAsync();
        };
    }

    /// <summary>Round-49 scroll fix: the Java ListBox (MaxHeight 220) has its
    /// own ScrollViewer, which swallows mouse-wheel events when the cursor is
    /// over it — the page would "stick" until the inner list hit its edge,
    /// then jump. Forward the wheel to the page-level ScrollViewer whenever
    /// the inner list cannot scroll further in that direction, so scrolling
    /// across the whole page is continuous.</summary>
    private void JavaList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ListBox lb || e.Handled) return;
        var inner = FindInnerScrollViewer(lb);
        var page = FindAncestorScrollViewer(this);
        if (inner == null || page == null) return;

        var atTop = inner.VerticalOffset <= 0.1;
        var atBottom = inner.VerticalOffset >= inner.ScrollableHeight - 0.1;
        var scrollingUp = e.Delta > 0;

        // Inner list can absorb the scroll → let it (normal case).
        if ((scrollingUp && !atTop) || (!scrollingUp && !atBottom)) return;

        // Inner list is at its edge → hand the event to the page scroller.
        e.Handled = true;
        var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = this
        };
        page.RaiseEvent(forwarded);
    }

    private static ScrollViewer? FindInnerScrollViewer(DependencyObject root, int depth = 0)
    {
        if (root is ScrollViewer sv) return sv;
        if (depth > 8) return null;
        var n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var found = FindInnerScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(root, i), depth + 1);
            if (found != null) return found;
        }
        return null;
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject start)
    {
        var d = System.Windows.Media.VisualTreeHelper.GetParent(start);
        while (d != null)
        {
            if (d is ScrollViewer sv) return sv;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }
}
