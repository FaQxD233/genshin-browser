using System.Globalization;

namespace GenshinBrowser.Utils;

/// <summary>
/// 视频续播：程序自己记下上次退出时视频页的进度，再打开时用网址参数 t=秒数 跳回去
/// （B 站有时不保存进度）。只认 B 站视频页（/video/、番剧 /bangumi/play/）和 YouTube 视频页（/watch?v=），两边都接受 t=秒数。
/// </summary>
public static class PlaybackResume
{
    /// <summary>开头这么多秒内不记：从头放也差不多。</summary>
    public const double MinimumSeconds = 10;

    /// <summary>离结尾这么多秒内算看完了，不记：下次从头放。</summary>
    public const double EndMarginSeconds = 15;

    private const string TimeParameter = "t";

    /// <summary>这个网址是能用 t= 指定开始时间的视频页。</summary>
    public static bool SupportsTimeParameter(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !NavigationTarget.IsHttpOrHttps(uri))
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath;
        return host switch
        {
            "www.bilibili.com" or "bilibili.com" or "m.bilibili.com" =>
                path.StartsWith("/video/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/bangumi/play/", StringComparison.OrdinalIgnoreCase),
            "www.youtube.com" or "youtube.com" or "m.youtube.com" =>
                path.Equals("/watch", StringComparison.OrdinalIgnoreCase) && HasParameter(uri.Query, "v"),
            _ => false,
        };
    }

    /// <summary>这个进度值得记：时长有限（不是直播），且不在开头 <see cref="MinimumSeconds"/> 秒、结尾 <see cref="EndMarginSeconds"/> 秒内。</summary>
    public static bool ShouldRemember(double positionSeconds, double durationSeconds) =>
        double.IsFinite(positionSeconds) && double.IsFinite(durationSeconds) &&
        positionSeconds >= MinimumSeconds && positionSeconds <= durationSeconds - EndMarginSeconds;

    /// <summary>
    /// 给视频页网址加上 t=整秒数（已有 t 就替换，其它参数和 # 后面的部分不动；有没有 ? 都能拼对）。不支持的网址原样返回。
    /// </summary>
    public static string WithTime(string url, double positionSeconds)
    {
        if (!SupportsTimeParameter(url) || !double.IsFinite(positionSeconds) || positionSeconds < 1)
        {
            return url;
        }

        var uri = new Uri(url);
        var pairs = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => !string.Equals(ParameterName(pair), TimeParameter, StringComparison.Ordinal))
            .Append(TimeParameter + "=" + Math.Floor(positionSeconds).ToString(CultureInfo.InvariantCulture));
        return new UriBuilder(uri) { Query = string.Join('&', pairs) }.Uri.AbsoluteUri;
    }

    private static bool HasParameter(string query, string name) =>
        query.TrimStart('?').Split('&').Any(pair => string.Equals(ParameterName(pair), name, StringComparison.Ordinal));

    private static string ParameterName(string pair)
    {
        var equals = pair.IndexOf('=');
        return equals < 0 ? pair : pair[..equals];
    }
}
