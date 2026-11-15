using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AuroraCore.Services;

namespace AuroraLauncher.Desktop.Converters;

/// <summary>Round-96: an accent bar on the LEFT edge marks the truly active
/// page. Mouse-over can still tint a button's background, but it can never
/// produce this bar, so "highlighted" and "hovered" are visually distinct.</summary>
public sealed class PageActiveBorderConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (!NavigationHighlight.IsActive(value?.ToString(), parameter?.ToString()))
            return Brushes.Transparent;
        return Application.Current.TryFindResource("AccentBrush") as Brush
               ?? new SolidColorBrush(Color.FromArgb(0xFF, 0x5B, 0x4D, 0xF6));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
