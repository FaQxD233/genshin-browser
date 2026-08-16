using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GenshinBrowser.Constants;
using GenshinBrowser.Models;
using GenshinBrowser.Utils;
using Microsoft.Web.WebView2.Core;

namespace GenshinBrowser.Services;

/// <summary>
/// 维护下载任务列表并提供打开文件 / 打开所在文件夹 / 取消下载能力。
/// 下载进度由 WebView2 的 <see cref="Microsoft.Web.WebView2.Core.CoreWebView2DownloadOperation"/>
/// 事件驱动，本服务仅持有引用并更新可观察模型。
/// </summary>
public sealed class DownloadsService : IDisposable
{
    private readonly object _gate = new();
    private readonly object _persistLock = new();
    private readonly Dictionary<DownloadItem, CoreWebView2DownloadOperation> _operations = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly string? _downloadsPath;
    private CancellationTokenSource? _saveDebounceCts;
    private int _version;
    private int _savedVersion;
    private bool _disposed;

    public DownloadsService(string? downloadsPath = null)
    {
        _downloadsPath = downloadsPath;
    }

    /// <summary>
    /// 异步从磁盘加载下载记录。构造函数不再同步读盘，避免阻塞首帧。
    /// 应在 UI 准备好显示下载列表前调用一次（如 MainWindow_OnLoaded）。
    /// </summary>
    public async Task InitializeAsync()
    {
        _version = await LoadFromDiskAsync().ConfigureAwait(true) ? 1 : 0;
    }

    public ObservableCollection<DownloadItem> Downloads { get; } = new();

    public void Track(DownloadItem item, CoreWebView2DownloadOperation operation)
    {
        lock (_gate)
        {
            _operations[item] = operation;
        }

        Downloads.Insert(0, item);
        TrimFinishedExcess();
        QueuePersist();
    }

    public void Restart(
        DownloadItem item,
        CoreWebView2DownloadOperation operation,
        string sourceUri,
        string filePath,
        long totalBytes,
        long receivedBytes)
    {
        lock (_gate)
        {
            _operations[item] = operation;
        }

        item.SourceUri = sourceUri;
        item.FilePath = filePath;
        var fileName = Path.GetFileName(filePath);
        if (!string.IsNullOrEmpty(fileName))
        {
            item.FileName = fileName;
        }
        item.TotalBytes = Math.Max(0, totalBytes);
        item.ReceivedBytes = Math.Max(0, receivedBytes);
        item.StartedAtUtc = DateTime.UtcNow;
        item.State = DownloadState.InProgress;

        var index = Downloads.IndexOf(item);
        if (index > 0)
        {
            Downloads.Move(index, 0);
        }

        QueuePersist();
    }

    /// <summary>
    /// 下载列表超过上限时，从尾部移除最旧的已完成/取消/中断项，
    /// 避免长期累积占用内存。进行中的任务不会被自动移除。
    /// </summary>
    private void TrimFinishedExcess()
    {
        var max = AppConfig.Data.MaxDownloadItems;
        for (var i = Downloads.Count - 1; i >= 0 && Downloads.Count > max; i--)
        {
            var state = Downloads[i].State;
            if (state is DownloadState.Completed or DownloadState.Canceled or DownloadState.Interrupted)
            {
                Downloads.RemoveAt(i);
            }
        }
    }

    public bool TryCancel(DownloadItem item)
    {
        CoreWebView2DownloadOperation? operation;
        lock (_gate)
        {
            if (!_operations.TryGetValue(item, out operation))
            {
                return false;
            }
        }

        try
        {
            operation.Cancel();
            item.State = DownloadState.Canceled;
            lock (_gate)
            {
                _operations.Remove(item);
            }

            QueuePersist();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void MarkCompleted(DownloadItem item)
    {
        lock (_gate)
        {
            _operations.Remove(item);
        }

        item.State = DownloadState.Completed;
        TrimFinishedExcess();
        QueuePersist();
    }

    public void MarkInterrupted(DownloadItem item)
    {
        lock (_gate)
        {
            _operations.Remove(item);
        }

        item.State = DownloadState.Interrupted;
        TrimFinishedExcess();
        QueuePersist();
    }

    public void MarkCanceled(DownloadItem item)
    {
        lock (_gate)
        {
            _operations.Remove(item);
        }

        item.State = DownloadState.Canceled;
        TrimFinishedExcess();
        QueuePersist();
    }

    public bool OpenFile(DownloadItem item)
    {
        if (string.IsNullOrEmpty(item.FilePath) || !File.Exists(item.FilePath))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool OpenFolder(DownloadItem item)
    {
        if (string.IsNullOrEmpty(item.FilePath))
        {
            return false;
        }

        var dir = Path.GetDirectoryName(item.FilePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
            };
            startInfo.ArgumentList.Add(dir);
            Process.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void ClearFinished()
    {
        // 倒序 RemoveAt 避免 Where().ToList() + 正序 Remove 的 O(n²)：
        // ObservableCollection.Remove(item) 内部是 O(n) 线性查找 + 移位，
        // 循环执行整体 O(n²)；倒序 RemoveAt 直接按索引删除，整体 O(n)。
        var removed = 0;
        for (var i = Downloads.Count - 1; i >= 0; i--)
        {
            var state = Downloads[i].State;
            if (state is DownloadState.Completed or DownloadState.Canceled or DownloadState.Interrupted)
            {
                Downloads.RemoveAt(i);
                removed++;
            }
        }

        if (removed > 0)
        {
            QueuePersist();
        }
    }

    public async Task FlushAsync()
    {
        CancelDebouncedSave();
        if (_downloadsPath is null)
        {
            return;
        }

        while (!_disposed)
        {
            List<PersistedDownloadEntry> snapshot;
            int version;
            lock (_persistLock)
            {
                if (_version == _savedVersion)
                {
                    return;
                }

                version = _version;
                snapshot = CreatePersistSnapshot();
            }

            await SaveAsync(snapshot, version).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelDebouncedSave();
        _saveGate.Dispose();
    }

    private void QueuePersist()
    {
        if (_downloadsPath is null)
        {
            return;
        }

        List<PersistedDownloadEntry> snapshot;
        int version;
        CancellationToken token;
        // 合并两段锁为一段，_disposed 检查必须在锁内（见 FavoritesService.QueueDebouncedSave 同类修复）。
        lock (_persistLock)
        {
            if (_disposed)
            {
                return;
            }

            _version++;
            version = _version;
            snapshot = CreatePersistSnapshot();

            _saveDebounceCts?.Cancel();
            _saveDebounceCts?.Dispose();
            _saveDebounceCts = new CancellationTokenSource();
            token = _saveDebounceCts.Token;
        }

        _ = SaveDebouncedAsync(snapshot, version, token);
    }

    private void CancelDebouncedSave()
    {
        lock (_persistLock)
        {
            _saveDebounceCts?.Cancel();
            _saveDebounceCts?.Dispose();
            _saveDebounceCts = null;
        }
    }

    private async Task SaveDebouncedAsync(
        IReadOnlyList<PersistedDownloadEntry> snapshot,
        int version,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(AppConfig.Data.DownloadSaveDebounceMs, cancellationToken).ConfigureAwait(false);
            await SaveAsync(snapshot, version, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A newer snapshot replaced this one.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ObjectDisposedException)
        {
            // SaveAsync already logged the concrete failure; ODE is the Dispose race on shutdown.
        }
    }

    private async Task SaveAsync(
        IReadOnlyList<PersistedDownloadEntry> snapshot,
        int version,
        CancellationToken cancellationToken = default)
    {
        if (_downloadsPath is null)
        {
            return;
        }

        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_persistLock)
            {
                if (version != _version)
                {
                    return;
                }
            }

            await JsonFileWriter.WriteAtomicAsync(
                _downloadsPath,
                snapshot,
                JsonFileWriter.CompactOptions).ConfigureAwait(false);
            lock (_persistLock)
            {
                if (version == _version)
                {
                    _savedVersion = version;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            FileLogger.LogException(ex, "Save downloads");
            throw;
        }
        finally
        {
            try
            {
                _saveGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // 关闭竞态：Dispose 已先行释放 _saveGate；写盘已成功或被取消，无需 Release。
            }
        }
    }

    private async Task<bool> LoadFromDiskAsync()
    {
        if (_downloadsPath is null || !File.Exists(_downloadsPath))
        {
            return false;
        }

        try
        {
            // 异步读盘 + 在线程池反序列化，避免阻塞 UI 线程
            var json = await JsonFileWriter.ReadAllTextBoundedAsync(
                _downloadsPath,
                AppConfig.Data.MaxDownloadsFileSizeBytes).ConfigureAwait(false);

            var (loaded, sanitized) = await Task.Run(() =>
            {
                var l = JsonSerializer.Deserialize<List<PersistedDownloadEntry?>>(json)
                        ?? new List<PersistedDownloadEntry?>();
                return (l, SanitizeEntries(l));
            }).ConfigureAwait(true);

            // 回到 UI 线程填充 ObservableCollection（ConfigureAwait(true) 默认）
            foreach (var item in sanitized)
            {
                Downloads.Add(item);
            }

            return !EntriesMatch(loaded, sanitized);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            FileLogger.LogException(ex, "Load downloads");
            return false;
        }
    }

    private List<PersistedDownloadEntry> CreatePersistSnapshot()
    {
        return Downloads.Take(AppConfig.Data.MaxDownloadItems)
            .Select(item => new PersistedDownloadEntry
            {
                FileName = item.FileName,
                SourceUri = item.SourceUri,
                FilePath = item.FilePath,
                TotalBytes = item.TotalBytes,
                ReceivedBytes = item.ReceivedBytes,
                State = item.State,
                StartedAtUtc = item.StartedAtUtc,
            })
            .ToList();
    }

    private static bool EntriesMatch(
        IReadOnlyList<PersistedDownloadEntry?> loaded,
        IReadOnlyList<DownloadItem> sanitized)
    {
        if (loaded.Count != sanitized.Count)
        {
            return false;
        }

        for (var i = 0; i < sanitized.Count; i++)
        {
            var source = loaded[i];
            var target = sanitized[i];
            if (source is null ||
                !string.Equals(source.FileName, target.FileName, StringComparison.Ordinal) ||
                !string.Equals(source.SourceUri, target.SourceUri, StringComparison.Ordinal) ||
                !string.Equals(source.FilePath, target.FilePath, StringComparison.Ordinal) ||
                source.TotalBytes != target.TotalBytes ||
                source.ReceivedBytes != target.ReceivedBytes ||
                source.State != target.State ||
                source.StartedAtUtc != target.StartedAtUtc)
            {
                return false;
            }
        }

        return true;
    }

    private static List<DownloadItem> SanitizeEntries(IEnumerable<PersistedDownloadEntry?> loaded)
    {
        var result = new List<DownloadItem>(AppConfig.Data.MaxDownloadItems);
        var fallbackTime = DateTime.UtcNow;
        foreach (var entry in loaded)
        {
            if (entry is null)
            {
                continue;
            }

            var filePath = (entry.FilePath ?? string.Empty).Trim();
            if (filePath.Length > 32_767)
            {
                filePath = string.Empty;
            }

            var sourceUri = EntryText.TryValidateHttpUrl(entry.SourceUri, out var validSourceUri)
                ? validSourceUri
                : string.Empty;
            var fileName = EntryText.TruncateTitle(entry.FileName);
            if (fileName.Length == 0)
            {
                fileName = !string.IsNullOrEmpty(filePath)
                    ? Path.GetFileName(filePath)
                    : "download";
            }

            var state = Enum.IsDefined(entry.State) ? entry.State : DownloadState.Interrupted;
            if (state == DownloadState.InProgress)
            {
                state = DownloadState.Interrupted;
            }

            var receivedBytes = Math.Max(0, entry.ReceivedBytes);
            var totalBytes = Math.Max(0, entry.TotalBytes);
            if (totalBytes > 0)
            {
                receivedBytes = Math.Min(receivedBytes, totalBytes);
            }

            result.Add(new DownloadItem
            {
                FileName = fileName,
                SourceUri = sourceUri,
                FilePath = filePath,
                TotalBytes = totalBytes,
                ReceivedBytes = receivedBytes,
                State = state,
                StartedAtUtc = EntryText.NormalizeUtcTimestamp(entry.StartedAtUtc, fallbackTime),
            });

            if (result.Count == AppConfig.Data.MaxDownloadItems)
            {
                break;
            }
        }

        return result;
    }

    private sealed class PersistedDownloadEntry
    {
        public string FileName { get; set; } = string.Empty;

        public string SourceUri { get; set; } = string.Empty;

        public string FilePath { get; set; } = string.Empty;

        public long TotalBytes { get; set; }

        public long ReceivedBytes { get; set; }

        public DownloadState State { get; set; }

        public DateTime StartedAtUtc { get; set; }
    }
}
