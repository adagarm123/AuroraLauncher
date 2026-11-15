namespace AuroraSelfTest;

/// <summary>Guard behind "long lists stay virtualized" (round-68 review item #2).
///
/// WPF disables virtualization the moment a list is measured with infinite
/// height — which is exactly what happens inside a page-level ScrollViewer.
/// Round-17 already learned this the hard way (103 version rows realised at
/// once). The rule enforced here: a ListBox nested in a ScrollViewer MUST carry
/// its own bound (MaxHeight / Height), because FrameworkElement.MeasureCore
/// clamps the available height by that constraint and the items host then sees a
/// finite viewport again. SettingsView's Java list is the legitimate example.</summary>
internal static class NestedScrollPolicy
{
    public static IReadOnlyList<string> FindUnboundedLists(string xaml)
    {
        var problems = new List<string>();
        if (string.IsNullOrEmpty(xaml)) return problems;

        var depth = 0;
        var lineNo = 0;
        foreach (var line in xaml.Split('\n'))
        {
            lineNo++;

            if (line.Contains("</ScrollViewer", StringComparison.Ordinal))
                depth = Math.Max(0, depth - 1);

            var openAt = line.IndexOf("<ScrollViewer", StringComparison.Ordinal);
            if (openAt >= 0 && !line.Contains("</ScrollViewer", StringComparison.Ordinal))
            {
                var selfClosing = line.IndexOf("/>", openAt, StringComparison.Ordinal) > openAt;
                if (!selfClosing) depth++;
            }

            if (depth <= 0) continue;
            if (!ListBoxOpen.IsMatch(line)) continue;
            if (BoundHeight.IsMatch(line)) continue;

            problems.Add($"line {lineNo}: ListBox inside ScrollViewer without MaxHeight/Height");
        }
        return problems;
    }

    /// <summary>Only a real start tag — property-element syntax such as
    /// <c>&lt;ListBox.ItemTemplate&gt;</c> is not a list.</summary>
    private static readonly System.Text.RegularExpressions.Regex ListBoxOpen =
        new(@"<ListBox[\s>]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>MaxHeight / Height only — MinHeight does not bound the measure.
    /// The space before the name keeps MinHeight and ScrollViewer.Height= from
    /// matching by accident.</summary>
    private static readonly System.Text.RegularExpressions.Regex BoundHeight =
        new(@"(?:\s|^)(?:Max)?Height\s*=\s*""[0-9]", System.Text.RegularExpressions.RegexOptions.Compiled);
}
