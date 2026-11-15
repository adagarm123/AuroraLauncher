namespace AuroraCore.Services;

/// <summary>Pure navigation decisions (round-63). Page instances are cached,
/// but assigning Content again STILL raises Loaded/Unloaded, so clicking the
/// already-active sidebar item must not re-enter the whole navigation path.
/// Kept in AuroraCore so the rule is unit-testable without WPF.</summary>
public static class NavigationPolicy
{
    public static string Normalize(string? key) =>
        string.IsNullOrWhiteSpace(key) ? "Home" : key;

    /// <summary>First navigation always proceeds (no page yet); afterwards a
    /// repeat request for the current key is a no-op.
    ///
    /// KEY GRANULARITY (round-64): <paramref name="normalizedKey"/> is a PAGE
    /// identifier only ("Mods", "Download", ...), never "page + sub-argument".
    /// Sub-pages of the content centre are switched by SelectContentKindCommand
    /// and never go through Navigate, so they cannot be swallowed by the
    /// early-return. If a parameterized navigation is ever added, pass the
    /// sub-parameter via the two subKey arguments — otherwise two different
    /// sub-views would look like "the same page" and be incorrectly ignored.
    /// </summary>
    public static bool ShouldNavigate(string? currentKey, string normalizedKey, bool hasCurrentPage,
        string? currentSubKey = null, string? requestedSubKey = null)
    {
        if (!hasCurrentPage) return true;
        if (!string.Equals(currentKey, normalizedKey, StringComparison.Ordinal)) return true;
        return !string.Equals(currentSubKey ?? "", requestedSubKey ?? "", StringComparison.Ordinal);
    }
}
