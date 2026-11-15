namespace AuroraCore.Services;

/// <summary>Pure helper for restoring a ListBox/ComboBox selection after a
/// bulk Reset (round-64). ObservableCollection.Reset drops SelectedItem, so
/// any collection with selection semantics must re-select by a stable key.
/// Collections WITHOUT selection (Modrinth search results, Java list, etc.)
/// need no restoration — see docs in BulkObservableCollection.</summary>
public static class SelectionPolicy
{
    public static T? RestoreByKey<T>(IEnumerable<T> items, Func<T, string> keyOf, string? previousKey)
        where T : class
    {
        var list = items as IList<T> ?? items.ToList();
        if (!string.IsNullOrEmpty(previousKey))
        {
            var match = list.FirstOrDefault(i =>
                string.Equals(keyOf(i), previousKey, StringComparison.Ordinal));
            if (match != null) return match;
        }
        return list.FirstOrDefault();
    }
}

/// <summary>Guard at the debounce timer's callback (round-64). A timer that
/// fires after the user switched pages or picked another version must NOT
/// start a stale 4-route loader fetch.</summary>
public static class LoaderDebouncePolicy
{
    public static bool ShouldFire(string currentPageKey, string targetVersion, string currentVersion) =>
        string.Equals(currentPageKey, "Download", StringComparison.Ordinal)
        && targetVersion.Length > 0
        && string.Equals(targetVersion, currentVersion, StringComparison.Ordinal);
}
