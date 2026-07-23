using GenshinBrowser.Constants;

namespace GenshinBrowser.Utils;

public static class NavigationTarget
{
    public static string GetStartupUrl(string? savedUrl)
    {
        return EntryText.TryNormalizeHttpUrl(savedUrl, out string normalizedUrl)
            ? normalizedUrl
            : AppConfig.Browser.DefaultUrl;
    }

    public static string? Build(string? input)
    {
        string? target = input?.Trim();
        if (string.IsNullOrWhiteSpace(target) || target.Length > AppConfig.Data.MaxEntryUrlLength)
        {
            return null;
        }

        if (Uri.TryCreate(target, UriKind.Absolute, out Uri? absoluteUri))
        {
            if (IsHttpOrHttps(absoluteUri))
            {
                return LimitLength(absoluteUri.ToString());
            }

            if (target.Contains("://", StringComparison.Ordinal))
            {
                return null;
            }
        }

        if (LooksLikeWebAddress(target))
        {
            bool hasHttpScheme = target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                 target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            string prefixedTarget = hasHttpScheme ? target : $"https://{target}";

            if (Uri.TryCreate(prefixedTarget, UriKind.Absolute, out Uri? uri) && IsHttpOrHttps(uri))
            {
                return LimitLength(uri.ToString());
            }
        }

        return LimitLength(string.Format(
            AppConfig.Browser.SearchUrlTemplate,
            Uri.EscapeDataString(target)));
    }

    public static bool IsHttpOrHttps(Uri uri)
    {
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }

    private static string? LimitLength(string url)
    {
        return url.Length <= AppConfig.Data.MaxEntryUrlLength ? url : null;
    }

    private static bool LooksLikeWebAddress(string input)
    {
        if (input.Any(char.IsWhiteSpace))
        {
            return false;
        }

        int separatorIndex = input.IndexOfAny(['/', '?', '#']);
        string hostPart = separatorIndex >= 0 ? input[..separatorIndex] : input;
        if (string.IsNullOrWhiteSpace(hostPart))
        {
            return false;
        }

        int colonIndex = hostPart.LastIndexOf(':');
        if (colonIndex > 0 && hostPart.IndexOf(':') == colonIndex)
        {
            hostPart = hostPart[..colonIndex];
        }

        return hostPart.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               (hostPart.Contains('.', StringComparison.Ordinal) &&
                Uri.CheckHostName(hostPart) != UriHostNameType.Unknown);
    }
}
