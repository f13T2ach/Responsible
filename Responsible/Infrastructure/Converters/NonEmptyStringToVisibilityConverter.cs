using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Responsible.Infrastructure.Converters;

/// <summary>
/// 非空字符串 → Visible（附加说明 / 徽标），空或缺失 → Collapsed。
/// </summary>
public sealed class NonEmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
