using System.Text;

namespace AuroraCore.Services;

/// <summary>
/// Round-90: "did the user change anything on the settings page and walk away?"
///
/// The page is local-edit + explicit save (<c>ApplySettings</c>). Before this,
/// typing a new Java path and clicking a sidebar item threw the edit away with
/// no trace — the user re-opened settings, saw the OLD value, and concluded the
/// launcher "forgot" it.
///
/// The comparison is a snapshot string, not a bunch of INotifyPropertyChanged
/// flags: fifteen setters would each need a `MarkDirty()` call, and the one that
/// somebody forgets is exactly the one that loses data. A snapshot has no such
/// hole — it compares whatever <c>Capture</c> produced at save time against what
/// it produces now, so a newly added field is included by construction.
/// </summary>
public static class SettingsDirtyPolicy
{
    private const char RecordSeparator = '\n';
    private const char FieldSeparator = '=';

    /// <summary>Builds a stable, order-independent snapshot. Keys are sorted so
    /// two equal configurations always produce byte-identical strings.</summary>
    public static string Snapshot(IEnumerable<KeyValuePair<string, string?>> values)
    {
        if (values == null) return "";
        var sb = new StringBuilder();
        foreach (var kv in values.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            sb.Append(kv.Key).Append(FieldSeparator)
              .Append((kv.Value ?? "").Replace(RecordSeparator, ' '))
              .Append(RecordSeparator);
        }
        return sb.ToString();
    }

    public static bool IsDirty(string? baseline, string? current)
        => !string.Equals(baseline ?? "", current ?? "", StringComparison.Ordinal);

    /// <summary>Which fields differ. Used for the prompt ("改了 3 项：内存、Java 路径…")
    /// and for tests — a bare boolean would not tell the user WHY they are being stopped.</summary>
    public static IReadOnlyList<string> ChangedKeys(string? baseline, string? current)
    {
        var before = Parse(baseline);
        var after = Parse(current);
        var changed = new List<string>();
        foreach (var key in before.Keys.Union(after.Keys, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
        {
            before.TryGetValue(key, out var b);
            after.TryGetValue(key, out var a);
            if (!string.Equals(b, a, StringComparison.Ordinal)) changed.Add(key);
        }
        return changed;
    }

    /// <summary>The dialog text. Names the fields (up to five) so the user can see
    /// what is at stake instead of a generic "unsaved changes".</summary>
    public static string BuildPrompt(IReadOnlyCollection<string>? changedFriendlyNames)
    {
        var names = changedFriendlyNames?.Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? new List<string>();
        var headline = names.Count == 0
            ? "设置还没有保存。"
            : $"设置还没有保存（改了 {names.Count} 项：{string.Join("、", names.Take(5))}" +
              (names.Count > 5 ? " 等" : "") + "）。";

        return headline + "\n\n" +
               "选择「是」：保存后离开（立即生效）。\n" +
               "选择「否」：放弃这次修改，恢复成上次保存的值。\n" +
               "选择「取消」：留在设置页继续修改。";
    }

    private static Dictionary<string, string> Parse(string? snapshot)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(snapshot)) return result;
        foreach (var line in snapshot.Split(RecordSeparator))
        {
            if (line.Length == 0) continue;
            var idx = line.IndexOf(FieldSeparator);
            if (idx <= 0) continue;
            result[line[..idx]] = line[(idx + 1)..];
        }
        return result;
    }
}
