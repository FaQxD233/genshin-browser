using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using GenshinBrowser.Models;
using GenshinBrowser.Services;
using GenshinBrowser.Utils;

namespace GenshinBrowser.Tests;

public sealed class ConfigBackupServiceTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ExportThenRead_RoundTripsSettingsFavoritesAndHistory()
    {
        using var directory = new TestDirectory();
        var path = directory.GetPath("config.json");
        var settings = new AppSettings
        {
            ThemeMode = "Light",
            Language = "en-US",
            WindowOpacity = 0.7,
            ZoomFactor = 1.25,
            WindowLeft = 20,
            WindowTop = 30,
            WindowWidth = 800,
            WindowHeight = 450,
            ToggleModeKey = Key.F6,
            HotkeyScope = HotkeyScope.AllApps,
            FloatingClickThrough = true,
            LastUrl = "https://www.bilibili.com/video/BV1keqing01",
            WindowMode = WindowMode.Fixed,
            HasSeenFloatingModeHint = true,
        };
        var favorites = new[]
        {
            new FavoriteEntry { Url = "https://www.bilibili.com/video/BV1keqing01", Title = "【原神】刻晴 培养攻略", SavedAt = T0 },
            new FavoriteEntry { Url = "https://www.bilibili.com/video/BV1ganyu02", Title = "甘雨", SavedAt = T0.AddDays(-1) },
        };
        var history = new[]
        {
            new HistoryEntry { Url = "https://www.bilibili.com/video/BV1keqing01", Title = "【原神】刻晴 培养攻略", VisitedAt = T0 },
        };

        await ConfigBackupService.WriteAsync(path, ConfigBackupService.Create(settings, favorites, history, T0));
        var bundle = await ConfigBackupService.ReadAsync(path);

        var imported = Assert.IsType<AppSettings>(bundle.Settings);
        Assert.Equal("Light", imported.ThemeMode);
        Assert.Equal("en-US", imported.Language);
        Assert.Equal(0.7, imported.WindowOpacity);
        Assert.Equal(1.25, imported.ZoomFactor);
        Assert.Equal((20d, 30d, 800d, 450d), (imported.WindowLeft, imported.WindowTop, imported.WindowWidth, imported.WindowHeight));
        Assert.Equal(Key.F6, imported.ToggleModeKey);
        Assert.Equal(HotkeyScope.AllApps, imported.HotkeyScope);
        Assert.True(imported.FloatingClickThrough);
        Assert.Equal("https://www.bilibili.com/video/BV1keqing01", imported.LastUrl);
        // 本机运行状态不随配置迁移
        Assert.Equal(WindowMode.Free, imported.WindowMode);
        Assert.False(imported.HasSeenFloatingModeHint);

        Assert.Equal(favorites.Select(e => (e.Url, e.Title, e.SavedAt)), bundle.Favorites!.Select(e => (e.Url, e.Title, e.SavedAt)));
        Assert.Equal(history.Select(e => (e.Url, e.Title, e.VisitedAt)), bundle.History!.Select(e => (e.Url, e.Title, e.VisitedAt)));
        // 中文标题不转义，记事本里能直接看
        Assert.Contains("刻晴", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("{}", ConfigImportError.NotConfigFile)]
    [InlineData("{\"WindowWidth\": 800}", ConfigImportError.NotConfigFile)]
    [InlineData("{\"Format\": \"GenshinBrowser.Config\", \"Version\": 0}", ConfigImportError.NotConfigFile)]
    [InlineData("{\"Format\": \"GenshinBrowser.Config\", \"Version\": 2}", ConfigImportError.NewerVersion)]
    [InlineData("{\"Format\": \"GenshinBrowser.Config\", \"Version\": 1, \"Settings\": {\"WindowWidth\": \"wide\"}}", ConfigImportError.Corrupted)]
    [InlineData("{\"Format\": ", ConfigImportError.Corrupted)]
    [InlineData("[1, 2]", ConfigImportError.Corrupted)]
    public void Parse_RejectsUnusableFilesWithReason(string json, ConfigImportError expected)
    {
        var error = Assert.Throws<ConfigImportException>(() => ConfigBackupService.Parse(json));
        Assert.Equal(expected, error.Error);
    }

    [Fact]
    public async Task ReadAsync_RejectsOversizedFileBeforeParsing()
    {
        using var directory = new TestDirectory();
        var path = directory.GetPath("huge.json");
        File.WriteAllText(path, new string(' ', 2048));

        var error = await Assert.ThrowsAsync<ConfigImportException>(() => ConfigBackupService.ReadAsync(path, maxBytes: 1024));
        Assert.Equal(ConfigImportError.TooLarge, error.Error);
    }

    [Fact]
    public void Parse_SanitizesImportedValuesAndSkipsMissingSections()
    {
        var json = JsonSerializer.Serialize(new ConfigBundle
        {
            Format = ConfigBundle.FormatName,
            Version = ConfigBundle.CurrentVersion,
            Settings = new AppSettings
            {
                WindowOpacity = 5,
                WindowWidth = 99_999,
                ToggleModeKey = Key.K,
                TogglePlaybackKey = Key.K,
            },
            Favorites =
            [
                new FavoriteEntry { Url = "javascript:alert(1)", Title = "bad" },
                new FavoriteEntry { Url = "https://www.bilibili.com/video/BV1", Title = "first", SavedAt = T0 },
                new FavoriteEntry { Url = "https://WWW.bilibili.com/video/BV1", Title = "duplicate", SavedAt = T0 },
            ],
        });

        var bundle = ConfigBackupService.Parse(json);

        var settings = Assert.IsType<AppSettings>(bundle.Settings);
        Assert.Equal(1.0, settings.WindowOpacity);
        Assert.Equal(new AppSettings().WindowWidth, settings.WindowWidth);
        Assert.NotEqual(
            (settings.ToggleModeKey, settings.ToggleModeModifiers),
            (settings.TogglePlaybackKey, settings.TogglePlaybackModifiers));
        var favorite = Assert.Single(bundle.Favorites!);
        Assert.Equal("first", favorite.Title);
        Assert.Null(bundle.History);
    }

    [Fact]
    public void MergeByUrl_KeepsNewerEntryPerUrlNewestFirstAndCapped()
    {
        var current = new[] { Entry("https://a.example/", 1), Entry("https://b.example/", 5) };
        var incoming = new[] { Entry("https://B.example/", 3), Entry("https://c.example/", 4) };

        var merged = EntryMerge.MergeByUrl(current, incoming, e => e.Url, e => e.VisitedAt, maxCount: 10);
        Assert.Equal(["https://b.example/", "https://c.example/", "https://a.example/"], merged.Select(e => e.Url));
        Assert.Equal(T0.AddHours(5), merged[0].VisitedAt);

        var capped = EntryMerge.MergeByUrl(current, incoming, e => e.Url, e => e.VisitedAt, maxCount: 2);
        Assert.Equal(["https://b.example/", "https://c.example/"], capped.Select(e => e.Url));

        // 覆盖会替换掉的：现有里有、导入里没有的网址
        Assert.Equal(1, EntryMerge.CountMissing(current, incoming, e => e.Url));
    }

    [Fact]
    public async Task FavoritesImport_MergesOrReplacesAndFlushesToDisk()
    {
        using var directory = new TestDirectory();
        var path = directory.GetPath("favorites.json");
        using (var service = new FavoritesService(path))
        {
            await service.InitializeAsync();
            await service.AddOrUpdateAsync("https://www.bilibili.com/video/BV1a", "A");

            await service.ImportAsync(
                [new FavoriteEntry { Url = "https://www.bilibili.com/video/BV1b", Title = "B", SavedAt = T0 }],
                merge: true);
            Assert.Equal(2, service.GetEntries().Count);
            Assert.True(service.Contains("https://www.bilibili.com/video/BV1a"));

            await service.ImportAsync(
                [new FavoriteEntry { Url = "https://www.bilibili.com/video/BV1c", Title = "C", SavedAt = T0 }],
                merge: false);
            Assert.Equal("C", Assert.Single(service.GetEntries()).Title);
            Assert.False(service.Contains("https://www.bilibili.com/video/BV1a"));
        }

        using var reloaded = new FavoritesService(path);
        await reloaded.InitializeAsync();
        Assert.Equal("C", Assert.Single(reloaded.GetEntries()).Title);
    }

    [Fact]
    public void WindowBoundsChange_ComparesRoundedSizeAndOnlyUsablePositions()
    {
        var current = new Rect(100, 100, 658, 370);

        // 只差小数（DPI 取整）：不算不同，不必询问
        Assert.False(WindowBoundsChange.Create(current, new Rect(100.4, 100.2, 658.3, 369.8), true, true).HasDifference);

        var moved = WindowBoundsChange.Create(current, new Rect(300, 100, 658, 370), true, true);
        Assert.True(moved.PositionDiffers);
        Assert.False(moved.SizeDiffers);

        // 位置在所有屏幕外：只导入大小，位置不算不同
        var offScreen = WindowBoundsChange.Create(current, new Rect(-30_000, 0, 800, 450), true, false);
        Assert.False(offScreen.PositionDiffers);
        Assert.True(offScreen.ImportedPositionOffScreen);
        Assert.True(offScreen.SizeDiffers);

        // 文件里没有位置（控制台从没拖动过）
        var noPosition = WindowBoundsChange.Create(current, new Rect(-1, -1, 658, 370), false, false);
        Assert.False(noPosition.HasDifference);
        Assert.False(noPosition.ImportedPositionOffScreen);
    }

    private static HistoryEntry Entry(string url, int hour) => new() { Url = url, Title = url, VisitedAt = T0.AddHours(hour) };
}
