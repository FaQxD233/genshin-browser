using GenshinBrowser.Models;
using GenshinBrowser.Services;
using GenshinBrowser.Threading;
using GenshinBrowser.Utils;
using Microsoft.Web.WebView2.Core;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace GenshinBrowser.Browser;

internal sealed partial class BrowserSession
{
    private const int DownloadRetryTimeoutSeconds = 30;
    private readonly Dictionary<CoreWebView2DownloadOperation, DownloadItem> downloadItemsByOperation = [];
    private readonly Dictionary<CoreWebView2DownloadOperation, DownloadItem> pendingDownloadProgress = [];
    private IUiTimer? downloadProgressTimer;
    private IUiTimer? downloadRetryTimer;
    private PendingDownloadRetry? pendingDownloadRetry;

    public void CancelDownload(DownloadItem item)
    {
        KeyValuePair<CoreWebView2DownloadOperation, DownloadItem> pair =
            downloadItemsByOperation.FirstOrDefault(entry => ReferenceEquals(entry.Value, item));
        if (pair.Key is null)
        {
            return;
        }

        try
        {
            pair.Key.Cancel();
            downloadsService.MarkCanceled(item);
            pendingDownloadProgress.Remove(pair.Key);
            DetachDownloadOperation(pair.Key);
            SetStatus(text.Format("Status.DownloadCanceled", "已取消下载：{0}", item.FileName), StatusLevel.Success);
            DownloadsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Cancel download");
            SetStatus(text.Format("Status.DownloadCancelFailed", "取消下载失败：{0}", ex.Message), StatusLevel.Error);
        }
    }

    public void RetryDownload(DownloadItem item)
    {
        if (!item.CanRetry || !EntryText.TryValidateHttpUrl(item.SourceUri, out string retryUri) || core is null)
        {
            SetStatus(text.Get("Status.DownloadRetryUnavailable", "当前下载无法重试"), StatusLevel.Warning);
            return;
        }

        pendingDownloadRetry = new PendingDownloadRetry(
            item,
            retryUri,
            DateTime.UtcNow.AddSeconds(DownloadRetryTimeoutSeconds));
        StartDownloadRetryTimer();
        _ = TriggerDownloadRetryAsync(retryUri, item.FileName);
    }

    public void OpenDownloadFile(DownloadItem item)
    {
        if (!downloadsService.OpenFile(item))
        {
            SetStatus(text.Get("Status.CannotOpenFile", "无法打开下载文件"), StatusLevel.Warning);
        }
    }

    public void OpenDownloadFolder(DownloadItem item)
    {
        if (!downloadsService.OpenFolder(item))
        {
            SetStatus(text.Get("Status.CannotOpenFolder", "无法打开下载目录"), StatusLevel.Warning);
        }
    }

    public void ClearFinishedDownloads()
    {
        downloadsService.ClearFinished();
        DownloadsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> ClearBrowsingDataAsync(bool silent = false)
    {
        if (core is null)
        {
            if (!silent)
            {
                SetStatus(text.Get("Status.BrowserNotReady", "浏览器尚未就绪"), StatusLevel.Warning);
            }
            return false;
        }

        try
        {
            if (!silent)
            {
                SetStatus(text.Get("Status.ClearingBrowsingData", "正在清理 WebView2 磁盘缓存..."), StatusLevel.Info);
            }

            // This command is intentionally cache-only. The existing profile may contain
            // login state in LocalStorage/IndexedDB in addition to cookies; migration must
            // never turn routine cache maintenance into a profile reset.
            CoreWebView2BrowsingDataKinds kinds = CoreWebView2BrowsingDataKinds.DiskCache;
            await core.Profile.ClearBrowsingDataAsync(kinds);
            if (shuttingDown)
            {
                return false;
            }
            settings.LastWebView2CacheCheckUtc = DateTime.UtcNow;
            QueueSettingsSave();

            if (!silent)
            {
                SetStatus(text.Get("Status.BrowsingDataCleared", "WebView2 磁盘缓存已清理"), StatusLevel.Success);
            }
            return true;
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, silent ? "Clear WebView2 disk cache automatically" : "Clear WebView2 disk cache");
            if (!silent)
            {
                SetStatus(text.Format("Status.ClearBrowsingDataFailed", "清理 WebView2 磁盘缓存失败：{0}", ex.Message), StatusLevel.Error);
            }
            return false;
        }
    }

    private void CoreOnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
    {
        if (!ReferenceEquals(sender, core))
        {
            return;
        }

        if (shuttingDown)
        {
            args.Cancel = true;
            return;
        }

        CoreWebView2DownloadOperation? operation = null;
        DownloadItem? item = null;
        try
        {
            operation = args.DownloadOperation;
            string filePath = args.ResultFilePath ?? operation.ResultFilePath ?? string.Empty;
            string fileName = Path.GetFileName(filePath);
            if (string.IsNullOrEmpty(fileName))
            {
                fileName = text.Get("Downloads.DefaultFileName", "下载文件");
            }

            string sourceUri = operation.Uri ?? string.Empty;
            long totalBytes = ToInt64Bytes(operation.TotalBytesToReceive);
            long receivedBytes = Math.Max(0L, operation.BytesReceived);
            DownloadItem? retryItem = TakePendingDownloadRetry(sourceUri);
            if (retryItem is not null)
            {
                item = retryItem;
                downloadsService.Restart(item, sourceUri, filePath, totalBytes, receivedBytes);
            }
            else
            {
                item = new DownloadItem
                {
                    FileName = fileName,
                    SourceUri = sourceUri,
                    FilePath = filePath,
                    TotalBytes = totalBytes,
                    ReceivedBytes = receivedBytes,
                    State = DownloadState.InProgress,
                    StartedAtUtc = DateTime.UtcNow,
                };
                downloadsService.Track(item);
            }

            downloadItemsByOperation[operation] = item;
            operation.BytesReceivedChanged += DownloadOperationOnBytesReceivedChanged;
            operation.StateChanged += DownloadOperationOnStateChanged;
            SetStatus(text.Format("Status.DownloadStarted", "开始下载：{0}", item.FileName), StatusLevel.Info);
            DownloadsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            if (operation is not null)
            {
                DetachDownloadOperation(operation);
            }

            if (item is not null)
            {
                downloadsService.MarkInterrupted(item);
                DownloadsChanged?.Invoke(this, EventArgs.Empty);
            }

            FileLogger.LogException(ex, "Start WebView2 download");
        }
    }

    private void DownloadOperationOnBytesReceivedChanged(object? sender, object args)
    {
        if (sender is CoreWebView2DownloadOperation operation)
        {
            dispatcher.TryEnqueue(() => QueueDownloadProgress(operation));
        }
    }

    private void DownloadOperationOnStateChanged(object? sender, object args)
    {
        if (sender is CoreWebView2DownloadOperation operation)
        {
            dispatcher.TryEnqueue(() => HandleDownloadStateChanged(operation));
        }
    }

    private void QueueDownloadProgress(CoreWebView2DownloadOperation operation)
    {
        if (shuttingDown || !downloadItemsByOperation.TryGetValue(operation, out DownloadItem? item))
        {
            return;
        }

        pendingDownloadProgress[operation] = item;
        downloadProgressTimer ??= CreateDownloadProgressTimer();
        if (!downloadProgressTimer.IsRunning)
        {
            downloadProgressTimer.Start();
        }
    }

    private IUiTimer CreateDownloadProgressTimer()
    {
        IUiTimer timer = timerFactory.Create(TimeSpan.FromMilliseconds(100));
        timer.Tick += DownloadProgressTimerOnTick;
        return timer;
    }

    private void DownloadProgressTimerOnTick(object? sender, EventArgs e)
    {
        downloadProgressTimer?.Stop();
        CapturePendingDownloadProgress();
    }

    private void CapturePendingDownloadProgress()
    {
        if (pendingDownloadProgress.Count == 0)
        {
            return;
        }

        foreach ((CoreWebView2DownloadOperation operation, DownloadItem item) in pendingDownloadProgress)
        {
            try
            {
                ApplyDownloadProgress(operation, item);
            }
            catch (Exception ex)
            {
                FileLogger.LogException(ex, "Capture download progress");
            }
        }
        pendingDownloadProgress.Clear();
        downloadsService.NotifyProgressChanged();
    }

    private static void ApplyDownloadProgress(CoreWebView2DownloadOperation operation, DownloadItem item)
    {
        string resultFilePath = operation.ResultFilePath ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(resultFilePath) &&
            !string.Equals(item.FilePath, resultFilePath, StringComparison.Ordinal))
        {
            item.FilePath = resultFilePath;
            string fileName = Path.GetFileName(resultFilePath);
            if (!string.IsNullOrEmpty(fileName))
            {
                item.FileName = fileName;
            }
        }

        item.ReceivedBytes = Math.Max(0L, operation.BytesReceived);
        long totalBytes = ToInt64Bytes(operation.TotalBytesToReceive);
        if (totalBytes > 0)
        {
            item.TotalBytes = totalBytes;
        }
    }

    private void HandleDownloadStateChanged(CoreWebView2DownloadOperation operation)
    {
        if (!downloadItemsByOperation.TryGetValue(operation, out DownloadItem? item))
        {
            return;
        }

        try
        {
            ApplyDownloadProgress(operation, item);
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Capture final download state");
            downloadsService.MarkInterrupted(item);
            DetachDownloadOperation(operation);
            DownloadsChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        pendingDownloadProgress.Remove(operation);

        switch (operation.State)
        {
            case CoreWebView2DownloadState.Completed:
                downloadsService.MarkCompleted(item);
                SetStatus(text.Format("Status.DownloadCompleted", "下载完成：{0}", item.FileName), StatusLevel.Success);
                DetachDownloadOperation(operation);
                break;
            case CoreWebView2DownloadState.Interrupted:
                if (operation.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled ||
                    item.State == DownloadState.Canceled)
                {
                    downloadsService.MarkCanceled(item);
                    SetStatus(text.Format("Status.DownloadCanceled", "已取消下载：{0}", item.FileName), StatusLevel.Success);
                }
                else
                {
                    downloadsService.MarkInterrupted(item);
                    SetStatus(text.Format("Status.DownloadInterrupted", "下载中断：{0}", item.FileName), StatusLevel.Warning);
                }
                DetachDownloadOperation(operation);
                break;
        }

        DownloadsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DetachDownloadOperation(CoreWebView2DownloadOperation operation)
    {
        try
        {
            operation.BytesReceivedChanged -= DownloadOperationOnBytesReceivedChanged;
            operation.StateChanged -= DownloadOperationOnStateChanged;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            // The browser process can invalidate the operation before recovery cleanup.
        }
        pendingDownloadProgress.Remove(operation);
        downloadItemsByOperation.Remove(operation);
    }

    private void DetachDownloadOperations(bool markInterrupted)
    {
        downloadProgressTimer?.Stop();
        if (downloadProgressTimer is not null)
        {
            downloadProgressTimer.Tick -= DownloadProgressTimerOnTick;
            downloadProgressTimer.Dispose();
            downloadProgressTimer = null;
        }

        pendingDownloadProgress.Clear();
        foreach ((CoreWebView2DownloadOperation operation, DownloadItem item) in downloadItemsByOperation.ToArray())
        {
            if (markInterrupted && item.State == DownloadState.InProgress)
            {
                downloadsService.MarkInterrupted(item);
            }
            DetachDownloadOperation(operation);
        }

        StopDownloadRetryTimer();
        pendingDownloadRetry = null;
        DownloadsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StartDownloadRetryTimer()
    {
        StopDownloadRetryTimer();
        downloadRetryTimer = timerFactory.Create(TimeSpan.FromSeconds(DownloadRetryTimeoutSeconds));
        downloadRetryTimer.Tick += DownloadRetryTimerOnTick;
        downloadRetryTimer.Start();
    }

    private void StopDownloadRetryTimer()
    {
        if (downloadRetryTimer is null)
        {
            return;
        }

        downloadRetryTimer.Stop();
        downloadRetryTimer.Tick -= DownloadRetryTimerOnTick;
        downloadRetryTimer.Dispose();
        downloadRetryTimer = null;
    }

    private void DownloadRetryTimerOnTick(object? sender, EventArgs e)
    {
        StopDownloadRetryTimer();
        if (pendingDownloadRetry is null)
        {
            return;
        }

        string fileName = pendingDownloadRetry.Item.FileName;
        pendingDownloadRetry = null;
        SetStatus(text.Format("Status.DownloadRetryTimedOut", "重试下载超时：{0}", fileName), StatusLevel.Warning);
    }

    private DownloadItem? TakePendingDownloadRetry(string sourceUri)
    {
        PendingDownloadRetry? pending = pendingDownloadRetry;
        if (pending is null ||
            pending.ExpiresAtUtc < DateTime.UtcNow ||
            !pending.Item.CanRetry)
        {
            pendingDownloadRetry = null;
            StopDownloadRetryTimer();
            return null;
        }

        if (!DownloadUriComparer.Matches(pending.SourceUri, sourceUri))
        {
            return null;
        }

        pendingDownloadRetry = null;
        StopDownloadRetryTimer();
        return pending.Item;
    }

    private async Task TriggerDownloadRetryAsync(string retryUri, string fileName)
    {
        if (core is null)
        {
            return;
        }

        try
        {
            string uriLiteral = JsonSerializer.Serialize(retryUri);
            string script =
                "(() => {" +
                "const frame=document.createElement('iframe');" +
                "frame.style.cssText='position:fixed;width:0;height:0;border:0;left:-9999px;top:-9999px';" +
                $"frame.src={uriLiteral};" +
                "document.documentElement.appendChild(frame);" +
                "setTimeout(()=>frame.remove(),60000);" +
                "return 'ok';" +
                "})();";
            string result = await core.ExecuteScriptAsync(script);
            if (shuttingDown)
            {
                return;
            }
            if (!result.Contains("ok", StringComparison.Ordinal))
            {
                core.Navigate(retryUri);
            }

            SetStatus(text.Format("Status.DownloadRetrying", "正在重试下载：{0}", fileName), StatusLevel.Info);
        }
        catch (Exception ex)
        {
            pendingDownloadRetry = null;
            StopDownloadRetryTimer();
            FileLogger.LogException(ex, "Retry download");
            SetStatus(text.Format("Status.DownloadRetryFailed", "重试下载失败：{0}", ex.Message), StatusLevel.Error);
        }
    }

    private sealed record PendingDownloadRetry(DownloadItem Item, string SourceUri, DateTime ExpiresAtUtc);

    private static long ToInt64Bytes(long value) => Math.Max(0L, value);
}
