using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AuroraSelfTest;

/// <summary>Opt-in diagnostic (round-67): loads the REAL style dictionary from
/// the Desktop assembly and forces layout / visibility / IsIndeterminate
/// cycles on the exact elements changed in round-66. Pure logic tests and the
/// XAML compiler cannot catch runtime exceptions in Style.Triggers or
/// ControlTemplate Storyboards. Invoked ONLY with nav-smoke; never in
/// unit / all modes.
///
/// Note: App.xaml cannot be loaded here because its relative pack URIs resolve
/// against the TEST host assembly, not the Desktop assembly. We load the style
/// dictionary directly with an assembly-qualified URI instead.</summary>
internal static class NavSmoke
{
    public static int Run()
    {
        var code = -1;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var dict = new ResourceDictionary
                {
                    Source = new Uri(
                        "pack://application:,,,/AuroraLauncher;component/Styles/AuroraControls.xaml",
                        UriKind.Absolute)
                };
                app.Resources.MergedDictionaries.Add(dict);
                Console.WriteLine("[smoke] style dictionary loaded");

                var card = new Border
                {
                    Style = (Style)dict["Card"],
                    Width = 300, Height = 120,
                    Background = Brushes.DimGray
                };
                var bar = new ProgressBar { IsIndeterminate = true, Width = 220, Height = 8 };
                var host = new StackPanel();
                host.Children.Add(card);
                host.Children.Add(bar);

                var win = new Window { Content = host, Width = 500, Height = 300 };
                Layout(win);
                Console.WriteLine("[smoke] initial layout OK");

                for (var i = 0; i < 12; i++)
                {
                    host.Visibility = i % 2 == 0 ? Visibility.Collapsed : Visibility.Visible;
                    bar.IsIndeterminate = i % 3 != 0;
                    Layout(win);
                }
                Console.WriteLine("[smoke] SMOKE OK - 12 visibility / indeterminate cycles");
                code = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[smoke] EXCEPTION: " + ex);
                code = 1;
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60)))
        {
            Console.WriteLine("[smoke] TIMEOUT - UI thread blocked");
            return 2;
        }
        return code;
    }

    private static void Layout(Window win)
    {
        win.Measure(new Size(500, 300));
        win.Arrange(new Rect(0, 0, 500, 300));
        win.UpdateLayout();
    }
}
