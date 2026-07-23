using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace GenshinBrowser.Converters;

public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool visible = value switch
        {
            bool flag => flag,
            int count => count > 0,
            _ => false,
        };
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        bool visible = value is Visibility.Visible;
        return string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase)
            ? !visible
            : visible;
    }
}
