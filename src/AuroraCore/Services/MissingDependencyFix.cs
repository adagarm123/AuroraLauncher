using AuroraCore.Models;

namespace AuroraCore.Services;

/// <summary>Round-100: how one attempted auto-fix ended.</summary>
public enum AutoFixStatus
{
    /// <summary>Downloaded, hash-checked, and the rescan found the id.</summary>
    Installed,
    /// <summary>The rescan found the id without us downloading anything.</summary>
    AlreadyPresent,
    /// <summary>Modrinth has no project with this slug/id.</summary>
    NotFound,
    /// <summary>Project exists, but no file fits this loader / MC version / range.</summary>
    NoMatchingVersion,
    /// <summary>Best file has no SHA-1 or size — refused on purpose.</summary>
    Unverifiable,
    /// <summary>Network / disk / hash failure (detail carries the reason).</summary>
    DownloadFailed,
    /// <summary>Bytes arrived, but a fresh scan still does not see the id.</summary>
    NotRecognized
}

/// <summary>One prerequisite the user can fix with a click.</summary>
public sealed record AutoFixCandidate(
    string ModId,
    IReadOnlyList<string> Ranges,
    IReadOnlyList<string> RequiredBy,
    string Slug,
    bool KnownSlug)
{
    public string RangeText => Ranges.Count == 0 ? "任意版本" : string.Join(" 且 ", Ranges);
    public string RequiredByText => RequiredBy.Count == 0 ? "未知来源" : string.Join("、", RequiredBy.Take(3))
        + (RequiredBy.Count > 3 ? $" 等 {RequiredBy.Count} 个模组" : "");
}

/// <summary>The verdict for one candidate. Never thrown — one failure must not
/// abort the rest of the batch.</summary>
public sealed record AutoFixResult(string ModId, string Slug, AutoFixStatus Status,
    string FileName = "", string Detail = "")
{
    public bool Ok => Status is AutoFixStatus.Installed or AutoFixStatus.AlreadyPresent;
}

/// <summary>
/// Round-100: "一键补齐前置". Round-97 fixed the parser and left this to the
/// next round on purpose (the user explicitly asked to fix the scan first).
///
/// Two rules make this safe enough to run unattended:
///  1. <b>Nothing is downloaded without a checksum.</b> A prerequisite that
///     cannot be verified is reported, never installed — a wrong or corrupted
///     jar in mods/ is worse than a missing one (the game then blames a
///     random crash on it).
///  2. <b>"Installed" is only claimed after a fresh scan</b> sees the id.
///     Downloading a jar is not proof that the loader will accept it; the
///     rescan is the single source of truth (see <see cref="AutoFixSession"/>).
///
/// Everything here is pure logic: the VM supplies search / download delegates,
/// so the whole decision table is unit-testable offline.
/// </summary>
public static class MissingDependencyFix
{
    /// <summary>What the rescan must be able to do before anything is claimed.</summary>
    public static bool CanVerify(ModrinthVersion version)
        => version.FileSize > 0 && !string.IsNullOrWhiteSpace(version.Sha1);

    /// <summary>Turns a preflight report into the checklist the UI shows.
    /// Ordered by "how many mods need it" so the biggest win is on top.</summary>
    public static IReadOnlyList<AutoFixCandidate> Plan(ModPreflight.Report report)
        => report.Missing
            .Where(d => !string.IsNullOrWhiteSpace(d.ModId))
            .Select(d => new AutoFixCandidate(
                d.ModId.Trim(),
                d.Ranges.Count > 0 ? d.Ranges : new[] { d.Range },
                d.RequiredByList.Count > 0 ? d.RequiredByList : new[] { d.RequiredBy },
                PreflightAutoFixPolicy.SlugFor(d.ModId),
                PreflightAutoFixPolicy.HasKnownSlug(d.ModId)))
            .GroupBy(c => c.ModId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(c => c.RequiredBy.Count)
            .ThenBy(c => c.ModId, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Picks the project out of search hits. Exact slug first, then an
    /// exact (whitespace/case-insensitive) title match. Anything looser is
    /// refused: installing a different mod that happens to rank first is the
    /// failure mode this whole feature must never have.</summary>
    public static ModrinthProject? PickProject(AutoFixCandidate candidate, IReadOnlyList<ModrinthProject> hits)
    {
        if (hits.Count == 0) return null;
        var slug = candidate.Slug.Trim();
        var exact = hits.FirstOrDefault(h => string.Equals(h.Slug, slug, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        var wanted = new[] { Normalize(slug), Normalize(candidate.ModId) };
        return hits.FirstOrDefault(h =>
        {
            var title = Normalize(h.Title);
            return title.Length > 0 && wanted.Contains(title);
        });
    }

    /// <summary>Chooses the file to download. Loader + MC version + every merged
    /// range must agree, and the winner must be verifiable.</summary>
    public static (ModrinthVersion? Version, PickOutcome Outcome) PickVersion(
        AutoFixCandidate candidate, ModLoaderType loader, string mcVersion, IReadOnlyList<ModrinthVersion> versions)
    {
        var dep = new ModDependency(candidate.ModId, "", "", "") { Ranges = candidate.Ranges };
        var eligible = versions.Where(v =>
                PreflightAutoFixPolicy.VersionAllowed(loader, v.Loaders, mcVersion, v.GameVersions) &&
                PreflightAutoFixPolicy.VersionInRange(dep, v.VersionNumber))
            .OrderBy(v => TypeRank(v.VersionType))
            .ThenByDescending(v => v.DatePublished ?? DateTime.MinValue)
            .ThenByDescending(v => v.VersionNumber, Comparer<string>.Create(VersionRangePolicy.Compare))
            .ToList();

        if (eligible.Count == 0) return (null, PickOutcome.NoMatchingVersion);
        var pick = eligible.FirstOrDefault(CanVerify);
        return pick != null ? (pick, PickOutcome.Found) : (null, PickOutcome.Unverifiable);
    }

    public enum PickOutcome { Found, NoMatchingVersion, Unverifiable }

    /// <summary>release &gt; beta &gt; alpha; anything else last.</summary>
    public static int TypeRank(string? versionType) => (versionType ?? "").ToLowerInvariant() switch
    {
        "release" => 0,
        "beta" => 1,
        "alpha" => 2,
        _ => 3
    };

    /// <summary>Chinese one-liner for the result row. Every status has text —
    /// a silent failure would leave the user staring at a spinner.</summary>
    public static string Describe(AutoFixResult result) => result.Status switch
    {
        AutoFixStatus.Installed => $"已安装 {Describe0(result)}",
        AutoFixStatus.AlreadyPresent => "扫描时发现它已经在 mods 里了",
        AutoFixStatus.NotFound => "Modrinth 上没有找到这个前置（可能只在 CurseForge 或作者自己的站点发布）",
        AutoFixStatus.NoMatchingVersion => "找到了项目，但没有适配当前 MC 版本 / 加载器 / 版本范围的文件",
        AutoFixStatus.Unverifiable => "找到的文件没有校验值，为安全起见没有自动下载",
        AutoFixStatus.DownloadFailed => "下载或校验失败" + (result.Detail.Length > 0 ? $"（{result.Detail}）" : ""),
        AutoFixStatus.NotRecognized => "文件已下载，但重扫仍未识别到它（大概率版本不匹配）",
        _ => "未处理"
    };

    private static string Describe0(AutoFixResult result)
        => result.FileName.Length > 0 ? result.FileName : result.ModId;

    /// <summary>Always-available manual exit for anything that did not work.</summary>
    public static string ManualHint(AutoFixCandidate candidate)
        => $"可点「打开 mods 文件夹」手动放入，或在 Modrinth 搜索 {candidate.Slug}" +
           (candidate.Ranges.Count > 0 ? $"（需要版本 {candidate.RangeText}）" : "");

    private static string Normalize(string? text)
        => new((text ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

/// <summary>
/// Round-100: collects one batch of auto-fix outcomes and applies the final
/// honesty rule — "Installed" survives only if the post-download rescan can
/// actually see the mod id. Kept as a separate object so this rule is testable
/// without a network, a game instance, or a WPF thread.
/// </summary>
public sealed class AutoFixSession
{
    private readonly List<AutoFixResult> _recorded = new();
    private IReadOnlyList<AutoFixResult>? _final;

    public AutoFixSession(IEnumerable<AutoFixCandidate> candidates)
        => Candidates = candidates.ToList();

    public IReadOnlyList<AutoFixCandidate> Candidates { get; }

    public void Record(AutoFixResult result) => _recorded.Add(result);

    public void Skip(AutoFixCandidate candidate, string detail = "") =>
        _recorded.Add(new AutoFixResult(candidate.ModId, candidate.Slug, AutoFixStatus.NoMatchingVersion, "", detail));

    /// <summary>Final pass: any "Installed" the rescan cannot see becomes
    /// "NotRecognized" — we never tell the user a prerequisite is fixed when
    /// the loader would still refuse to start.</summary>
    public IReadOnlyList<AutoFixResult> Complete(IReadOnlyCollection<string> presentIds)
    {
        var present = new HashSet<string>(presentIds, StringComparer.OrdinalIgnoreCase);
        _final = _recorded.Select(r =>
                r.Status == AutoFixStatus.Installed && !present.Contains(r.ModId)
                    ? r with { Status = AutoFixStatus.NotRecognized, Detail = "重扫未见该 modId" }
                    : r)
            .ToList();
        return _final;
    }

    public IReadOnlyList<AutoFixResult> Results => _final ?? _recorded;

    public int InstalledCount => Results.Count(r => r.Status == AutoFixStatus.Installed);

    public int FailedCount => Results.Count(r => !r.Ok);

    /// <summary>Status-bar / toast text. Honest even when the batch is partly red.</summary>
    public string Summary
    {
        get
        {
            var total = Results.Count;
            if (total == 0) return "没有需要补齐的前置";
            var ok = InstalledCount;
            var failed = FailedCount;
            return failed == 0
                ? $"已补齐 {ok} 个前置模组"
                : $"已补齐 {ok} 项，{failed} 项需要手动处理";
        }
    }
}
