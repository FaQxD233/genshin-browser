namespace GenshinBrowser.Utils;

internal static class TimeFormatter
{
    /// <summary>
    /// 格式化 UTC 时间为友好的本地时间显示
    /// </summary>
    public static string FormatRelativeTime(DateTime utcTime)
    {
        var localTime = utcTime.ToLocalTime();
        var now = DateTime.Now;

        if (localTime.Date == now.Date)
        {
            return $"今天 {localTime:HH:mm}";
        }

        if (localTime.Date == now.Date.AddDays(-1))
        {
            return $"昨天 {localTime:HH:mm}";
        }

        if ((now - localTime).TotalDays < 7)
        {
            return $"{localTime:MM-dd HH:mm}";
        }

        return localTime.ToString("yyyy-MM-dd HH:mm");
    }
}
