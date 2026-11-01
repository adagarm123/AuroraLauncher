using System.Windows;
using System.Windows.Media;
using AuroraLauncher.Desktop.ViewModels;

namespace AuroraLauncher.Desktop;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        // Sync the actual WPF theme resources with the persisted preference.
        if (DataContext is MainViewModel vm)
            vm.ApplyTheme();
        // Round-51: Card styles use BitmapCache (scroll smoothness for the
        // software-rendered DropShadow). A cached texture does not track DPI
        // changes on its own — re-scale every cache when the user moves the
        // window between monitors / changes display scaling, otherwise cards
        // and shadows go blurry. (Theme switches need no handling: the
        // DynamicResource brush change re-renders the element and the cache
        // regenerates automatically.)
        DpiChanged += (_, e) => UpdateCacheScales(e.NewDpi.DpiScaleX);
    }

    private void UpdateCacheScales(double scale)
    {
        UpdateCacheScalesRecursive(this, scale);
    }

    private static void UpdateCacheScalesRecursive(DependencyObject node, double scale)
    {
        if (node is UIElement { CacheMode: BitmapCache bc })
            bc.RenderAtScale = scale;
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
            UpdateCacheScalesRecursive(VisualTreeHelper.GetChild(node, i), scale);
    }
}
