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
    // URL 镜像集合：O(1) 判断 URL 是否已存在，避免 AddEntryAsync/RemoveAsync 中 O(n) RemoveAll 全表扫描。
    private readonly HashSet<string> _urlSet = new(StringComparer.OrdinalIgnoreCase);

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
    }

    /// <summary>
    /// 异步从磁盘加载历史。构造函数不再同步读盘，避免阻塞首帧。
    /// 应在 UI 准备好显示历史前调用一次（如 MainWindow_OnLoaded）。
    /// </summary>
    public async Task InitializeAsync()
    {
        if (await LoadFromDiskAsync().ConfigureAwait(false))
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
            // 仅在 URL 已存在时才做删除（O(n)），避免新 URL 的全表扫描
            if (_urlSet.Contains(normalizedUrl))
            {
                _entries.RemoveAll(item => string.Equals(item.Url, normalizedUrl, StringComparison.OrdinalIgnoreCase));
            }
            _entries.Insert(0, new HistoryEntry
            {
                Url = normalizedUrl,
                Title = safeTitle,
                VisitedAt = DateTime.UtcNow,
            });
            _urlSet.Add(normalizedUrl);

            if (_entries.Count > AppConfig.Data.MaxHistoryEntries)
            {
                // 同步移除被裁剪条目的 URL 镜像
                for (var i = AppConfig.Data.MaxHistoryEntries; i < _entries.Count; i++)
                {
                    _urlSet.Remove(_entries[i].Url);
                }
                _entries.RemoveRange(AppConfig.Data.MaxHistoryEntries, _entries.Count - AppConfig.Data.MaxHistoryEntries);
            }

            _snapshotCache = null;
            _version++;
        }

        QueueDebouncedSave();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 删除指定历史。内存立即生效；磁盘写入按 <see cref="AppConfig.Data.HistorySaveDebounceMs"/> 防抖合并。
    /// </summary>
    public Task RemoveAsync(string url)
    {
        if (!EntryText.TryNormalizeHttpUrl(url, out var normalizedUrl))
        {
            return Task.CompletedTask;
        }

        lock (_entriesLock)
        {
            if (!_urlSet.Contains(normalizedUrl))
            {
                return Task.CompletedTask;
            }
            _entries.RemoveAll(item => string.Equals(item.Url, normalizedUrl, StringComparison.OrdinalIgnoreCase));
            _urlSet.Remove(normalizedUrl);
            _snapshotCache = null;
            _version++;
        }

        QueueDebouncedSave();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 清空所有历史。内存立即生效；磁盘写入按 <see cref="AppConfig.Data.HistorySaveDebounceMs"/> 防抖合并。
    /// </summary>
    public Task ClearAllAsync()
    {
        lock (_entriesLock)
        {
            _entries.Clear();
            _urlSet.Clear();
            _snapshotCache = null;
            _version++;
        }

        QueueDebouncedSave();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 将尚未落盘的变更立即写入。关闭应用前应调用；Dispose 不再二次写盘。
    /// </summary>
    public async Task FlushAsync()
    {
        CancelDebouncedSave();

        while (!_disposed)
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
        CancellationToken token;
        lock (_debounceLock)
        {
            // _disposed 检查必须在锁内：否则 Dispose 可能在检查通过后、新 CTS 创建前清空 _saveDebounceCts，
            // 导致此处 new 出一个永远不被取消的 CTS，DebouncedSaveAsync 在 500ms 后向已关闭的服务写盘。
            if (_disposed)
            {
                return;
            }

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ObjectDisposedException)
        {
            // SaveAsync 已记录具体异常；ODE 为 Dispose 与后台写盘的关闭竞态；后台任务不能留下未观察异常。
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

    private async Task<bool> LoadFromDiskAsync()
    {
        if (!File.Exists(_historyPath))
        {
            return false;
        }

        try
        {
            // 异步读盘 + 在线程池反序列化，避免阻塞 UI 线程
            var json = await JsonFileWriter.ReadAllTextBoundedAsync(
                _historyPath,
                AppConfig.Data.MaxHistoryFileSizeBytes).ConfigureAwait(false);

            var (loaded, sanitized) = await Task.Run(() =>
            {
                var l = JsonSerializer.Deserialize<List<HistoryEntry?>>(json) ?? new List<HistoryEntry?>();
                return (l, SanitizeEntries(l));
            }).ConfigureAwait(false);

            lock (_entriesLock)
            {
                _entries = sanitized;
                _urlSet.Clear();
                foreach (var e in _entries)
                {
                    _urlSet.Add(e.Url);
                }
                var changed = !EntriesMatch(loaded, _entries);
                _version = changed ? 1 : 0;
                _savedVersion = 0;
                _snapshotCache = null;
                return changed;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            FileLogger.LogException(ex, "Load history");
            lock (_entriesLock)
            {
                _entries = new List<HistoryEntry>();
                _urlSet.Clear();
                _version = 0;
                _savedVersion = 0;
            }
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
