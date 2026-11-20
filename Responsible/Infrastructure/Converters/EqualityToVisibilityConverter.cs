using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Responsible.Infrastructure.Converters;

public sealed class EqualityToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var expected = parameter as string ?? string.Empty;
        var invert = expected.StartsWith('!');
        if (invert)
            expected = expected[1..];

        var equals = string.Equals(value?.ToString(), expected, StringComparison.Ordinal);
        return equals ^ invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}