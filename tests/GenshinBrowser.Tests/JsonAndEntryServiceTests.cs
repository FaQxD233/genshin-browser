using System.Text.Json;
using System.Windows.Input;
using GenshinBrowser.Constants;
using GenshinBrowser.Models;
using GenshinBrowser.Services;

namespace GenshinBrowser.Tests;

public sealed class JsonAndEntryServiceTests
{
    [Fact]
    public void ReadAllTextBounded_RejectsFileBeforeReadingPastLimit()
    {
        using var directory = new TestDirectory();
        var path = directory.GetPath("oversized.json");
        File.WriteAllBytes(path, new byte[AppConfig.Data.MaxSettingsFileSizeBytes + 1]);

        Assert.Throws<InvalidDataException>(() =>
            JsonFileWriter.ReadAllTextBounded(path, AppConfig.Data.MaxSettingsFileSizeBytes));
    }

    [Fact]
    public async Task SettingsHistoryAndFavorites_AreSanitizedAndFlushed()
    {
        using var directory = new TestDirectory();
        var settingsPath = directory.GetPath("settings.json");
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new AppSettings
        {
            LastUrl = "javascript:alert(1)",
            ControlWindowLeft = -1920,
            ControlWindowTop = -200,
            WindowOpacity = 5,
            ZoomFactor = 10,
        }));

        using (var settingsService = new SettingsService(settingsPath))
        {
            var settings = settingsService.Load();
            Assert.True(settings.HasControlWindowPosition);
            Assert.Equal(-1920, settings.ControlWindowLeft);
            Assert.Equal(string.Empty, settings.LastUrl);
            Assert.Equal(1, settings.WindowOpacity);
            Assert.Equal(5, settings.ZoomFactor);

            settings.ZoomFactor = 1.75;
            await settingsService.SaveAsync(settings);
        }

        using (var reloadedSettingsService = new SettingsService(settingsPath))
        {
            Assert.Equal(1.75, reloadedSettingsService.Load().ZoomFactor);
        }

        var historyPath = directory.GetPath("history.json");
        File.WriteAllText(historyPath, JsonSerializer.Serialize(new HistoryEntry?[]
        {
            null,
            new() { Url = "file:///invalid", Title = "invalid" },
            new() { Url = "https://www.bilibili.com/video/BV1?from=share", Title = new string('x', 300) },
            new() { Url = "https://www.bilibili.com/video/BV1", Title = "duplicate" },
        }));
        using (var historyService = new HistoryService(historyPath))
        {
            Assert.Single(historyService.GetEntries());
            Assert.Equal(AppConfig.Data.MaxEntryTitleLength, historyService.GetEntries()[0].Title.Length);
            await historyService.FlushAsync();
        }

        var favoritesPath = directory.GetPath("favorites.json");
        File.WriteAllText(favoritesPath, JsonSerializer.Serialize(new FavoriteEntry?[]
        {
            null,
            new() { Url = "data:text/plain,invalid", Title = "invalid" },
            new() { Url = "https://example.com/item?utm_source=ad", Title = "item" },
        }));
        using var favoritesService = new FavoritesService(favoritesPath);
        Assert.Single(favoritesService.GetEntries());
        Assert.DoesNotContain("utm_source", favoritesService.GetEntries()[0].Url);
        await favoritesService.FlushAsync();
    }

    [Fact]
    public void SettingsSanitize_ResolvesHotkeyConflictWhenModeIsDefaultPlayback()
    {
        using var directory = new TestDirectory();
        var settingsPath = directory.GetPath("settings.json");
        // 模式键 = 默认播放键 K，播放键也是 K → 旧逻辑重置播放键后仍冲突
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new AppSettings
        {
            ToggleModeKey = Key.K,
            ToggleModeModifiers = ModifierKeys.None,
            TogglePlaybackKey = Key.K,
            TogglePlaybackModifiers = ModifierKeys.None,
        }));

        using var settingsService = new SettingsService(settingsPath);
        var settings = settingsService.Load();

        Assert.NotEqual(
            (settings.ToggleModeKey, settings.ToggleModeModifiers),
            (settings.TogglePlaybackKey, settings.TogglePlaybackModifiers));
        // 播放键应回到默认 K，模式键应回到默认 F8
        Assert.Equal(Key.F8, settings.ToggleModeKey);
        Assert.Equal(Key.K, settings.TogglePlaybackKey);
    }

    [Fact]
    public void SettingsSanitize_ResetsPlaybackWhenOnlyPlaybackConflictsWithMode()
    {
        using var directory = new TestDirectory();
        var settingsPath = directory.GetPath("settings.json");
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new AppSettings
        {
            ToggleModeKey = Key.F7,
            ToggleModeModifiers = ModifierKeys.None,
            TogglePlaybackKey = Key.F7,
            TogglePlaybackModifiers = ModifierKeys.None,
        }));

        using var settingsService = new SettingsService(settingsPath);
        var settings = settingsService.Load();

        Assert.Equal(Key.F7, settings.ToggleModeKey);
        Assert.Equal(Key.K, settings.TogglePlaybackKey);
    }

    [Fact]
    public async Task FileLogger_FlushAsyncReturnsQuicklyAndHandlesLogEntries()
    {
        using var directory = new TestDirectory();
        var previousOverride = FileLogger.LogRootOverride;
        FileLogger.LogRootOverride = directory.Path;
        try
        {
            FileLogger.LogDebug("Test message 1");
            FileLogger.LogException(new InvalidOperationException("Test exception"), "Unit Test");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await FileLogger.FlushAsync(2000);
            sw.Stop();

            // 计数泄漏会死等满超时；正常应在毫秒级完成。
            Assert.True(sw.ElapsedMilliseconds < 1500,
                $"FlushAsync took {sw.ElapsedMilliseconds}ms, expected < 1500ms");

            var logFile = System.IO.Path.Combine(directory.Path, $"{DateTime.Now:yyyy-MM-dd}.log");
            Assert.True(System.IO.File.Exists(logFile), $"Log file should exist at {logFile}");
            var content = await System.IO.File.ReadAllTextAsync(logFile);
            Assert.Contains("Test message 1", content);
            Assert.Contains("Test exception", content);
            Assert.Contains("InvalidOperationException", content);
        }
        finally
        {
            FileLogger.LogRootOverride = previousOverride;
        }
    }

    [Fact]
    public async Task FileLogger_FlushAsyncReturnsQuicklyUnderFloodWithoutCountLeak()
    {
        // 复现 a7729f8 引入的计数泄漏：1000 容量被打满后 DropWrite 丢弃的条目
        // 若不通过 ItemDropped 回滚 _pendingCount，FlushAsync 会死等满超时。
        using var directory = new TestDirectory();
        var previousOverride = FileLogger.LogRootOverride;
        FileLogger.LogRootOverride = directory.Path;
        try
        {
            // 远超队列容量 1000，迫使 DropWrite 触发。
            for (var i = 0; i < 5000; i++)
            {
                FileLogger.LogDebug($"Flood-{i}");
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await FileLogger.FlushAsync(2000);
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 1500,
                $"FlushAsync took {sw.ElapsedMilliseconds}ms under flood, expected < 1500ms (count leak?)");
        }
        finally
        {
            FileLogger.LogRootOverride = previousOverride;
        }
    }
}
