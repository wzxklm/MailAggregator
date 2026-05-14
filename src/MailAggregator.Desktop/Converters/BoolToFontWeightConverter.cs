using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MailAggregator.Desktop.Converters;

/// <summary>
/// Converts a boolean (IsRead) to FontWeight. Unread = Bold, Read = Normal.
/// </summary>
public class BoolToFontWeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isRead)
            return isRead ? FontWeights.Normal : FontWeights.Bold;
        return FontWeights.Normal;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
