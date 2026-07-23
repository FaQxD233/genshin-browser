using GenshinBrowser.Constants;
using GenshinBrowser.Services;

namespace GenshinBrowser.Utils;

public static class EntryText
{
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

        string trimmed = title.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    public static bool TryNormalizeHttpUrl(string? url, out string normalizedUrl)
    {
        normalizedUrl = string.Empty;
        if (!TryValidateHttpUrl(url, out string validatedUrl))
        {
            return false;
        }

        string normalized = UrlNormalizer.Normalize(validatedUrl);
        if (normalized.Length == 0 || normalized.Length > AppConfig.Data.MaxEntryUrlLength)
        {
            return false;
        }

        normalizedUrl = normalized;
        return true;
    }

    public static bool TryValidateHttpUrl(string? url, out string validatedUrl)
    {
        validatedUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        string trimmed = url.Trim();
        if (trimmed.Length > AppConfig.Data.MaxEntryUrlLength ||
            !Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) ||
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
