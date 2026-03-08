using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MailAggregator.Desktop.Converters;

/// <summary>
/// Converts boolean to Visibility. True = Visible, False = Collapsed.
/// Set ConverterParameter to "Invert" for reverse behavior.
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var boolValue = value is bool b && b;
        var invert = parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);

        if (invert) boolValue = !boolValue;
        return boolValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
