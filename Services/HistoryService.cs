using System.IO;
using System.Text.Json;
using GenshinBrowser.Constants;
using GenshinBrowser.Models;
using GenshinBrowser.Utils;

namespace GenshinBrowser.Services;

public sealed class HistoryService : IDisposable
{
    private readonly string _historyPath;
    private readonly object _entriesLock = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private List<HistoryEntry> _entries = new();

    /// <summary>
    /// 缓存的只读快照。仅在 _entries 变更时重建，避免每次 GetEntries() 都 ToList() 分配新副本。
    /// 在锁内访问与替换。UI 读取用；落盘必须用 <see cref="CreatePersistSnapshot"/> 的独立拷贝。
    /// </summary>
    private IReadOnlyList<HistoryEntry>? _snapshotCache;

    private CancellationTokenSource? _saveDebounceCts;
    private readonly object _debounceLock = new();
    private bool _disposed;

    /// <summary>
    /// 内存版本号。每次变更 +1；落盘成功时仅当保存的版本仍是最新才视为干净，
    /// 避免「保存过程中又有新写入」被误标为已落盘。
    /// </summary>
    private int _version;
    private int _savedVersion;

    public HistoryService(string historyPath)
    {
        _historyPath = historyPath;
        if (LoadFromDisk())
        {
            QueueDebouncedSave();
        }
    }

    public IReadOnlyList<HistoryEntry> GetEntries()
    {
        lock (_entriesLock)
        {
            return _snapshotCache ??= _entries.AsReadOnly();
        }
    }

    /// <summary>
    /// 追加/更新历史。内存立即生效；磁盘写入按 <see cref="AppConfig.Data.HistorySaveDebounceMs"/> 防抖合并。
    /// </summary>
    public Task AddEntryAsync(string url, string title)
    {
        if (!EntryText.TryNormalizeHttpUrl(url, out var normalizedUrl))
        {
            return Task.CompletedTask;
        }

        var safeTitle = EntryText.TruncateTitle(title);
        if (safeTitle.Length == 0)
        {
            safeTitle = normalizedUrl;
        }

        lock (_entriesLock)
        {
            _entries.RemoveAll(item => string.Equals(item.Url, normalizedUrl, StringComparison.OrdinalIgnoreCase));
            _entries.Insert(0, new HistoryEntry
            {
                Url = normalizedUrl,
                Title = safeTitle,
                VisitedAt = DateTime.UtcNow,
            });

            if (_entries.Count > AppConfig.Data.MaxHistoryEntries)
            {
                _entries.RemoveRange(AppConfig.Data.MaxHistoryEntries, _entries.Count - AppConfig.Data.MaxHistoryEntries);
            }

            _snapshotCache = null;
            _version++;
        }

        QueueDebouncedSave();
        return Task.CompletedTask;
    }

    public async Task RemoveAsync(string url)
    {
        if (!EntryText.TryNormalizeHttpUrl(url, out var normalizedUrl))
        {
            return;
        }

        IReadOnlyList<HistoryEntry> snapshot;
        int version;
        lock (_entriesLock)
        {
            _entries.RemoveAll(item => string.Equals(item.Url, normalizedUrl, StringComparison.OrdinalIgnoreCase));
            _snapshotCache = null;
            _version++;
            version = _version;
            snapshot = CreatePersistSnapshot();
        }

        CancelDebouncedSave();
        await SaveAsync(snapshot, version).ConfigureAwait(false);
    }

    public async Task ClearAllAsync()
    {
        IReadOnlyList<HistoryEntry> snapshot;
        int version;
        lock (_entriesLock)
        {
            _entries.Clear();
            _snapshotCache = null;
            _version++;
            version = _version;
            snapshot = CreatePersistSnapshot();
        }

        CancelDebouncedSave();
        await SaveAsync(snapshot, version).ConfigureAwait(false);
    }

    /// <summary>
    /// 将尚未落盘的变更立即写入。关闭应用前应调用；Dispose 不再二次写盘。
    /// </summary>
    public async Task FlushAsync()
    {
        CancelDebouncedSave();

        while (true)
        {
            IReadOnlyList<HistoryEntry> snapshot;
            int version;
            lock (_entriesLock)
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
        // 关闭路径应先 await FlushAsync()；此处不再同步写盘，避免 UI 关闭时卡死。
        _saveGate.Dispose();
    }

    private void QueueDebouncedSave()
    {
        if (_disposed)
        {
            return;
        }

        CancellationToken token;
        lock (_debounceLock)
        {
            _saveDebounceCts?.Cancel();
            _saveDebounceCts?.Dispose();
            _saveDebounceCts = new CancellationTokenSource();
            token = _saveDebounceCts.Token;
        }

        _ = DebouncedSaveAsync(token);
    }

    private void CancelDebouncedSave()
    {
        lock (_debounceLock)
        {
            _saveDebounceCts?.Cancel();
            _saveDebounceCts?.Dispose();
            _saveDebounceCts = null;
        }
    }

    private async Task DebouncedSaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(AppConfig.Data.HistorySaveDebounceMs, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<HistoryEntry>? snapshot = null;
            var version = 0;
            lock (_entriesLock)
            {
                if (_version == _savedVersion)
                {
                    return;
                }

                version = _version;
                // 独立拷贝：序列化在锁外进行，期间 UI 仍可能改 _entries
                snapshot = CreatePersistSnapshot();
                _snapshotCache = _entries.AsReadOnly();
            }

            await SaveAsync(snapshot, version, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 被更新的写入请求取消，预期行为
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // SaveAsync 已记录具体异常；后台防抖任务不能留下未观察异常。
        }
    }

    /// <summary>
    /// 在持锁状态下生成落盘用独立列表（条目浅拷贝）。
    /// 禁止直接把 <c>_entries.AsReadOnly()</c> 交给异步序列化。
    /// </summary>
    private List<HistoryEntry> CreatePersistSnapshot()
    {
        var copy = new List<HistoryEntry>(_entries.Count);
        for (var i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            copy.Add(new HistoryEntry
            {
                Url = e.Url,
                Title = e.Title,
                VisitedAt = e.VisitedAt,
            });
        }

        return copy;
    }

    private bool LoadFromDisk()
    {
        if (!File.Exists(_historyPath))
        {
            return false;
        }

        try
        {
            var json = JsonFileWriter.ReadAllTextBounded(_historyPath, AppConfig.Data.MaxHistoryFileSizeBytes);
            var loaded = JsonSerializer.Deserialize<List<HistoryEntry?>>(json) ?? new List<HistoryEntry?>();
            _entries = SanitizeEntries(loaded);
            var changed = !EntriesMatch(loaded, _entries);
            _version = changed ? 1 : 0;
            _savedVersion = 0;
            return changed;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            FileLogger.LogException(ex, "Load history");
            _entries = new List<HistoryEntry>();
            _version = 0;
            _savedVersion = 0;
            return false;
        }
    }

    private async Task SaveAsync(
        IReadOnlyList<HistoryEntry> snapshot,
        int version,
        CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_entriesLock)
            {
                if (version != _version)
                {
                    return;
                }
            }

            await JsonFileWriter.WriteAtomicAsync(_historyPath, snapshot, JsonFileWriter.CompactOptions).ConfigureAwait(false);
            lock (_entriesLock)
            {
                // 仅当没有更新的内存版本时标记已保存，避免覆盖更新的 dirty 状态
                if (version == _version)
                {
                    _savedVersion = version;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            FileLogger.LogException(ex, "Save history");
            throw;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private static List<HistoryEntry> SanitizeEntries(IEnumerable<HistoryEntry?> loaded)
    {
        var result = new List<HistoryEntry>(AppConfig.Data.MaxHistoryEntries);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fallbackTime = DateTime.UtcNow;

        foreach (var entry in loaded)
        {
            if (entry is null ||
                !EntryText.TryNormalizeHttpUrl(entry.Url, out var normalizedUrl) ||
                !seen.Add(normalizedUrl))
            {
                continue;
            }

            var title = EntryText.TruncateTitle(entry.Title);
            result.Add(new HistoryEntry
            {
                Url = normalizedUrl,
                Title = title.Length == 0 ? normalizedUrl : title,
                VisitedAt = EntryText.NormalizeUtcTimestamp(entry.VisitedAt, fallbackTime),
            });

            if (result.Count == AppConfig.Data.MaxHistoryEntries)
            {
                break;
            }
        }

        return result;
    }

    private static bool EntriesMatch(IReadOnlyList<HistoryEntry?> loaded, IReadOnlyList<HistoryEntry> sanitized)
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
                !string.Equals(source.Url, target.Url, StringComparison.Ordinal) ||
                !string.Equals(source.Title, target.Title, StringComparison.Ordinal) ||
                source.VisitedAt != target.VisitedAt)
            {
                return false;
            }
        }

        return true;
    }
}
