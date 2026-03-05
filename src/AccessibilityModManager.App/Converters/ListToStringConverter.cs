using System.Collections;
using System.Globalization;
using System.Windows.Data;

namespace AccessibilityModManager.App.Converters;

public sealed class ListToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is IEnumerable enumerable)
            return string.Join(", ", enumerable.Cast<object>());
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
