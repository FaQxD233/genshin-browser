using GenshinBrowser.Services;

namespace GenshinBrowser.Tests;

public sealed class WebViewDataSizeCalculatorTests
{
    [Fact]
    public void Calculate_CountsOnlyDiskCacheData()
    {
        using var directory = new TestDirectory();
        WriteBytes(directory.GetPath("EBWebView/Default/Cache/cache.bin"), 11);
        WriteBytes(directory.GetPath("EBWebView/Default/IndexedDB/data.bin"), 13);
        WriteBytes(directory.GetPath("EBWebView/Default/IndexedDB/Cache/nested-cache.bin"), 47);
        WriteBytes(directory.GetPath("EBWebView/Default/Service Worker/cache.bin"), 17);
        WriteBytes(directory.GetPath("EBWebView/Default/Service Worker/Cache/nested-cache.bin"), 53);
        WriteBytes(directory.GetPath("EBWebView/Default/Local Storage/data.bin"), 23);
        WriteBytes(directory.GetPath("EBWebView/Default/Session Storage/data.bin"), 29);
        WriteBytes(directory.GetPath("EBWebView/Default/WebStorage/data.bin"), 31);
        WriteBytes(directory.GetPath("EBWebView/Default/File System/data.bin"), 37);
        WriteBytes(directory.GetPath("EBWebView/Default/blob_storage/data.bin"), 41);
        WriteBytes(directory.GetPath("EBWebView/Default/AutofillAiModelCache/model.bin"), 19);
        WriteBytes(directory.GetPath("EBWebView/Default/Login Data"), 43);

        var result = WebViewDataSizeCalculator.Calculate(directory.Path, CancellationToken.None);

        Assert.Equal(11, result);
    }

    [Fact]
    public void Calculate_ObservesCancellation()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(directory.GetPath("EBWebView/Default/Cache"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            WebViewDataSizeCalculator.Calculate(directory.Path, cts.Token));
    }

    private static void WriteBytes(string path, int count)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[count]);
    }
}
