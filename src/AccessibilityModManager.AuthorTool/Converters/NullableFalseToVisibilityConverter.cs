using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AccessibilityModManager.AuthorTool.Converters;

/// <summary>
/// For nullable bool bindings: returns Visible when the value is false (i.e. confirmed
/// negative), Collapsed when true or null. Used by the registry banner so the "Request
/// listing" button only appears when we know the plugin isn't listed (not while still
/// checking).
/// </summary>
public sealed class NullableFalseToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is false ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
