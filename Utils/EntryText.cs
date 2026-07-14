using GenshinBrowser.Constants;

namespace GenshinBrowser.Utils;

/// <summary>
/// 历史 / 收藏等条目文本的统一处理。
/// </summary>
public static class EntryText
{
    /// <summary>
    /// 截断标题：去空白，超过 <see cref="AppConfig.Data.MaxEntryTitleLength"/> 时裁切。
    /// </summary>
    public static string TruncateTitle(string? title, int maxLength = AppConfig.Data.MaxEntryTitleLength)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        if (maxLength < 1)
        {
            maxLength = AppConfig.Data.MaxEntryTitleLength;
        }

        var trimmed = title.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    public static bool TryNormalizeHttpUrl(string? url, out string normalizedUrl)
    {
        normalizedUrl = string.Empty;
        if (!TryValidateHttpUrl(url, out var validatedUrl))
        {
            return false;
        }

        var normalized = Services.UrlNormalizer.Normalize(validatedUrl);
        if (normalized.Length == 0 || normalized.Length > AppConfig.Data.MaxEntryUrlLength)
        {
            return false;
        }

        normalizedUrl = normalized;
        return true;
    }

    /// <summary>
    /// 验证并保留原始 HTTP(S) URL。下载链接可能包含签名参数，不应走追踪参数规范化。
    /// </summary>
    public static bool TryValidateHttpUrl(string? url, out string validatedUrl)
    {
        validatedUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var trimmed = url.Trim();
        if (trimmed.Length > AppConfig.Data.MaxEntryUrlLength ||
            !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        validatedUrl = uri.OriginalString;
        return true;
    }

    public static DateTime NormalizeUtcTimestamp(DateTime value, DateTime fallbackUtc)
    {
        if (value == default)
        {
            return fallbackUtc;
        }

        DateTime utc;
        try
        {
            utc = value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            };
        }
        catch (ArgumentException)
        {
            return fallbackUtc;
        }

        return utc < DateTime.UnixEpoch || utc > DateTime.UtcNow.AddDays(1)
            ? fallbackUtc
            : utc;
    }
}
