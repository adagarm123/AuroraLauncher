using System.Windows;
using AuroraCore.Services;

namespace AuroraLauncher.Desktop.Views;

/// <summary>Round-99: every destructive path is listed in full and the user
/// must tick an explicit acknowledgement. Changing the save/Java switches
/// REBUILDS the plan, so the list always matches what will actually happen.</summary>
public partial class UninstallWindow : Window
{
    private readonly string _root;
    private readonly string? _exe;
    private readonly bool _portable;
    private readonly IReadOnlyList<string> _entries;
    private UninstallPlan _plan;

    public UninstallPlan ResultPlan => _plan;

    /// <summary>Round-102: registry cleanup is opt-out, but strictly allowlisted.</summary>
    public bool ResultRemoveRegistry => RemoveRegistryBox.IsChecked == true;

    public UninstallWindow(string root, string? exe, bool portable, IReadOnlyList<string> entries)
    {
        InitializeComponent();
        _root = root;
        _exe = exe;
        _portable = portable;
        _entries = entries;
        _plan = UninstallPolicy.BuildPlan(
            new UninstallRequest(root, exe, portable, false, false, entries));
        var regEntries = UninstallRegistryService.Scan(exe ?? "");
        RegistryHintText.Text = regEntries.Count == 0
            ? "当前未检测到属于启动器的注册表项。"
            : "检测到 " + regEntries.Count + " 项：" + string.Join("、", regEntries.Select(r => r.Display));
        Rebuild();
    }

    private void Rebuild()
    {
        _plan = UninstallPolicy.BuildPlan(new UninstallRequest(
            _root, _exe, _portable,
            DeleteSavesBox.IsChecked == true,
            DeleteJavaBox.IsChecked == true,
            _entries));
        TargetList.ItemsSource = _plan.Targets;
        KeepList.ItemsSource = _plan.Kept;
        WarningText.Text = UninstallPolicy.Warning(_plan);
    }

    private void OnOptionsChanged(object sender, RoutedEventArgs e) => Rebuild();

    private void OnConfirmChanged(object sender, RoutedEventArgs e)
        => OkButton.IsEnabled = ConfirmBox.IsChecked == true && _plan.Targets.Count > 0;

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (ConfirmBox.IsChecked != true || _plan.Targets.Count == 0) return;
        DialogResult = true;
        Close();
    }
}
