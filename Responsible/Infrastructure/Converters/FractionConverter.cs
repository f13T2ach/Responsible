using System.Globalization;
using System.Windows.Data;

namespace Responsible.Infrastructure.Converters;

/// <summary>
/// 长度 × 比例（参数为 invariant 小数），用于输入框最大高度占内容区 1/3。
/// </summary>
public sealed class FractionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double length
            || !double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction))
        {
            return 0d;
        }

        return length * fraction;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}