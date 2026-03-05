using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AccessibilityModManager.App.Converters;

public class TabIndexToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int selectedIndex && parameter is string paramStr && int.TryParse(paramStr, out var targetIndex))
            return selectedIndex == targetIndex ? Visibility.Visible : Visibility.Collapsed;

        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
