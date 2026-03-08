using System.Globalization;
using System.Windows.Data;

namespace MailAggregator.Desktop.Converters;

/// <summary>
/// Converts a file size in bytes to a human-readable string (KB, MB, etc.).
/// </summary>
public class FileSizeConverter : IValueConverter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB"];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not long size) return "0 B";

        var unitIndex = 0;
        var displaySize = (double)size;

        while (displaySize >= 1024 && unitIndex < Units.Length - 1)
        {
            displaySize /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{displaySize:F0} {Units[unitIndex]}"
            : $"{displaySize:F1} {Units[unitIndex]}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
