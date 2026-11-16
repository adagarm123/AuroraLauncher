using System.Windows;
using AuroraCore.Models;
using AuroraCore.Services;

namespace AuroraLauncher.Desktop.Views;

/// <summary>What the user chose in the pre-launch mod check.</summary>
public enum PreflightChoice { Cancel, LaunchAnyway, InstallFabricApi, AutoFix }

/// <summary>
/// Round-77: shows the pre-launch mod findings. A MessageBox could not express
/// three answers, and the "install the missing prerequisite for me" path is the
/// whole point — a wall of text telling a new player to go find Fabric API is
/// exactly the support burden this feature is meant to remove.
/// </summary>
public partial class PreflightWindow : Window
{
    public PreflightChoice Choice { get; private set; } = PreflightChoice.Cancel;

    private readonly List<IssueVm> _items = new();

    /// <summary>Round-100: checked prerequisite ids for the auto-fix path.</summary>
    public IReadOnlyList<string> SelectedModIds =>
        _items.Where(i => i.CanFix && i.IsChecked).Select(i => i.ModId).ToList();

    /// <summary>allowAutoFix=false on the second pass after a batch, so a
    /// partially fixed pack cannot put the dialog into an infinite loop.</summary>
    public PreflightWindow(ModPreflight.Report report, GameInstance instance, bool allowAutoFix = true)
    {
        InitializeComponent();

        HeadlineText.Text = $"{report.Headline}（共 {report.IssueCount} 项） · 实例「{instance.Name}」" +
                            $" · 已扫描 {report.ScannedJars} 个模组";

        foreach (var missing in report.Missing.Take(12))
            _items.Add(new IssueVm
            {
                Title = $"缺少前置：{missing.ModId}",
                Detail = $"「{missing.RequiredBy}」需要它" +
                         (missing.Range is "*" or "" ? "" : $"（要求版本 {missing.Range}）") +
                         $" · 来源 {missing.RequiredByFile}",
                ModId = missing.ModId,
                CanFix = allowAutoFix
            });
        foreach (var conflict in report.Conflicts.Take(6))
            _items.Add(new IssueVm { Title = "模组冲突", Detail = conflict });
        foreach (var duplicate in report.Duplicates.Take(6))
            _items.Add(new IssueVm { Title = "重复安装", Detail = duplicate });
        foreach (var wrong in report.WrongLoader.Take(6))
            _items.Add(new IssueVm { Title = "加载器不匹配", Detail = wrong });

        IssueList.ItemsSource = _items;

        var hidden = report.Missing.Count + report.Conflicts.Count + report.Duplicates.Count +
                     report.WrongLoader.Count - _items.Count;
        if (hidden > 0)
        {
            TruncatedText.Text = $"还有 {hidden} 项未列出（可在「日志」页导出日志查看完整扫描结果）。";
            TruncatedText.Visibility = Visibility.Visible;
        }

        // Round-100: any missing prerequisite (not just fabric-api) can be
        // fixed in one batch, with checkboxes and per-item feedback.
        var canAutoFix = allowAutoFix && _items.Any(i => i.CanFix);
        AutoFixButton.Visibility = canAutoFix ? Visibility.Visible : Visibility.Collapsed;
        if (canAutoFix)
        {
            AutoFixButton.Content = $"补齐选中的 {_items.Count(i => i.CanFix && i.IsChecked)} 项并继续";
            AutoFixButton.IsDefault = true;
        }
        // Fabric API is the narrow, legacy one-click path.
        InstallButton.Visibility = !canAutoFix && report.FabricApiMissing
            ? Visibility.Visible : Visibility.Collapsed;
        if (!canAutoFix && report.FabricApiMissing) InstallButton.IsDefault = true;
        else if (!canAutoFix) Launch_Click_Default();
    }

    private void Launch_Click_Default()
    {
        // When there is no auto-fix available, "仍然启动" is the default action.
        foreach (var child in FindVisualChildren<System.Windows.Controls.Button>(this))
            if (Equals(child.Content, "仍然启动")) child.IsDefault = true;
    }

    private static IEnumerable<T> FindVisualChildren<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        Choice = PreflightChoice.LaunchAnyway;
        DialogResult = true;
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        Choice = PreflightChoice.InstallFabricApi;
        DialogResult = true;
    }

    private void AutoFix_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedModIds.Count == 0) return;
        Choice = PreflightChoice.AutoFix;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = PreflightChoice.Cancel;
        DialogResult = false;
    }

    private sealed class IssueVm
    {
        public string Title { get; init; } = "";
        public string Detail { get; init; } = "";
        public string ModId { get; init; } = "";
        public bool CanFix { get; init; }
        public bool IsChecked { get; set; } = true;
    }
}
