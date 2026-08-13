using System.Globalization;
using GenshinBrowser.Services;

namespace GenshinBrowser.Utils;

internal static class TimeFormatter
{
    /// <summary>
    /// 格式化 UTC 时间为友好的本地时间显示。时间文案走本地化资源，
    /// 避免硬编码中文在 en-US 界面下显示「今天/昨天」。
    /// </summary>
    public static string FormatRelativeTime(DateTime utcTime)
    {
        var localTime = utcTime.ToLocalTime();
        var now = DateTime.Now;

        if (localTime.Date == now.Date)
        {
            return LocalizationService.Format("Time.Today", localTime.ToString("HH:mm", CultureInfo.CurrentCulture));
        }

        if (localTime.Date == now.Date.AddDays(-1))
        {
            return LocalizationService.Format("Time.Yesterday", localTime.ToString("HH:mm", CultureInfo.CurrentCulture));
        }

        if ((now - localTime).TotalDays < 7)
        {
            return localTime.ToString("MM-dd HH:mm", CultureInfo.CurrentCulture);
        }

        return localTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    }
}
