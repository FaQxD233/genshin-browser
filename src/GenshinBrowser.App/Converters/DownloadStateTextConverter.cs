using GenshinBrowser.Localization;
using GenshinBrowser.Models;
using Microsoft.UI.Xaml.Data;

namespace GenshinBrowser.Converters;

public sealed class DownloadStateTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        DownloadState state = value is DownloadState downloadState ? downloadState : DownloadState.Interrupted;
        (string key, string fallback) = state switch
        {
            DownloadState.InProgress => ("Downloads.State.InProgress", "下载中"),
            DownloadState.Completed => ("Downloads.State.Completed", "已完成"),
            DownloadState.Canceled => ("Downloads.State.Canceled", "已取消"),
            _ => ("Downloads.State.Interrupted", "已中断"),
        };
        return LocalizationBinding.Provider?.Get(key, fallback) ?? fallback;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
