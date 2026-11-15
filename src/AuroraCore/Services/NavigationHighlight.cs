namespace AuroraCore.Services;

/// <summary>
/// Round-96: navigation highlight has exactly ONE input — CurrentPageKey.
/// The screenshot showed two lavender items; the converter was already driven
/// solely by that key, so the second tint was the mouse-over brush, not a
/// second IsSelected. This pure rule is shared by the background and the new
/// active-marker converters, and a guard locks every nav button to it.
/// </summary>
public static class NavigationHighlight
{
    public static bool IsActive(string? currentPageKey, string? candidateKey)
        => !string.IsNullOrWhiteSpace(currentPageKey) &&
           !string.IsNullOrWhiteSpace(candidateKey) &&
           string.Equals(currentPageKey, candidateKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>With distinct keys this is always 0 or 1 — never two.</summary>
    public static int ActiveCount(string? currentPageKey, IEnumerable<string> keys)
        => keys.Count(k => IsActive(currentPageKey, k));
}
