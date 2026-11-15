namespace AuroraCore.Services;

/// <summary>Round-68: idle-time page pre-warm policy.
///
/// The layout benchmark proved a *warm* page flip costs ~0.3 ms, while the
/// FIRST build + first show of a page costs 5–60 ms (BAML load, template
/// application, DynamicResource resolution, first measure/arrange). Since
/// round-65 pages are permanent, that cold cost is paid exactly once per page —
/// precisely the cost a user feels as "第一次点这页有点卡".
///
/// Strategy: after startup settles, build the remaining pages one per idle
/// dispatcher tick and pre-lay them out as Visibility.Hidden (Hidden still
/// measures/arranges; Collapsed does not), then park them Collapsed. The first
/// real click is then a warm flip.
///
/// Rules kept in this pure class so they are testable:
/// - never warm while the launcher is busy (install/download) — stealing the
///   UI thread from progress reporting is exactly the "卡顿" we are fixing;
/// - never warm during shutdown;
/// - always warm in sidebar order (Home first) so the most likely next click
///   is covered first.</summary>
public static class PageWarmupPolicy
{
    /// <summary>Every page the shell can show, in sidebar order.</summary>
    public static readonly string[] PageOrder =
    {
        "Home", "Download", "Instances", "Mods", "Downloads",
        "Settings", "Logs", "Changelog", "Help"
    };

    /// <summary>Next page worth building: the first one in sidebar order that
    /// has not been created yet. Null when every page is already built.</summary>
    public static string? NextToWarm(IEnumerable<string> createdKeys)
    {
        var created = createdKeys as ICollection<string> ?? createdKeys.ToList();
        foreach (var key in PageOrder)
            if (!created.Contains(key)) return key;
        return null;
    }

    /// <summary>Idle work must never compete with the user's own work.</summary>
    public static bool ShouldWarmNow(bool busy, bool downloadActive, bool shuttingDown) =>
        !busy && !downloadActive && !shuttingDown;
}
