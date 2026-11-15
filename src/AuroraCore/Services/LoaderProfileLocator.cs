namespace AuroraCore.Services;

/// <summary>
/// Round-96: adopt an ALREADY INSTALLED loader version directory.
///
/// The round-95 "success must produce a real profile" check only looked at
/// directories CREATED by the current installer run. But a directory from an
/// earlier install is already in `existing`, so `detected` came back null even
/// though `versions/1.20.1-forge-47.4.22/…json` was sitting right there and
/// had just been rewritten (exit 0). Users whose instance still held a stale
/// hint id ("forge-1.20.1-47.4.22") were then told the install had failed.
///
/// Rules: the directory must carry the MC version, the loader name and (when
/// known) the loader version token, AND must contain a real version JSON.
/// Never guess from a bare directory name.
/// </summary>
public static class LoaderProfileLocator
{
    /// <summary>The trailing version token of an installer URL hint, e.g.
    /// "forge-1.20.1-47.4.22" → "47.4.22".</summary>
    public static string? VersionToken(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return null;
        var parts = hint.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var last = parts.Length == 0 ? "" : parts[^1].Trim();
        return last.Length > 0 && char.IsDigit(last[0]) && last.Contains('.') ? last : null;
    }

    public static bool Matches(string dirName, string loaderName, string? versionHint, string mcVersion)
    {
        if (string.IsNullOrWhiteSpace(dirName)) return false;
        if (!string.IsNullOrEmpty(mcVersion) &&
            !dirName.Contains(mcVersion, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(loaderName) &&
            !dirName.Contains(loaderName, StringComparison.OrdinalIgnoreCase)) return false;
        var token = VersionToken(versionHint);
        if (!string.IsNullOrEmpty(token) &&
            !dirName.Contains(token, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>Picks the best existing profile directory. <paramref name="hasProfile"/>
    /// must verify that &lt;dir&gt;/&lt;dir&gt;.json really exists — an empty
    /// shell left by a broken install is not an installed loader.</summary>
    public static string? Pick(IEnumerable<string> dirNames, string loaderName,
        string? versionHint, string mcVersion, Func<string, bool> hasProfile)
    {
        if (dirNames == null) return null;
        var token = VersionToken(versionHint);
        return dirNames
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Where(d => Matches(d, loaderName, versionHint, mcVersion))
            .Where(hasProfile)
            .OrderByDescending(d => !string.IsNullOrEmpty(token) &&
                                    d.Contains(token, StringComparison.OrdinalIgnoreCase))
            .ThenBy(d => d.Length)
            .ThenBy(d => d, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}
