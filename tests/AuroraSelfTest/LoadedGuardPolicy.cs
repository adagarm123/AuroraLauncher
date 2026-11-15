using System.Text.RegularExpressions;

namespace AuroraSelfTest;

/// <summary>Rule behind "Guard: cached pages never re-run init on every
/// Loaded" (round-64). Matches the PATTERN of a single-shot guard, not a
/// hard-coded field name: _initialized / _isLoaded / _firstLoadDone are all
/// equally valid. A name-based check produced false positives and forced
/// future contributors to rename perfectly correct guards.</summary>
internal static class LoadedGuardPolicy
{
    private static readonly Regex BoolField = new(@"private\s+bool\s+(_\w+)", RegexOptions.Compiled);

    public static bool IsGuarded(string source)
    {
        // (a) any private bool field used as an early-return guard
        //     (supports both `if (_x) return;` and `if (!_x) return;`)
        foreach (Match f in BoolField.Matches(source))
        {
            var name = Regex.Escape(f.Groups[1].Value);
            if (Regex.IsMatch(source, @"if\s*\(\s*!?" + name + @"\s*\)\s*return\s*;"))
                return true;
        }
        // (b) or a re-entrant subscription hook (LogsView: Unloaded unhooks,
        //     Loaded re-hooks on every attach) — also a safe pattern.
        return source.Contains("HookVm(");
    }
}
