using GenshinBrowser.Constants;
using GenshinBrowser.Services;
using GenshinBrowser.Utils;

namespace GenshinBrowser.Tests;

public sealed class UrlTests
{
    [Fact]
    public void Normalize_StripsBilibiliOnlyParametersOnlyOnBilibiliHosts()
    {
        var generic = UrlNormalizer.Normalize(
            "https://example.com/watch?from=feed&t=42&utm_source=ad#part");
        var bilibili = UrlNormalizer.Normalize(
            "https://www.bilibili.com/video/BV1?from=feed&t=42&foo=bar&utm_source=ad");

        Assert.Contains("from=feed", generic);
        Assert.Contains("t=42", generic);
        Assert.DoesNotContain("utm_source", generic, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("#part", generic);
        Assert.DoesNotContain("from=", bilibili, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("t=", bilibili, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("foo=bar", bilibili);
    }

    [Fact]
    public void ValidateHttpUrl_PreservesSignedDownloadQuery()
    {
        const string source = "https://cdn.example.com/a.zip?token=AbC&utm_source=required";

        Assert.True(EntryText.TryValidateHttpUrl(source, out var validated));
        Assert.Equal(source, validated);
        Assert.Null(NavigationTarget.Build(new string('x', AppConfig.Data.MaxEntryUrlLength + 1)));
    }

    [Fact]
    public void DownloadRetryUriMatch_AllowsHostCaseAndQueryReorder()
    {
        const string expected = "https://cdn.example.com/a.zip?token=AbC&x=1";

        Assert.True(MainWindow.DownloadUrisMatch(expected, "https://CDN.EXAMPLE.COM/a.zip?token=AbC&x=1"));
        Assert.True(MainWindow.DownloadUrisMatch(expected, "https://cdn.example.com/a.zip?x=1&token=AbC"));
        Assert.True(MainWindow.DownloadUrisMatch(
            "https://cdn.example.com/path/",
            "https://cdn.example.com/path"));
        Assert.False(MainWindow.DownloadUrisMatch(expected, "https://cdn.example.com/b.zip?token=AbC&x=1"));
        Assert.False(MainWindow.DownloadUrisMatch(expected, "https://cdn.example.com/a.zip?token=abc&x=1"));
    }

    [Fact]
    public void Build_NavigatesIpv6LiteralsAndUserinfoHosts()
    {
        // [IPv6] 字面量（可带端口/路径）应导航而非搜索
        var v6 = NavigationTarget.Build("[::1]:8080");
        Assert.NotNull(v6);
        Assert.StartsWith("https://[::1]:8080", v6, StringComparison.OrdinalIgnoreCase);

        Assert.NotNull(NavigationTarget.Build("[2001:db8::1]"));
        Assert.NotNull(NavigationTarget.Build("[::1]/index.html"));

        // userinfo 前缀：取 @ 之后的主机判定
        var userinfo = NavigationTarget.Build("user@example.com:8080");
        Assert.NotNull(userinfo);
        Assert.StartsWith("https://user@example.com:8080", userinfo, StringComparison.OrdinalIgnoreCase);

        // 括号不配对 / 括号内容为空：不当作地址，落入搜索
        Assert.StartsWith("https://search.bilibili.com/all?keyword=", NavigationTarget.Build("[oops"));
        Assert.StartsWith("https://search.bilibili.com/all?keyword=", NavigationTarget.Build("[]"));
    }

    [Fact]
    public void Build_HostPortWithoutSchemeStillNavigates()
    {
        // host[:port]（无 scheme）会被 Uri 解析进 scheme 位，必须放行给主机判定
        var localhost = NavigationTarget.Build("localhost:8080");
        Assert.NotNull(localhost);
        Assert.StartsWith("https://localhost:8080", localhost, StringComparison.OrdinalIgnoreCase);

        Assert.NotNull(NavigationTarget.Build("example.com:8080"));
    }

    [Fact]
    public void Build_NonHttpSchemesWithoutDelimiterAreNotNavigable()
    {
        // mailto:/tel: 等无 :// 的真协议不再落入搜索，与注释语义一致
        Assert.Null(NavigationTarget.Build("mailto:foo@bar.com"));
        Assert.Null(NavigationTarget.Build("tel:+8613800000000"));
        // 含 :// 的未知协议保持原行为
        Assert.Null(NavigationTarget.Build("notes://whatever"));
    }
}
