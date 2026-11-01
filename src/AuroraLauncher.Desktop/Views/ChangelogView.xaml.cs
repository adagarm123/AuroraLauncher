using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using AuroraLauncher.Desktop.ViewModels;

namespace AuroraLauncher.Desktop.Views;

/// <summary>Dedicated changelog page (sidebar, below 日志). Plain-language
/// release notes rendered as cards; content lives in HelpContent.Changelog()
/// (updated every release per docs/release-notes-template.md).</summary>
public partial class ChangelogView : UserControl
{
    public ChangelogView()
    {
        InitializeComponent();
        // Self-VM pattern: this page only displays static content, no
        // MainViewModel bindings are needed.
        DataContext = this;
    }

    public IReadOnlyList<HelpTopicVM> ChangelogTopics { get; } = HelpContent.Changelog().ToList();
}
