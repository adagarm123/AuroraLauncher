namespace AuroraCore.Services;

/// <summary>Page-host contract (round-65) — the real fix for the repeated
/// "未响应" freeze. Round-55 cached UserControl instances but still assigned
/// them to ContentControl.Content on every click; WPF destroys a control's
/// VISUAL TREE when it is detached, so every navigation rebuilt 103 version
/// rows, dozens of changelog cards and every Card's BitmapCache/DropShadow
/// texture. Round-63/64 removed duplicate async init but could not remove
/// that synchronous render cost.
///
/// Correct model: create each page lazily ONCE, keep it in the visual tree
/// forever, and flip only Visibility. Navigation becomes O(1).</summary>
public static class PageHostPolicy
{
    /// <summary>True only when this page has never been created.</summary>
    public static bool ShouldCreate(IEnumerable<string> createdKeys, string key) =>
        !createdKeys.Contains(key, StringComparer.Ordinal);

    /// <summary>Exactly one page is Visible; every other page is Collapsed
    /// (Collapsed elements are skipped by Measure/Arrange/Render).</summary>
    public static bool IsVisible(string activeKey, string key) =>
        string.Equals(activeKey, key, StringComparison.Ordinal);
}
