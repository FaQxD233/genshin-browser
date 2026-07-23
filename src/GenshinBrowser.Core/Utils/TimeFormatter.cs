namespace GenshinBrowser.Utils;

public static class TimeFormatter
{
    public static string FormatRelativeTime(DateTime utcTime)
    {
        return FormatRelativeTime(utcTime, "今天", "昨天");
    }

    public static string FormatRelativeTime(DateTime utcTime, string todayText, string yesterdayText)
    {
        DateTime localTime = utcTime.ToLocalTime();
        DateTime now = DateTime.Now;

        if (localTime.Date == now.Date)
        {
            return $"{todayText} {localTime:HH:mm}";
        }

        if (localTime.Date == now.Date.AddDays(-1))
        {
            return $"{yesterdayText} {localTime:HH:mm}";
        }

        return (now - localTime).TotalDays < 7
            ? $"{localTime:MM-dd HH:mm}"
            : localTime.ToString("yyyy-MM-dd HH:mm");
    }
}
