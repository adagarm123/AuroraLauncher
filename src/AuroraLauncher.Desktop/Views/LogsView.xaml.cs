using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using AuroraLauncher.Desktop.ViewModels;

namespace AuroraLauncher.Desktop.Views;

public partial class LogsView : UserControl
{
    private MainViewModel? _vm;
    private bool _autoScroll = true;

    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookVm(DataContext as MainViewModel);
        Unloaded += (_, _) => HookVm(null);
    }

    private void HookVm(MainViewModel? vm)
    {
        if (_vm == vm) return;
        if (_vm != null) _vm.Logs.CollectionChanged -= Logs_CollectionChanged;
        _vm = vm;
        if (_vm != null) _vm.Logs.CollectionChanged += Logs_CollectionChanged;
    }

    /// <summary>Auto-scroll to the newest line (unless the user scrolled up to
    /// read something — scrolling away pauses following until they return to
    /// the bottom).</summary>
    private void Logs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || LogList.Items.Count == 0) return;
        if (!_autoScroll) return;
        LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private void ErrorsOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (_vm != null) _vm.ShowErrorsOnly = ErrorsOnlyToggle.IsChecked == true;
    }

    private void LogList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Near the bottom → keep following new lines; scrolled away → pause.
        var sv = e.OriginalSource as ScrollViewer;
        if (sv == null) return;
        _autoScroll = sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 24;
    }
}
