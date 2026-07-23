using GenshinBrowser.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace GenshinBrowser.Converters;

public sealed class StatusLevelToInfoBarSeverityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value switch
        {
            StatusLevel.Success => InfoBarSeverity.Success,
            StatusLevel.Warning => InfoBarSeverity.Warning,
            StatusLevel.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
