using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Responsible.Infrastructure.Converters;

/// <summary>
/// 按窗口宽度断点切换可见性。参数格式："1100:narrow" 或 "1100:wide"。
/// </summary>
public sealed class BreakpointToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var width = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
        var parts = ((string)parameter).Split(':', 2);
        var threshold = double.Parse(parts[0], CultureInfo.InvariantCulture);
        var isNarrow = width < threshold;
        return parts[1] switch
        {
            "narrow" when isNarrow => Visibility.Visible,
            "wide" when !isNarrow => Visibility.Visible,
            _ => Visibility.Collapsed,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}