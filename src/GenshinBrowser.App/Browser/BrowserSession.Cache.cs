using GenshinBrowser.Constants;
using GenshinBrowser.Services;

namespace GenshinBrowser.Browser;

internal sealed partial class BrowserSession
{
    private void TryScheduleAutoCacheCheck()
    {
        if (cacheCheckTask is not null || shuttingDown)
        {
            return;
        }

        DateTime lastCheck = settings.LastWebView2CacheCheckUtc;
        if (lastCheck != DateTime.MinValue && DateTime.UtcNow - lastCheck < TimeSpan.FromHours(24))
        {
            return;
        }

        cacheCheckCts = new CancellationTokenSource();
        cacheCheckTask = CheckCacheSizeAsync(cacheCheckCts.Token);
    }

    private async Task CheckCacheSizeAsync(CancellationToken cancellationToken)
    {
        try
        {
            long? bytes = await WebViewDataSizeCalculator.CalculateAsync(
                environmentProvider.UserDataFolder,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes is null)
            {
                return;
            }
            await dispatcher.InvokeAsync(async () =>
            {
                if (shuttingDown)
                {
                    return;
                }

                if (bytes >= AppConfig.Data.WebView2CacheThresholdBytes)
                {
                    await ClearBrowsingDataAsync(silent: true).ConfigureAwait(true);
                    return;
                }

                settings.LastWebView2CacheCheckUtc = DateTime.UtcNow;
                QueueSettingsSave();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Check WebView2 cache size");
        }
    }
}
