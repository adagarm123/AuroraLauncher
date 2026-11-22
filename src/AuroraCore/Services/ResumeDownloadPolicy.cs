using AuroraCore.Utils;

namespace AuroraCore.Services;

/// <summary>
/// Round-103: a download journal written by an older build can contain a dead
/// mirror host. Re-enqueuing that URL verbatim makes "retry" fail forever.
/// Normalise to the official URL first, then apply the CURRENT route choice.
/// </summary>
public static class ResumeDownloadPolicy
{
    public static string ChooseUrl(string journalUrl, DownloadPolicy policy, bool preferMirror)
    {
        if (string.IsNullOrWhiteSpace(journalUrl)) return journalUrl;
        var official = LauncherConfig.ToOfficialUrl(journalUrl);
        return policy switch
        {
            DownloadPolicy.Official => official,
            DownloadPolicy.Mirror => LauncherConfig.ToBmclapiUrl(official),
            _ => preferMirror ? LauncherConfig.ToBmclapiUrl(official) : official
        };
    }

    /// <summary>Would this journal entry be sent to a retired mirror host?</summary>
    public static bool UsesLegacyMirror(string journalUrl)
        => journalUrl.Contains(LauncherConfig.LegacyMirrorHost, StringComparison.OrdinalIgnoreCase) ||
           journalUrl.Contains(LauncherConfig.LegacyMirrorHost2, StringComparison.OrdinalIgnoreCase);
}
