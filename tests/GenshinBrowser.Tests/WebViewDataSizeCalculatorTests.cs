using GenshinBrowser.Services;

namespace GenshinBrowser.Tests;

public sealed class WebViewDataSizeCalculatorTests
{
    [Fact]
    public void Calculate_CountsOnlyReclaimableWebViewData()
    {
        using var directory = new TestDirectory();
        WriteBytes(directory.GetPath("EBWebView/Default/Cache/cache.bin"), 11);
        WriteBytes(directory.GetPath("EBWebView/Default/IndexedDB/data.bin"), 13);
        WriteBytes(directory.GetPath("EBWebView/Default/Service Worker/cache.bin"), 17);
        WriteBytes(directory.GetPath("EBWebView/Default/AutofillAiModelCache/model.bin"), 19);
        WriteBytes(directory.GetPath("EBWebView/Default/Login Data"), 23);

        var result = WebViewDataSizeCalculator.Calculate(directory.Path, CancellationToken.None);

        Assert.Equal(41, result);
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
