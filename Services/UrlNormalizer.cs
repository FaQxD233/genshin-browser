namespace GenshinBrowser.Services;

/// <summary>
/// 规范化 URL：剥离已知的追踪 / 分析参数与无意义 UI 标记，避免同一条目以不同参数形式被重复记录。
/// 仅对 query 部分过滤；path、host、fragment 保持不变。
/// </summary>
public static class UrlNormalizer
{
    /// <summary>
    /// 已知的追踪 / 分析参数名（小写匹配，含通用 utm_* 与 B 站特有 spm_id_from / vd_source 等）。
    /// 同一视频带不同 t=（跳转时间戳）也会被合并，避免历史重复堆积。
    /// </summary>
    private static readonly HashSet<string> GlobalTrackingParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content",
        "fbclid", "gclid", "mc_cid", "mc_eid", "igshid", "msclkid", "yclid",
    };

    private static readonly HashSet<string> BilibiliTrackingParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "spm_id_from", "from_spmid", "from", "from_spm", "share_source", "share_medium",
        "share_plat", "share_tag", "share_session_id", "buvid", "vd_source", "up_id",
        "up_session_id", "is_story_h5", "ts", "unique_k", "share_times", "platform",
        "t",
    };

    /// <summary>
    /// 规范化 URL。无法解析为绝对 URI 时原样返回，保证不丢数据。
    /// </summary>
    public static string Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return url.Trim();
        }

        // 非 http/https 不处理（避免破坏 mailto:、file: 等特殊协议）
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return uri.ToString();
        }

        var query = uri.Query;
        if (string.IsNullOrEmpty(query) || query.Length <= 1)
        {
            return uri.ToString();
        }

        var isBilibili = IsBilibiliHost(uri.Host);
        var pairs = query.AsSpan(1).ToString().Split('&', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(pairs.Length);
        foreach (var pair in pairs)
        {
            var eq = pair.IndexOf('=');
            var encodedName = eq < 0 ? pair : pair[..eq];
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
            // 没有任何参数被剥除，返回原 URL 避免重新构造带来的大小写/编码差异
            return uri.ToString();
        }

        var builder = new UriBuilder(uri)
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
