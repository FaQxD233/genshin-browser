using GenshinBrowser.Constants;
using GenshinBrowser.Models;
using GenshinBrowser.Utils;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace GenshinBrowser.Services;

public sealed class DownloadsService : IDisposable
{
    private readonly string downloadsPath;
    private readonly object persistLock = new();
    private readonly SemaphoreSlim saveGate = new(1, 1);
    private CancellationTokenSource? saveDebounceCts;
    private List<PersistedDownloadEntry>? pendingSnapshot;
    private int version;
    private int savedVersion;
    private long lastProgressPersistTick;
    private bool disposed;

    public DownloadsService(string downloadsPath)
    {
        this.downloadsPath = downloadsPath;
        version = LoadFromDisk() ? 1 : 0;
    }

    public ObservableCollection<DownloadItem> Downloads { get; } = [];

    public void Track(DownloadItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Downloads.Insert(0, item);
        TrimFinishedExcess();
        lastProgressPersistTick = Environment.TickCount64;
        QueuePersist();
    }

    public void Restart(
        DownloadItem item,
        string sourceUri,
        string filePath,
        long totalBytes,
        long receivedBytes)
    {
        item.SourceUri = sourceUri;
        item.FilePath = filePath;
        string fileName = Path.GetFileName(filePath);
        if (!string.IsNullOrEmpty(fileName))
        {
            item.FileName = fileName;
        }

        item.TotalBytes = Math.Max(0, totalBytes);
        item.ReceivedBytes = Math.Max(0, receivedBytes);
        item.StartedAtUtc = DateTime.UtcNow;
        item.State = DownloadState.InProgress;

        int index = Downloads.IndexOf(item);
        if (index > 0)
        {
            Downloads.Move(index, 0);
        }

        lastProgressPersistTick = Environment.TickCount64;
        QueuePersist();
    }

    public void MarkCompleted(DownloadItem item)
    {
        item.State = DownloadState.Completed;
        TrimFinishedExcess();
        QueuePersist();
    }

    public void MarkInterrupted(DownloadItem item)
    {
        item.State = DownloadState.Interrupted;
        TrimFinishedExcess();
        QueuePersist();
    }

    public void MarkCanceled(DownloadItem item)
    {
        item.State = DownloadState.Canceled;
        TrimFinishedExcess();
        QueuePersist();
    }

    public void NotifyProgressChanged()
    {
        long now = Environment.TickCount64;
        if (now - lastProgressPersistTick < 1_000)
        {
            return;
        }

        lastProgressPersistTick = now;
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
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Open downloaded file");
            return false;
        }
    }

    public bool OpenFolder(DownloadItem item)
    {
        string? directory = Path.GetDirectoryName(item.FilePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        try
        {
            ProcessStartInfo startInfo = new("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add(directory);
            Process.Start(startInfo);
            return true;
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Open download folder");
            return false;
        }
    }

    public void ClearFinished()
    {
        bool changed = false;
        for (int index = Downloads.Count - 1; index >= 0; index--)
        {
            if (Downloads[index].State is DownloadState.Completed or DownloadState.Canceled or DownloadState.Interrupted)
            {
                Downloads.RemoveAt(index);
                changed = true;
            }
        }

        if (changed)
        {
            QueuePersist();
        }
    }

    public async Task FlushAsync()
    {
        CancelDebouncedSave();
        while (true)
        {
            List<PersistedDownloadEntry> snapshot;
            int snapshotVersion;
            lock (persistLock)
            {
                if (version == savedVersion)
                {
                    return;
                }

                snapshotVersion = version;
                snapshot = CreatePersistSnapshot();
            }

            await SaveAsync(snapshot, snapshotVersion).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        CancelDebouncedSave();
        saveGate.Dispose();
    }

    private void TrimFinishedExcess()
    {
        for (int index = Downloads.Count - 1;
             index >= 0 && Downloads.Count > AppConfig.Data.MaxDownloadItems;
             index--)
        {
            if (Downloads[index].State != DownloadState.InProgress)
            {
                Downloads.RemoveAt(index);
            }
        }
    }

    private void QueuePersist()
    {
        if (disposed)
        {
            return;
        }

        CancellationToken token;
        lock (persistLock)
        {
            version++;
            pendingSnapshot = CreatePersistSnapshot();
            saveDebounceCts?.Cancel();
            saveDebounceCts?.Dispose();
            saveDebounceCts = new CancellationTokenSource();
            token = saveDebounceCts.Token;
        }

        _ = SaveDebouncedAsync(token);
    }

    private void CancelDebouncedSave()
    {
        lock (persistLock)
        {
            saveDebounceCts?.Cancel();
            saveDebounceCts?.Dispose();
            saveDebounceCts = null;
        }
    }

    private async Task SaveDebouncedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(AppConfig.Data.DownloadSaveDebounceMs, cancellationToken).ConfigureAwait(false);
            List<PersistedDownloadEntry> snapshot;
            int snapshotVersion;
            lock (persistLock)
            {
                snapshotVersion = version;
                snapshot = pendingSnapshot ?? CreatePersistSnapshot();
            }

            await SaveAsync(snapshot, snapshotVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Persist downloads");
        }
    }

    private async Task SaveAsync(
        List<PersistedDownloadEntry> snapshot,
        int snapshotVersion,
        CancellationToken cancellationToken = default)
    {
        await saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (persistLock)
            {
                if (snapshotVersion != version)
                {
                    return;
                }
            }

            await JsonFileWriter.WriteAtomicAsync(downloadsPath, snapshot, JsonFileWriter.CompactOptions).ConfigureAwait(false);
            lock (persistLock)
            {
                if (snapshotVersion == version)
                {
                    savedVersion = snapshotVersion;
                }
            }
        }
        finally
        {
            saveGate.Release();
        }
    }

    private bool LoadFromDisk()
    {
        if (!File.Exists(downloadsPath))
        {
            return false;
        }

        try
        {
            string json = JsonFileWriter.ReadAllTextBounded(downloadsPath, AppConfig.Data.MaxDownloadsFileSizeBytes);
            List<PersistedDownloadEntry?> loaded = JsonSerializer.Deserialize<List<PersistedDownloadEntry?>>(json) ?? [];
            List<DownloadItem> sanitized = Sanitize(loaded).ToList();
            foreach (DownloadItem item in sanitized)
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
        return Downloads.Select(item => new PersistedDownloadEntry
        {
            FileName = item.FileName,
            SourceUri = item.SourceUri,
            FilePath = item.FilePath,
            TotalBytes = item.TotalBytes,
            ReceivedBytes = item.ReceivedBytes,
            State = item.State,
            StartedAtUtc = item.StartedAtUtc,
        }).ToList();
    }

    private static bool EntriesMatch(
        IReadOnlyList<PersistedDownloadEntry?> loaded,
        IReadOnlyList<DownloadItem> sanitized)
    {
        if (loaded.Count != sanitized.Count)
        {
            return false;
        }

        for (int index = 0; index < sanitized.Count; index++)
        {
            PersistedDownloadEntry? source = loaded[index];
            DownloadItem target = sanitized[index];
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

    private static IEnumerable<DownloadItem> Sanitize(IEnumerable<PersistedDownloadEntry?> entries)
    {
        int count = 0;
        foreach (PersistedDownloadEntry? entry in entries)
        {
            if (entry is null || count >= AppConfig.Data.MaxDownloadItems)
            {
                continue;
            }

            string sourceUri = EntryText.TryValidateHttpUrl(entry.SourceUri, out string validatedUri)
                ? validatedUri
                : string.Empty;
            string filePath = entry.FilePath?.Trim() ?? string.Empty;
            if (filePath.Length > 32_767)
            {
                filePath = string.Empty;
            }
            string fileName = EntryText.TruncateTitle(entry.FileName);
            if (fileName.Length == 0)
            {
                fileName = Path.GetFileName(filePath);
            }

            DownloadState state = Enum.IsDefined(entry.State) ? entry.State : DownloadState.Interrupted;
            if (state == DownloadState.InProgress)
            {
                state = DownloadState.Interrupted;
            }

            long totalBytes = Math.Max(0, entry.TotalBytes);
            long receivedBytes = Math.Max(0, entry.ReceivedBytes);
            if (totalBytes > 0)
            {
                receivedBytes = Math.Min(receivedBytes, totalBytes);
            }

            yield return new DownloadItem
            {
                FileName = fileName.Length == 0 ? "download" : fileName,
                SourceUri = sourceUri,
                FilePath = filePath,
                TotalBytes = totalBytes,
                ReceivedBytes = receivedBytes,
                State = state,
                StartedAtUtc = EntryText.NormalizeUtcTimestamp(entry.StartedAtUtc, DateTime.UtcNow),
            };
            count++;
        }
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
