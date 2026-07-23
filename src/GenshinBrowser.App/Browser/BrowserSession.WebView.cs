using GenshinBrowser.Models;
using GenshinBrowser.Interop;
using GenshinBrowser.Services;
using GenshinBrowser.Utils;
using Microsoft.Web.WebView2.Core;

namespace GenshinBrowser.Browser;

internal sealed partial class BrowserSession
{
    private void AttachWebViewEvents()
    {
        webView.NavigationStarting += WebViewOnNavigationStarting;
        webView.NavigationCompleted += WebViewOnNavigationCompleted;
        if (core is null)
        {
            return;
        }

        core.DocumentTitleChanged += CoreOnDocumentTitleChanged;
        core.SourceChanged += CoreOnSourceChanged;
        core.NewWindowRequested += CoreOnNewWindowRequested;
        core.HistoryChanged += CoreOnHistoryChanged;
        core.DownloadStarting += CoreOnDownloadStarting;
        core.ProcessFailed += CoreOnProcessFailed;
    }

    private void DetachWebViewEvents()
    {
        webView.NavigationStarting -= WebViewOnNavigationStarting;
        webView.NavigationCompleted -= WebViewOnNavigationCompleted;
        if (core is null)
        {
            return;
        }

        core.DocumentTitleChanged -= CoreOnDocumentTitleChanged;
        core.SourceChanged -= CoreOnSourceChanged;
        core.NewWindowRequested -= CoreOnNewWindowRequested;
        core.HistoryChanged -= CoreOnHistoryChanged;
        core.DownloadStarting -= CoreOnDownloadStarting;
        core.ProcessFailed -= CoreOnProcessFailed;
    }

    private void WebViewOnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (shuttingDown || !ReferenceEquals(sender, webView))
        {
            return;
        }

        if (args.IsRedirected)
        {
            return;
        }

        SetNavigating(true);
        if (!string.IsNullOrWhiteSpace(args.Uri))
        {
            currentAddress = args.Uri;
        }

        SetStatus(
            text.Get("Status.Loading", "正在加载..."),
            StatusLevel.Info,
            BrowserStateChangeKind.Navigation);
    }

    private async void WebViewOnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (shuttingDown || !ReferenceEquals(sender, webView))
        {
            return;
        }

        SetNavigating(false);
        CaptureCurrentAddress();
        if (args.IsSuccess)
        {
            await RecordHistoryAsync(currentAddress, GetDocumentTitle()).ConfigureAwait(true);
            if (shuttingDown)
            {
                return;
            }
            SetStatus(
                text.Format(
                    "Status.PageLoaded",
                    "页面已加载。按 {0} 控制视频播放/暂停，按 {1} 切换浏览/浮窗。",
                    HotkeyFormatter.Format(settings.TogglePlaybackHotkey),
                    HotkeyFormatter.Format(settings.ToggleModeHotkey)),
                StatusLevel.Success,
                BrowserStateChangeKind.Navigation | BrowserStateChangeKind.History);
        }
        else
        {
            SetStatus(
                text.Format("Status.LoadFailed", "页面加载失败：{0}", args.WebErrorStatus),
                StatusLevel.Error,
                BrowserStateChangeKind.Navigation);
        }

        // A failed first navigation must still release a stale cache threshold;
        // defer the scan until navigation has settled, regardless of outcome.
        TryScheduleAutoCacheCheck();
    }

    private void CoreOnDocumentTitleChanged(object? sender, object args)
    {
        if (shuttingDown || !ReferenceEquals(sender, core))
        {
            return;
        }

        Notify(BrowserStateChangeKind.Title);
        ScheduleHistoryCapture();
    }

    private void CoreOnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs args)
    {
        if (shuttingDown || !ReferenceEquals(sender, core))
        {
            return;
        }

        if (CaptureCurrentAddress())
        {
            Notify(BrowserStateChangeKind.Navigation);
            ScheduleHistoryCapture();
        }
    }

    private void CoreOnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        if (!ReferenceEquals(sender, core))
        {
            return;
        }

        if (shuttingDown)
        {
            args.Handled = true;
            return;
        }

        if (Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? uri) && NavigationTarget.IsHttpOrHttps(uri))
        {
            args.Handled = true;
            NavigateTo(args.Uri);
        }
    }

    private void CoreOnHistoryChanged(object? sender, object args)
    {
        if (shuttingDown || !ReferenceEquals(sender, core))
        {
            return;
        }

        Notify(BrowserStateChangeKind.Navigation);
    }

    private void CoreOnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
    {
        if (!ReferenceEquals(sender, core))
        {
            return;
        }

        FileLogger.LogDebug($"WinUI WebView2 process failed: {args.ProcessFailedKind}");
        if (shuttingDown || browserRecoveryInProgress)
        {
            return;
        }

        switch (args.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                QueueWebViewRecovery();
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
                QueueRendererRecovery(RecoverRendererProcess);
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                QueueRendererRecovery(HandleRendererUnresponsive);
                break;
        }
    }

    private void QueueWebViewRecovery()
    {
        if (shuttingDown)
        {
            return;
        }

        if (dispatcher.HasThreadAccess)
        {
            _ = RecoverWebViewAsync();
        }
        else
        {
            dispatcher.TryEnqueue(() => _ = RecoverWebViewAsync());
        }
    }

    private void QueueRendererRecovery(Action recovery)
    {
        if (dispatcher.HasThreadAccess)
        {
            recovery();
        }
        else
        {
            dispatcher.TryEnqueue(recovery);
        }
    }

    private void RecoverRendererProcess()
    {
        if (shuttingDown || browserRecoveryInProgress || core is null)
        {
            return;
        }

        try
        {
            SetStatus(
                text.Get("Status.RendererRecovering", "页面渲染进程异常，正在重新加载..."),
                StatusLevel.Warning);
            core.Reload();
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Recover WinUI WebView2 renderer");
            QueueWebViewRecovery();
        }
    }

    private void HandleRendererUnresponsive()
    {
        if (shuttingDown || browserRecoveryInProgress || core is null)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        rendererUnresponsiveCount = now - lastRendererUnresponsiveUtc <= TimeSpan.FromSeconds(10)
            ? rendererUnresponsiveCount + 1
            : 1;
        lastRendererUnresponsiveUtc = now;
        if (rendererUnresponsiveCount < 2)
        {
            return;
        }

        rendererUnresponsiveCount = 0;
        try
        {
            SetStatus(
                text.Get("Status.RendererUnresponsive", "页面长时间无响应，正在重新加载..."),
                StatusLevel.Warning);
            core.Stop();
            core.Reload();
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Recover unresponsive WinUI WebView2 renderer");
            QueueWebViewRecovery();
        }
    }

    private async Task RecoverWebViewAsync()
    {
        if (browserRecoveryInProgress || shuttingDown)
        {
            return;
        }

        browserRecoveryInProgress = true;
        rendererUnresponsiveCount = 0;
        lastRendererUnresponsiveUtc = default;
        string recoveryUrl = NavigationTarget.GetStartupUrl(currentAddress);
        SetStatus(text.Get("Status.BrowserRecovering", "正在恢复浏览器..."), StatusLevel.Warning);

        try
        {
            historyCaptureCts?.Cancel();
            historyCaptureCts?.Dispose();
            historyCaptureCts = null;
            await historyCaptureTask.ConfigureAwait(true);
            historyCaptureTask = Task.CompletedTask;
            if (shuttingDown)
            {
                return;
            }
            DetachDownloadOperations(markInterrupted: true);
            try
            {
                DetachWebViewEvents();
            }
            catch (Exception ex)
            {
                FileLogger.LogException(ex, "Detach failed WinUI WebView2 events during recovery");
            }
            try
            {
                webView.Close();
            }
            catch (Exception ex)
            {
                FileLogger.LogException(ex, "Close failed WinUI WebView2 during recovery");
            }
            core = null;
            webView = webViewHost.ReplaceWebView();
            bool recovered = await InitializeWebViewAsync(recoveryUrl).ConfigureAwait(true);
            if (recovered)
            {
                SetStatus(
                    text.Get("Status.BrowserRecovered", "浏览器已恢复。"),
                    StatusLevel.Success,
                    BrowserStateChangeKind.Navigation);
            }
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Recover WinUI WebView2");
            string message = text.Format("Status.BrowserRecoveryFailed", "浏览器恢复失败：{0}", ex.Message);
            SetStatus(message, StatusLevel.Error);
            Win32MessageBox.ShowError(
                message,
                text.Get("Status.ErrorTitle", "Genshin Browser 错误"),
                ownerWindowHandle);
        }
        finally
        {
            browserRecoveryInProgress = false;
        }
    }

    private bool CaptureCurrentAddress()
    {
        try
        {
            string source = core?.Source ?? string.Empty;
            if (!EntryText.TryValidateHttpUrl(source, out string currentUrl))
            {
                return false;
            }

            bool addressChanged = !string.Equals(currentAddress, currentUrl, StringComparison.Ordinal);
            bool canPersist = EntryText.TryNormalizeHttpUrl(currentUrl, out string persistedUrl);
            bool persistedChanged = canPersist &&
                !string.Equals(settings.LastUrl, persistedUrl, StringComparison.Ordinal);
            if (!addressChanged && !persistedChanged)
            {
                return false;
            }

            currentAddress = currentUrl;
            if (persistedChanged)
            {
                settings.LastUrl = persistedUrl;
                QueueSettingsSave();
            }
            return true;
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Capture WinUI WebView2 address");
            return false;
        }
    }

    private void ScheduleHistoryCapture()
    {
        if (core is null || shuttingDown)
        {
            return;
        }

        string url = currentAddress;
        string title = GetDocumentTitle();
        historyCaptureCts?.Cancel();
        historyCaptureCts?.Dispose();
        historyCaptureCts = new CancellationTokenSource();
        Task previousCapture = historyCaptureTask;
        historyCaptureTask = CaptureHistoryAfterDelayAsync(
            previousCapture,
            url,
            title,
            historyCaptureCts.Token);
    }

    private async Task CaptureHistoryAfterDelayAsync(
        Task previousCapture,
        string url,
        string title,
        CancellationToken cancellationToken)
    {
        try
        {
            await previousCapture.ConfigureAwait(false);
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(url, currentAddress, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await historyService.AddEntryAsync(url, title).ConfigureAwait(false);
            if (shuttingDown)
            {
                return;
            }
            dispatcher.TryEnqueue(() => Notify(BrowserStateChangeKind.History));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Capture SPA history");
        }
    }

    private async Task RecordHistoryAsync(string url, string title)
    {
        if (!EntryText.TryNormalizeHttpUrl(url, out string normalized))
        {
            return;
        }

        try
        {
            await historyService.AddEntryAsync(UrlNormalizer.Normalize(normalized), title).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Record navigation history");
        }
    }
}
