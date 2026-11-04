using System.Text.Json;
using GenshinBrowser.Models;
using GenshinBrowser.Services;
using GenshinBrowser.Utils;

namespace GenshinBrowser.Tests;

public sealed class PlaybackResumeTests
{
    [Theory]
    [InlineData("https://www.bilibili.com/video/BV1BMgj6MEz4", true)]
    [InlineData("https://www.bilibili.com/video/BV1BMgj6MEz4?p=12", true)]
    [InlineData("https://m.bilibili.com/video/BV1BMgj6MEz4", true)]
    [InlineData("https://www.bilibili.com/bangumi/play/ep123456", true)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", true)]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ&list=PL1", true)]
    [InlineData("https://www.youtube.com/watch", false)]
    [InlineData("https://www.youtube.com/@channel", false)]
    [InlineData("https://search.bilibili.com/all?keyword=原神", false)]
    [InlineData("https://live.bilibili.com/123", false)]
    [InlineData("https://www.bilibili.com/", false)]
    [InlineData("https://example.com/video/1", false)]
    [InlineData("not a url", false)]
    public void SupportsTimeParameter_OnlyVideoPagesOfBilibiliAndYoutube(string url, bool expected)
    {
        Assert.Equal(expected, PlaybackResume.SupportsTimeParameter(url));
    }

    [Theory]
    [InlineData("https://www.bilibili.com/video/BV1x", 754.9, "https://www.bilibili.com/video/BV1x?t=754")]
    [InlineData("https://www.bilibili.com/video/BV1x?p=12", 754, "https://www.bilibili.com/video/BV1x?p=12&t=754")]
    [InlineData("https://www.bilibili.com/video/BV1x?t=30&p=12", 754, "https://www.bilibili.com/video/BV1x?p=12&t=754")]
    [InlineData("https://www.bilibili.com/video/BV1x?p=2#reply123", 60, "https://www.bilibili.com/video/BV1x?p=2&t=60#reply123")]
    [InlineData("https://www.youtube.com/watch?v=abc&t=30s", 754, "https://www.youtube.com/watch?v=abc&t=754")]
    public void WithTime_AddsOrReplacesTimeParameter(string url, double seconds, string expected)
    {
        Assert.Equal(expected, PlaybackResume.WithTime(url, seconds));
    }

    [Theory]
    [InlineData("https://search.bilibili.com/all?keyword=x", 754)]
    [InlineData("https://www.bilibili.com/video/BV1x", 0.5)]
    [InlineData("https://www.bilibili.com/video/BV1x", double.NaN)]
    public void WithTime_LeavesUnsupportedOrMeaninglessInputUnchanged(string url, double seconds)
    {
        Assert.Equal(url, PlaybackResume.WithTime(url, seconds));
    }

    [Theory]
    [InlineData(5, 600, false)]
    [InlineData(10, 600, true)]
    [InlineData(300, 600, true)]
    [InlineData(585, 600, true)]
    [InlineData(590, 600, false)]
    [InlineData(300, double.PositiveInfinity, false)]
    [InlineData(300, double.NaN, false)]
    public void ShouldRemember_SkipsStartEndAndLiveStreams(double position, double duration, bool expected)
    {
        Assert.Equal(expected, PlaybackResume.ShouldRemember(position, duration));
    }

    [Fact]
    public void Normalize_StripsYoutubeTimeButKeepsVideoAndList()
    {
        var normalized = UrlNormalizer.Normalize("https://www.youtube.com/watch?v=abc&list=PL1&t=754&utm_source=x");

        Assert.Equal("https://www.youtube.com/watch?v=abc&list=PL1", normalized);
        Assert.Equal("https://youtu.be/abc", UrlNormalizer.Normalize("https://youtu.be/abc?t=30"));
    }

    [Fact]
    public void SettingsLoad_KeepsValidPlaybackPositionAndDropsInvalidOnes()
    {
        using var directory = new TestDirectory();
        var path = directory.GetPath("settings.json");
        using var service = new SettingsService(path);

        File.WriteAllText(path, JsonSerializer.Serialize(new AppSettings
        {
            LastPlaybackUrl = "https://www.bilibili.com/video/BV1x?p=2&spm_id_from=333.1007&t=30",
            LastPlaybackSeconds = 754,
        }));
        var valid = service.Load();
        Assert.Equal("https://www.bilibili.com/video/BV1x?p=2", valid.LastPlaybackUrl);
        Assert.Equal(754, valid.LastPlaybackSeconds);

        foreach (var (url, seconds) in new[]
                 {
                     ("https://search.bilibili.com/all?keyword=x", 754d),
                     ("https://www.bilibili.com/video/BV1x", -1d),
                     ("javascript:alert(1)", 754d),
                 })
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new AppSettings { LastPlaybackUrl = url, LastPlaybackSeconds = seconds }));
            var cleared = service.Load();
            Assert.Equal((string.Empty, 0d), (cleared.LastPlaybackUrl, cleared.LastPlaybackSeconds));
        }
    }

    [Fact]
    public async Task SettingsSave_PersistsPlaybackPosition()
    {
        // 快照按字段手工拷贝：新字段漏拷会静默丢失
        using var directory = new TestDirectory();
        var path = directory.GetPath("settings.json");
        using (var service = new SettingsService(path))
        {
            var settings = service.Load();
            settings.LastPlaybackUrl = "https://www.bilibili.com/video/BV1x?p=2";
            settings.LastPlaybackSeconds = 321;
            await service.SaveAsync(settings);
        }

        using var reloaded = new SettingsService(path);
        var loaded = reloaded.Load();
        Assert.Equal(("https://www.bilibili.com/video/BV1x?p=2", 321d), (loaded.LastPlaybackUrl, loaded.LastPlaybackSeconds));
    }

    [Fact]
    public void ConfigExport_DropsPlaybackPosition()
    {
        var settings = new AppSettings
        {
            LastPlaybackUrl = "https://www.bilibili.com/video/BV1x",
            LastPlaybackSeconds = 321,
        };

        var bundle = ConfigBackupService.Create(settings, Array.Empty<FavoriteEntry>(), Array.Empty<HistoryEntry>(), DateTime.UtcNow);

        Assert.Equal((string.Empty, 0d), (bundle.Settings!.LastPlaybackUrl, bundle.Settings.LastPlaybackSeconds));
    }
}
