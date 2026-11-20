using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Responsible.Infrastructure.Converters;

/// <summary>
/// 空字符串 → Visible（占位符），非空 → Collapsed。
/// </summary>
public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}