using System.Windows;
using AuroraCore.Services;
using AuroraLauncher.Desktop.Views;

namespace AuroraLauncher.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>Round-61: one binary, two modes. Started with
    /// <c>--apply-update|...</c> it is the updater HOST — it must never build
    /// MainWindow (that would re-trigger the swap) and instead shows a small
    /// borderless progress window until the new launcher is running. All
    /// other launches show the normal main window. StartupUri was removed so
    /// this decision happens before WPF auto-creates a window.</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (UpdateApplyArgs.TryParse(e.Args, out var plan) && plan != null)
        {
            var host = new UpdateApplyingWindow(plan);
            MainWindow = host;
            host.Show();
            return;
        }

        UpdateService.CleanupUpdaterHost();
        // The updater host that launched us exits a moment AFTER we start,
        // so its own temporary copy is still locked right now. Sweep again
        // shortly after startup instead of leaving a 63 MB file behind.
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000).ConfigureAwait(false);
            UpdateService.CleanupUpdaterHost();
        });
        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }
}
