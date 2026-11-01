using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AuroraLauncher.Desktop.Converters;

/// <summary>Colors the status-bar text (and, with parameter "bg", a soft
/// background) by message semantics: errors red, successes green, rest neutral.</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value?.ToString() ?? "";
        var bg = string.Equals(parameter?.ToString(), "bg", StringComparison.OrdinalIgnoreCase);

        static Brush? Find(string key) => Application.Current?.TryFindResource(key) as Brush;

        if (s.Contains("失败") || s.Contains("错误") || s.Contains("超时") || s.Contains("无法") ||
            s.Contains("已取消") || s.Contains("崩溃") || s.Contains("拒绝"))
            return bg ? Find("DangerSoftBrush") ?? Brushes.Transparent : Find("DangerBrush") ?? Brushes.Red;

        if (s.Contains("完成") || s.Contains("成功") || s.Contains("已安装") || s.Contains("已导入") ||
            s.Contains("已创建") || s.Contains("已删除") || s.Contains("已保存") || s.Contains("已打开"))
            return bg ? Find("AccentSoftBrush") ?? Brushes.Transparent : Find("SuccessBrush") ?? Brushes.Green;

        return bg ? Brushes.Transparent : Find("TextBrush") ?? Brushes.Black;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}
