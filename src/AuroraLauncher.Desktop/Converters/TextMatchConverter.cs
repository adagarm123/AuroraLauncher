using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AuroraLauncher.Desktop.Converters;

/// <summary>Returns the nav-active brush when the bound string equals the
/// converter parameter; transparent otherwise. Used for sub-navigation.</summary>
public sealed class TextMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var match = string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
        if (!match) return new SolidColorBrush(Colors.Transparent);
        return Application.Current.TryFindResource("NavActiveBrush") as Brush
               ?? new SolidColorBrush(Color.FromArgb(0x2E, 0x7C, 0x6A, 0xF8));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
