using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AuroraLauncher.Desktop.ViewModels;

/// <summary>An ObservableCollection that can swap its whole contents with ONE
/// Reset notification. The old "Clear + N times Add" pattern raised N layout
/// passes for a 103-item version list on the UI thread; combined with repeated
/// page-Loaded triggers that was the round-63 "未响应" freeze. Replace() makes
/// the ListBox realize its items once.
///
/// SCOPE / BOUNDARY (round-64): a Reset DROPS SelectedItem. Safe for
/// collections WITHOUT selection semantics (ModSearchResults, JavaList).
/// Versions DOES bind SelectedItem — its caller must restore the selection by
/// a stable key via SelectionPolicy.RestoreByKey (see LoadVersionsAsync).
/// If a future collection with selection uses Replace(), do the same.</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void Replace(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
