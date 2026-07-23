using GenshinBrowser.Constants;
using GenshinBrowser.Browser;
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

        Assert.True(DownloadUriComparer.Matches(expected, "https://CDN.EXAMPLE.COM/a.zip?token=AbC&x=1"));
        Assert.True(DownloadUriComparer.Matches(expected, "https://cdn.example.com/a.zip?x=1&token=AbC"));
        Assert.True(DownloadUriComparer.Matches(
            "https://cdn.example.com/path/",
            "https://cdn.example.com/path"));
        Assert.False(DownloadUriComparer.Matches(expected, "https://cdn.example.com/b.zip?token=AbC&x=1"));
        Assert.False(DownloadUriComparer.Matches(expected, "https://cdn.example.com/a.zip?token=abc&x=1"));
    }
}
