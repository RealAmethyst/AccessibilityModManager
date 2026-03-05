using System.Globalization;
using System.Windows;
using System.Windows.Data;
using AccessibilityModManager.Core.Interfaces;

namespace AccessibilityModManager.App.Converters;

public sealed class NotInstalledToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is DependencyStatusKind kind && kind != DependencyStatusKind.Installed
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
