using GenshinBrowser.Constants;

namespace GenshinBrowser.Utils;

/// <summary>
/// 地址栏输入 → 可导航 URL 的解析与启动 URL 回退。
/// </summary>
public static class NavigationTarget
{
    /// <summary>
    /// 从配置中的 LastUrl 得到合法启动地址；无效时回退默认 B 站搜索。
    /// </summary>
    public static string GetStartupUrl(string? savedUrl)
    {
        if (EntryText.TryNormalizeHttpUrl(savedUrl, out var normalizedUrl))
        {
            return normalizedUrl;
        }

        return AppConfig.Browser.DefaultUrl;
    }

    /// <summary>
    /// 将地址栏输入解析为 http(s) URL；纯关键词则走 B 站搜索。
    /// 返回 null 表示含未知协议等不可导航输入。
    /// </summary>
    public static string? Build(string? input)
    {
        var target = input?.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        if (target.Length > AppConfig.Data.MaxEntryUrlLength)
        {
            return null;
        }

        if (Uri.TryCreate(target, UriKind.Absolute, out var absoluteUri))
        {
            if (IsHttpOrHttps(absoluteUri))
            {
                return LimitLength(absoluteUri.ToString());
            }

            // 非 http(s) 的绝对 URI 视为不可导航（mailto: / tel: / file: / notes:// 等），
            // 不再落入搜索。注意 host[:port] 形式（无 scheme 前缀）会被 Uri 解析进
            // scheme 位（localhost:8080 → scheme=localhost），须放行给后续主机判定。
            if (target.Contains("://", StringComparison.Ordinal) || !SchemeLooksLikeHost(absoluteUri.Scheme))
            {
                return null;
            }
        }

        if (LooksLikeWebAddress(target))
        {
            var hasHttpScheme = target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            var prefixedTarget = hasHttpScheme ? target : $"https://{target}";

            if (Uri.TryCreate(prefixedTarget, UriKind.Absolute, out var uri) && IsHttpOrHttps(uri))
            {
                return LimitLength(uri.ToString());
            }
        }

        return LimitLength(string.Format(AppConfig.Browser.SearchUrlTemplate, Uri.EscapeDataString(target)));
    }

    public static bool IsHttpOrHttps(Uri uri)
    {
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }

    private static string? LimitLength(string url) =>
        url.Length <= AppConfig.Data.MaxEntryUrlLength ? url : null;

    private static bool LooksLikeWebAddress(string input)
    {
        if (input.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var separatorIndex = input.IndexOfAny(['/', '?', '#']);
        var hostPart = separatorIndex >= 0 ? input[..separatorIndex] : input;
        if (string.IsNullOrWhiteSpace(hostPart))
        {
            return false;
        }

        // userinfo 前缀（user@host / user:pass@host）：取最后一个 @ 之后的 authority
        var atIndex = hostPart.LastIndexOf('@');
        if (atIndex >= 0)
        {
            hostPart = hostPart[(atIndex + 1)..];
            if (hostPart.Length == 0)
            {
                return false;
            }
        }

        // [IPv6] 字面量（可带 :port）：只校验括号配对与内容非空，
        // 括号内格式交给 Build 侧 Uri.TryCreate 最终裁决
        if (hostPart.StartsWith('['))
        {
            var closeBracket = hostPart.IndexOf(']');
            return closeBracket > 1 && hostPart.IndexOf(']', closeBracket + 1) < 0;
        }

        var colonIndex = hostPart.LastIndexOf(':');
        if (colonIndex > 0 && hostPart.IndexOf(':') == colonIndex)
        {
            hostPart = hostPart[..colonIndex];
        }

        return hostPart.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (hostPart.Contains('.', StringComparison.Ordinal) && Uri.CheckHostName(hostPart) != UriHostNameType.Unknown);
    }

    /// <summary>
    /// scheme 位是否其实是「主机形」输入。无 scheme 前缀的 host[:port] 会被 Uri
    /// 解析成 scheme=host（"localhost:8080" → scheme=localhost），这类要放行；
    /// 其余（mailto / tel / file 等真协议名）按不可导航处理。
    /// </summary>
    private static bool SchemeLooksLikeHost(string scheme)
    {
        return scheme.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || scheme.Contains('.');
    }
}
