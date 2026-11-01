using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AuroraLauncher.Desktop.Views;

/// <summary>Minimal single-line input dialog (code-built, no XAML needed).</summary>
public static class Prompt
{
    public static string? Show(Window? owner, string title, string label, string? initial = null)
    {
        var tb = new TextBox
        {
            Text = initial ?? "",
            Margin = new Thickness(0, 8, 0, 14),
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 14
        };

        var ok = new Button { Content = "确定", Width = 90, IsDefault = true, Padding = new Thickness(0, 6, 0, 6) };
        var cancel = new Button { Content = "取消", Width = 90, IsCancel = true, Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(0, 6, 0, 6) };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, FontSize = 13 });
        panel.Children.Add(tb);
        panel.Children.Add(buttons);

        var win = new Window
        {
            Title = title,
            Owner = owner,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1D, 0x28)),
            Content = panel
        };

        bool accepted = false;
        ok.Click += (_, _) => { accepted = true; win.DialogResult = true; win.Close(); };
        tb.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { accepted = true; win.DialogResult = true; win.Close(); } };

        win.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        if (win.ShowDialog() == true && accepted)
            return tb.Text;
        return null;
    }
}
