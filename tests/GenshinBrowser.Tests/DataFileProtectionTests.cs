using System.Text.Json;
using GenshinBrowser.Models;
using GenshinBrowser.Services;

namespace GenshinBrowser.Tests;

/// <summary>
/// 数据文件读不出来时不能当成空文件继续：否则之后第一次保存会把原文件整个覆盖掉（浏览记录在启动后第一个页面加载完就会写盘）。
/// </summary>
public sealed class DataFileProtectionTests
{
    private const string Corrupted = "[{\"Url\":\"https://www.bilibili.com/video/BV1\",\"Title\":";

    [Fact]
    public async Task History_CorruptedFileIsPreservedAndNotOverwritten()
    {
        using var directory = new TestDirectory();
        var path = directory.GetPath("history.json");
        File.WriteAllText(path, Corrupted);

        using (var service = new HistoryService(path))
        {
            await service.InitializeAsync();
            var preserved = AssertPreserved(service.LoadProblem, "history.json");

            await service.AddEntryAsync("https://example.com/new", "new");
            await service.FlushAsync();

            Assert.Equal(Corrupted, File.ReadAllText(preserved));
        }

        using var reloaded = new HistoryService(path);
        await reloaded.InitializeAsync();
        Assert.Null(reloaded.LoadProblem);
        Assert.Equal("https://example.com/new", Assert.Single(reloaded.GetEntries()).Url);
    }

    [Fact]
    public async Task Favorites_CorruptedFileIsPreservedAndNotOverwritten()
    {
        using var directory = new TestDirectory();
        var path = directory.GetPath("favorites.json");
        File.WriteAllText(path, Corrupted);

        using var service = new FavoritesService(path);
        await service.InitializeAsync();
        var preserved = AssertPreserved(service.LoadProblem, "favorites.json");

        await service.AddOrUpdateAsync("https://example.com/new", "new");
        await service.FlushAsync();

        Assert.Equal(Corrupted, File.ReadAllText(preserved));
        Assert.Contains("example.com/new", File.ReadAllText(path));
    }

    [Fact]
    public async Task Downloads_CorruptedFileIsPreserved()
    {
        using var directory = new TestDirectory();
        var path = directory.GetPath("downloads.json");
        File.WriteAllText(path, "{not json");

        using var service = new DownloadsService(path);
        await service.InitializeAsync();

        var preserved = AssertPreserved(service.LoadProblem, "downloads.json");
        Assert.Empty(service.Downloads);
        Assert.Equal("{not json", File.ReadAllText(preserved));
    }

    [Fact]
    public async Task Settings_CorruptedFileIsPreservedAndDefaultsAreUsed()
    {
        using var directory = new TestDirectory();
        var path = directory.GetPath("settings.json");
        File.WriteAllText(path, "{\"WindowOpacity\": ");

        using var service = new SettingsService(path);
        var settings = service.Load();
        var preserved = AssertPreserved(service.LoadProblem, "settings.json");
        Assert.Equal(new AppSettings().WindowOpacity, settings.WindowOpacity);

        settings.WindowOpacity = 0.5;
        await service.SaveAsync(settings);

        Assert.Equal("{\"WindowOpacity\": ", File.ReadAllText(preserved));
        Assert.Equal(0.5, service.Load().WindowOpacity);
    }

    [Fact]
    public async Task History_LockedFileBlocksWritesInsteadOfOverwriting()
    {
        using var directory = new TestDirectory();
        var path = directory.GetPath("history.json");
        var original = JsonSerializer.Serialize(new[] { new HistoryEntry { Url = "https://example.com/old", Title = "old", VisitedAt = DateTime.UtcNow } });
        File.WriteAllText(path, original);

        using var service = new HistoryService(path);
        // 独占打开：读不了，也改不了名（模拟被其它程序占用）
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await service.InitializeAsync();
            Assert.NotNull(service.LoadProblem);
            Assert.Null(service.LoadProblem!.PreservedPath);
        }

        await service.AddEntryAsync("https://example.com/new", "new");
        await service.FlushAsync();
        // 防抖写盘也不应发生
        await Task.Delay(AppConfigDebounceMargin);

        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public async Task Settings_LockedFileBlocksWrites()
    {
        using var directory = new TestDirectory();
        var path = directory.GetPath("settings.json");
        File.WriteAllText(path, "{\"WindowOpacity\": 0.7}");

        using var service = new SettingsService(path);
        AppSettings settings;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            settings = service.Load();
        }

        Assert.Null(service.LoadProblem!.PreservedPath);
        settings.WindowOpacity = 0.3;
        await service.SaveAsync(settings);
        Assert.Equal("{\"WindowOpacity\": 0.7}", File.ReadAllText(path));
    }

    [Fact]
    public void Entries_DoNotSerializeInterfaceTimestamp()
    {
        // Timestamp 是显式接口实现：落盘字段名保持 VisitedAt / SavedAt（WinUI 分支按字段名读）
        var history = JsonSerializer.Serialize(new HistoryEntry { Url = "https://a.example", Title = "a" });
        var favorite = JsonSerializer.Serialize(new FavoriteEntry { Url = "https://a.example", Title = "a" });

        Assert.DoesNotContain("Timestamp", history);
        Assert.Contains("VisitedAt", history);
        Assert.DoesNotContain("Timestamp", favorite);
        Assert.Contains("SavedAt", favorite);
    }

    // 浏览记录防抖 500ms，多等一点确认没有迟到的写盘
    private static readonly TimeSpan AppConfigDebounceMargin = TimeSpan.FromMilliseconds(800);

    private static string AssertPreserved(DataFileProblem? problem, string fileName)
    {
        Assert.NotNull(problem);
        Assert.Equal(fileName, problem!.FileName);
        Assert.NotNull(problem.PreservedPath);
        Assert.True(File.Exists(problem.PreservedPath), problem.PreservedPath);
        Assert.StartsWith(Path.GetFileNameWithoutExtension(fileName) + ".broken-", Path.GetFileName(problem.PreservedPath));
        return problem.PreservedPath!;
    }
}
