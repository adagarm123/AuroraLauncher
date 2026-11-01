using System.Windows.Data;

namespace AuroraLauncher.Desktop.Converters;

/// <summary>true -> "已启用", false -> "已禁用".</summary>
public sealed class BoolToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is bool b && b ? "已启用" : "已禁用";

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
