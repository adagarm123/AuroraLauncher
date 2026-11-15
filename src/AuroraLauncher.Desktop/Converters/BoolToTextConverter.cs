using System.Windows.Data;

namespace AuroraLauncher.Desktop.Converters;

/// <summary>
/// true -> "已启用", false -> "已禁用".
///
/// Round-93: pass <c>ConverterParameter="忙碌文案|空闲文案"</c> to reuse it for
/// busy captions, e.g. binding a button's Content to
/// <c>{Binding ScanCommand.IsRunning, Converter=…, ConverterParameter='扫描中…|扫描'}</c>.
/// No new resource key, so every page (and every smoke that loads resources by
/// hand) keeps working.
/// </summary>
public sealed class BoolToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (parameter is string spec && spec.Contains('|'))
        {
            var parts = spec.Split('|', 2);
            return flag ? parts[0] : parts[1];
        }
        return flag ? "已启用" : "已禁用";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
