using System.Collections.ObjectModel;
using System.Windows.Media;
using AuroraCore.Services;
using AuroraLauncher.Desktop.Services;
using AuroraLauncher.ViewModels;

namespace AuroraLauncher.Desktop.ViewModels;

/// <summary>Wraps a ModrinthProject for the content browser list, adding an
/// asynchronously loaded icon.</summary>
public class ContentItemVM : ViewModelBase
{
    private ImageSource? _icon;

    public ContentItemVM(ModrinthProject project)
    {
        Project = project;
        _ = LoadIconAsync();
    }

    public ModrinthProject Project { get; }

    public ImageSource? Icon
    {
        get => _icon;
        private set => SetProperty(ref _icon, value);
    }

    private async Task LoadIconAsync()
    {
        var icon = await IconCache.GetAsync(Project.IconUrl).ConfigureAwait(true);
        Icon = icon;
    }
}

/// <summary>One row of the version-history picker for a content item.
/// Modpack rows use <c>neutral</c>: they always install into a NEW dedicated
/// instance, so instance-compatibility does not apply.</summary>
public class VersionHistoryVM : ViewModelBase
{
    public VersionHistoryVM(ModrinthVersion version, bool compatible, bool neutral = false)
    {
        Version = version;
        Compatible = compatible || neutral;
        Neutral = neutral;
    }

    public ModrinthVersion Version { get; }
    public bool Neutral { get; }

    /// <summary>True when this version may install without a confirmation
    /// (compatible with the instance, or a modpack creating its own instance).</summary>
    public bool Compatible { get; }

    public string VersionNumber => Version.VersionNumber;

    public string Meta
    {
        get
        {
            var parts = new List<string>();
            var loaders = string.Join("/", Version.Loaders.Take(2));
            if (!string.IsNullOrEmpty(loaders)) parts.Add(loaders);
            var gv = Version.GameVersions.FirstOrDefault();
            if (!string.IsNullOrEmpty(gv)) parts.Add("MC " + gv);
            parts.Add(FormatSize(Version.FileSize));
            if (!string.IsNullOrEmpty(Version.VersionType)) parts.Add(Version.VersionType);
            return string.Join(" · ", parts);
        }
    }

    public string CompatibleText => Neutral ? "新建实例" : Compatible ? "✓ 适配" : "不适配";

    private static string FormatSize(long b) =>
        b >= 1 << 20 ? $"{b / 1048576.0:0.0} MB" : b >= 1024 ? $"{b / 1024.0:0} KB" : $"{b} B";
}

/// <summary>All versions of a content item grouped under one supported MC version.</summary>
public class McVersionGroupVM
{
    public McVersionGroupVM(string mcVersion, ObservableCollection<VersionHistoryVM> versions)
    {
        McVersion = mcVersion;
        Versions = versions;
    }

    public string McVersion { get; }
    public ObservableCollection<VersionHistoryVM> Versions { get; }
    public string Header => $"Minecraft {McVersion}";
    public string CountText => $"{Versions.Count} 个版本";
}

/// <summary>A group header row in the flattened detail list — small and uniform
/// so virtualized pixel scrolling stays smooth even with thousands of versions.</summary>
public class ContentGroupHeaderVM
{
    public ContentGroupHeaderVM(string mcVersion, string countText)
    {
        McVersion = mcVersion;
        CountText = countText;
    }

    public string McVersion { get; }
    public string CountText { get; }
    public string Header => $"Minecraft {McVersion}";
}

/// <summary>One loader card on the "install new game" panel. An instance can
/// only carry ONE mod loader: picking a version here locks every other card
/// (via <c>onPicked</c> wired by MainViewModel).</summary>
public class LoaderCardVM : ViewModelBase
{
    private readonly Action<LoaderCardVM>? _onPicked;

    // ---- Sentinel strings, single source (round-53) ----
    // The "did the user pick a loader" decision must never be a scattered
    // string comparison (round-32 Evaluate lesson: state judgments belong in
    // one place). All checks go through IsLoaderSelected / IsAutoPick.
    public const string NoneChoice = "不安装";
    public const string AutoChoice = "（自动选择最新）";
    public const string TestSuffix = "（测试版）";

    public LoaderCardVM(ModLoaderType loader, string name, string shortName,
        Action<LoaderCardVM>? onPicked = null, long generation = -1)
    {
        Loader = loader;
        Name = name;
        Short = shortName;
        Generation = generation;
        _onPicked = onPicked;
    }

    public ModLoaderType Loader { get; }
    public string Name { get; }
    public string Short { get; }

    /// <summary>Generation tag of the card-build that created this card. The
    /// owner rejects callbacks from cards of an older generation — WPF may
    /// push a null selection into a detached card ASYNC, after a newer
    /// skeleton has replaced it, so collection-membership checks are not
    /// reliable; a monotonic counter is (round-53, per external review).</summary>
    public long Generation { get; }

    public ObservableCollection<string> Versions { get; } = new();

    private string _selectedVersion = NoneChoice;
    public string SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            // Round-50 fix: WPF pushes NULL into this setter when the bound
            // items list is cleared/reset (SelectedItem falls back to null).
            // Storing null made HasVersion true (null != "不安装") — cards
            // "selected themselves" with an EMPTY combo box, and the mutual
            // exclusion then polluted every other card. null/empty is always
            // coerced to the neutral choice.
            var v = string.IsNullOrEmpty(value) ? NoneChoice : value;
            if (SetProperty(ref _selectedVersion, v))
            {
                OnPropertyChanged(nameof(HasVersion));
                OnPropertyChanged(nameof(IsLoaderSelected));
                OnPropertyChanged(nameof(IsAutoPick));
                OnPropertyChanged(nameof(StateText));
                // A real loader version was picked → let the owner enforce
                // mutual exclusion across the other cards.
                _onPicked?.Invoke(this);
            }
        }
    }

    /// <summary>THE single source for "did the user pick a loader" — derived
    /// once here; nothing else may string-compare against the sentinel.</summary>
    public bool IsLoaderSelected => !string.IsNullOrEmpty(SelectedVersion) && SelectedVersion != NoneChoice;

    /// <summary>True when the user picked the "auto select latest" choice
    /// (resolver decides the concrete version at install time).</summary>
    public bool IsAutoPick => IsLoaderSelected && string.Equals(SelectedVersion, AutoChoice, StringComparison.Ordinal);

    /// <summary>Legacy alias kept for existing XAML triggers — same meaning
    /// as <see cref="IsLoaderSelected"/>.</summary>
    public bool HasVersion => IsLoaderSelected;

    private bool _isLoading;
    /// <summary>Version list is still being fetched — the card shows up
    /// IMMEDIATELY with a loading hint instead of appearing late (the old
    /// "select a version → loader panel pops in seconds later" lag).</summary>
    public bool IsLoading
    {
        get => _isLoading;
        set { if (SetProperty(ref _isLoading, value)) OnPropertyChanged(nameof(StateText)); }
    }

    public string StateText => HasVersion ? "已选择"
        : IsLoading ? "版本列表加载中…"
        : Empty ? "暂无可用版本"
        : "不安装";

    private bool _isLocked;
    /// <summary>Locked while ANOTHER card carries the loader choice.</summary>
    public bool IsLocked
    {
        get => _isLocked;
        set { if (SetProperty(ref _isLocked, value)) OnPropertyChanged(nameof(CanSelect)); }
    }

    /// <summary>ComboBox enable state: unlocked cards stay selectable.</summary>
    public bool CanSelect => !IsLocked;

    public bool Empty { get; set; }
}
