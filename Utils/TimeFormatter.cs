namespace GenshinBrowser.Utils;

internal static class TimeFormatter
{
    /// <summary>
    /// 格式化 UTC 时间为友好的本地时间显示
    /// </summary>
    public static string FormatRelativeTime(DateTime utcTime)
    {
        return FormatRelativeTime(utcTime, "今天", "昨天");
    }

    public static string FormatRelativeTime(DateTime utcTime, string todayText, string yesterdayText)
    {
        var localTime = utcTime.ToLocalTime();
        var now = DateTime.Now;

        if (localTime.Date == now.Date)
        {
            return $"{todayText} {localTime:HH:mm}";
        }

        if (localTime.Date == now.Date.AddDays(-1))
        {
            return $"{yesterdayText} {localTime:HH:mm}";
        }

        if ((now - localTime).TotalDays < 7)
        {
            return $"{localTime:MM-dd HH:mm}";
        }

        return localTime.ToString("yyyy-MM-dd HH:mm");
    }
}
