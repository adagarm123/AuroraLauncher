using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AuroraLauncher.Desktop.Converters;

/// <summary>Returns the accent brush when the bound page key equals the
/// converter parameter (used to highlight the active nav button).</summary>
public sealed class PageActiveConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        var active = string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
        // Round-66: reuse the frozen system brush. Every navigation used to
        // allocate 8 new SolidColorBrush instances (one per inactive nav item).
        if (!active) return Brushes.Transparent;
        return Application.Current.TryFindResource("NavActiveBrush") as Brush
               ?? new SolidColorBrush(Color.FromArgb(0x2E, 0x7C, 0x6A, 0xF8));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
