using System.Text.RegularExpressions;

namespace AuroraCore.Services;

/// <summary>
/// Round-97: a small, dependency-free version-range matcher for the ranges
/// Forge's mods.toml and Fabric's mod json actually use ("[4.11.0,4.99.0)",
/// "[1.0.3,)", "*", "1.x", "a || b"). Needed BEFORE any auto-fix downloads a
/// prerequisite: picking 4.99.0 for "[4.11.0,4.99.0)" would install a jar the
/// mod explicitly refuses to load.
///
/// Unknown syntax deliberately passes (conservative): a false alarm is worse
/// than a slightly loose match at this stage.
/// </summary>
public static class VersionRangePolicy
{
    public static bool Matches(string? range, string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        var text = (range ?? "").Trim();
        if (text.Length == 0 || text is "*" or "any" or "ANY") return true;
        foreach (var alt in text.Split("||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (MatchesSingle(alt, version)) return true;
        return false;
    }

    private static bool MatchesSingle(string range, string version)
    {
        range = range.Trim();
        if (range.Length == 0 || range == "*") return true;

        if (range[0] is '[' or '(')
        {
            var close = range[^1];
            if (close is not (']' or ')')) return true; // unparseable → do not block
            var inner = range[1..^1].Split(',', 2);
            var lo = inner[0].Trim();
            var hi = inner.Length > 1 ? inner[1].Trim() : "";
            var lowerOk = range[0] == '[';
            var upperOk = close == ']';

            if (lo.Length > 0)
            {
                var c = Compare(version, lo);
                if (c < 0 || (c == 0 && !lowerOk)) return false;
            }
            if (hi.Length > 0)
            {
                var c = Compare(version, hi);
                if (c > 0 || (c == 0 && !upperOk)) return false;
            }
            return true;
        }

        if (range.EndsWith(".*", StringComparison.Ordinal) || range.EndsWith(".x", StringComparison.OrdinalIgnoreCase))
        {
            var prefix = range[..range.LastIndexOf('.')];
            return version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        // Bare version: Maven semantics = "this version or newer".
        return Compare(version, range) >= 0;
    }

    /// <summary>Numeric-segment comparison; "4.11.0" &lt; "4.99.0", and
    /// "1.20.1+forge" compares on 1/20/1. Non-numeric suffixes are ignored so
    /// build metadata does not change ordering.</summary>
    public static int Compare(string a, string b)
    {
        var x = Numbers(a);
        var y = Numbers(b);
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var l = i < x.Length ? x[i] : 0;
            var r = i < y.Length ? y[i] : 0;
            if (l != r) return l.CompareTo(r);
        }
        return 0;
    }

    private static int[] Numbers(string s)
        => Regex.Matches(s ?? "", @"\d+")
            .Select(m => int.TryParse(m.Value, out var n) ? n : 0)
            .ToArray();

    /// <summary>Newest version satisfying the range, or null.</summary>
    public static string? PickLatest(string? range, IEnumerable<string> versions)
        => versions.Where(v => Matches(range, v))
            .OrderBy(v => v, Comparer<string>.Create(Compare))
            .LastOrDefault();

    /// <summary>Human-readable label for several merged requirements.</summary>
    public static string MergeLabel(IEnumerable<string> ranges)
    {
        var distinct = ranges.Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim()).Distinct(StringComparer.Ordinal).ToList();
        return distinct.Count == 0 ? "*" : string.Join(" 且 ", distinct.Take(3)) +
               (distinct.Count > 3 ? " …" : "");
    }
}
