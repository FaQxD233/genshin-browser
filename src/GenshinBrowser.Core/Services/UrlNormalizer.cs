namespace GenshinBrowser.Services;

public static class UrlNormalizer
{
    private static readonly HashSet<string> GlobalTrackingParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content",
        "fbclid", "gclid", "mc_cid", "mc_eid", "igshid", "msclkid", "yclid",
    };

    private static readonly HashSet<string> BilibiliTrackingParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "spm_id_from", "from_spmid", "from", "from_spm", "share_source", "share_medium",
        "share_plat", "share_tag", "share_session_id", "buvid", "vd_source", "up_id",
        "up_session_id", "is_story_h5", "ts", "unique_k", "share_times", "platform", "t",
    };

    public static string Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        string trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
        {
            return trimmed;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return uri.ToString();
        }

        string query = uri.Query;
        if (query.Length <= 1)
        {
            return uri.ToString();
        }

        bool isBilibili = IsBilibiliHost(uri.Host);
        string[] pairs = query.AsSpan(1).ToString().Split('&', StringSplitOptions.RemoveEmptyEntries);
        List<string> kept = new(pairs.Length);

        foreach (string pair in pairs)
        {
            int equalsIndex = pair.IndexOf('=');
            string encodedName = equalsIndex < 0 ? pair : pair[..equalsIndex];
            string name;

            try
            {
                name = Uri.UnescapeDataString(encodedName.Replace("+", " ", StringComparison.Ordinal));
            }
            catch (UriFormatException)
            {
                name = encodedName;
            }

            if (!GlobalTrackingParameters.Contains(name) &&
                (!isBilibili || !BilibiliTrackingParameters.Contains(name)))
            {
                kept.Add(pair);
            }
        }

        if (kept.Count == pairs.Length)
        {
            return uri.ToString();
        }

        UriBuilder builder = new(uri)
        {
            Query = kept.Count == 0 ? string.Empty : string.Join('&', kept),
        };
        return builder.Uri.ToString();
    }

    private static bool IsBilibiliHost(string host)
    {
        return host.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("b23.tv", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".b23.tv", StringComparison.OrdinalIgnoreCase);
    }
}
