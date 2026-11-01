using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AuroraLauncher.Desktop.Converters;

/// <summary>bool → double opacity: true = 1.0, false = dimmed (default 0.55,
/// overridable via ConverterParameter).</summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var on = value is bool b && b;
        if (!on)
        {
            if (parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                return p;
            return 0.55d;
        }
        return 1.0d;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}
