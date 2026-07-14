using GenshinBrowser.Models;
using GenshinBrowser.Services;

namespace GenshinBrowser.Tests;

public sealed class DownloadsServiceTests
{
    [Fact]
    public async Task Load_ConvertsRunningDownloadToRetryableInterruptedRecord()
    {
        using var directory = new TestDirectory();
        var path = directory.GetPath("downloads.json");
        const string sourceUri = "https://cdn.example.com/a.zip?token=AbC&utm_source=required";
        await File.WriteAllTextAsync(path, $$"""
            [{
              "FileName":"a.zip",
              "SourceUri":"{{sourceUri}}",
              "FilePath":"C:\\Downloads\\a.zip",
              "TotalBytes":100,
              "ReceivedBytes":40,
              "State":0,
              "StartedAtUtc":"2026-07-14T00:00:00Z"
            }]
            """);

        using (var service = new DownloadsService(path))
        {
            var item = Assert.Single(service.Downloads);
            Assert.Equal(DownloadState.Interrupted, item.State);
            Assert.True(item.CanRetry);
            Assert.Equal(sourceUri, item.SourceUri);
            await service.FlushAsync();
        }

        using var reloaded = new DownloadsService(path);
        Assert.Equal(DownloadState.Interrupted, Assert.Single(reloaded.Downloads).State);
    }
}
