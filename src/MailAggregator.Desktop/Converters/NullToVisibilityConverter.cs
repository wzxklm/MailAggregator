using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MailAggregator.Desktop.Converters;

/// <summary>
/// Converts null to Collapsed, non-null to Visible.
/// Set ConverterParameter to "Invert" for reverse behavior.
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isNull = value == null;
        var invert = parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);

        if (invert) isNull = !isNull;
        return isNull ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
