using System.IO;
using System.Text.Json;
using GenshinBrowser.Constants;
using GenshinBrowser.Models;
using GenshinBrowser.Utils;

namespace GenshinBrowser.Services;

public sealed class FavoritesService : IDisposable
{
    private readonly string _favoritesPath;
    private readonly object _entriesLock = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private List<FavoriteEntry> _entries = new();
    // URL 镜像集合：O(1) Contains 查询（每次导航都会调），同时用于 AddOrUpdate/Remove 的去重检查
    private readonly HashSet<string> _urlSet = new(StringComparer.OrdinalIgnoreCase);
    private int _version;
    private int _savedVersion;
    private bool _disposed;

    private CancellationTokenSource? _saveDebounceCts;
    private readonly object _debounceLock = new();

    /// <summary>
    /// 缓存的只读快照。仅在 _entries 变更时重建，避免每次 GetEntries() 都 ToList() 分配新副本。
    /// UI 读取用；落盘必须用 <see cref="CreatePersistSnapshot"/> 的独立拷贝。
    /// </summary>
    private IReadOnlyList<FavoriteEntry>? _snapshotCache;

    public FavoritesService(string favoritesPath)
    {
        _favoritesPath = favoritesPath;
    }

    /// <summary>
    /// 异步从磁盘加载收藏。构造函数不再同步读盘，避免阻塞首帧。
    /// 应在 UI 准备好显示收藏前调用一次（如 MainWindow_OnLoaded）。
    /// </summary>
    public async Task InitializeAsync()
    {
        _version = await LoadFromDiskAsync().ConfigureAwait(false) ? 1 : 0;
    }

    public IReadOnlyList<FavoriteEntry> GetEntries()
    {
        lock (_entriesLock)
        {
            return _snapshotCache ??= _entries.AsReadOnly();
        }
    }

    /// <summary>
    /// 添加/更新收藏。内存立即生效；磁盘写入按 <see cref="AppConfig.Data.FavoritesSaveDebounceMs"/> 防抖合并。
    /// </summary>
    public Task AddOrUpdateAsync(string url, string title)
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
            _entries.Insert(0, new FavoriteEntry
            {
                Url = normalizedUrl,
                Title = safeTitle,
                SavedAt = DateTime.UtcNow,
            });
            _urlSet.Add(normalizedUrl);

            if (_entries.Count > AppConfig.Data.MaxFavoriteEntries)
            {
                for (var i = AppConfig.Data.MaxFavoriteEntries; i < _entries.Count; i++)
                {
                    _urlSet.Remove(_entries[i].Url);
                }
                _entries.RemoveRange(AppConfig.Data.MaxFavoriteEntries, _entries.Count - AppConfig.Data.MaxFavoriteEntries);
            }

            _snapshotCache = null;
            _version++;
            _snapshotCache = _entries.AsReadOnly();
        }

        QueueDebouncedSave();
        return Task.CompletedTask;
    }

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
            _snapshotCache = _entries.AsReadOnly();
        }

        QueueDebouncedSave();
        return Task.CompletedTask;
    }

    public bool Contains(string url)
    {
        if (!EntryText.TryNormalizeHttpUrl(url, out var normalizedUrl))
        {
            return false;
        }

        lock (_entriesLock)
        {
            return _urlSet.Contains(normalizedUrl);
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

    public async Task FlushAsync()
    {
        CancelDebouncedSave();

        while (!_disposed)
        {
            IReadOnlyList<FavoriteEntry> snapshot;
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
            await Task.Delay(AppConfig.Data.FavoritesSaveDebounceMs, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<FavoriteEntry>? snapshot = null;
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
    private List<FavoriteEntry> CreatePersistSnapshot()
    {
        var copy = new List<FavoriteEntry>(_entries.Count);
        for (var i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            copy.Add(new FavoriteEntry
            {
                Url = e.Url,
                Title = e.Title,
                SavedAt = e.SavedAt,
            });
        }

        return copy;
    }

    private async Task<bool> LoadFromDiskAsync()
    {
        if (!File.Exists(_favoritesPath))
        {
            return false;
        }

        try
        {
            // 异步读盘 + 在线程池反序列化，避免阻塞 UI 线程
            var json = await JsonFileWriter.ReadAllTextBoundedAsync(
                _favoritesPath,
                AppConfig.Data.MaxFavoritesFileSizeBytes).ConfigureAwait(false);

            var (loaded, sanitized) = await Task.Run(() =>
            {
                var l = JsonSerializer.Deserialize<List<FavoriteEntry?>>(json) ?? new List<FavoriteEntry?>();
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
                _snapshotCache = _entries.AsReadOnly();
                return !EntriesMatch(loaded, _entries);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            FileLogger.LogException(ex, "Load favorites");
            lock (_entriesLock)
            {
                _entries = new List<FavoriteEntry>();
                _urlSet.Clear();
            }
            return false;
        }
    }

    private async Task SaveAsync(
        IReadOnlyList<FavoriteEntry> snapshot,
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

            await JsonFileWriter.WriteAtomicAsync(_favoritesPath, snapshot, JsonFileWriter.CompactOptions).ConfigureAwait(false);
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
            FileLogger.LogException(ex, "Save favorites");
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

    private static List<FavoriteEntry> SanitizeEntries(IEnumerable<FavoriteEntry?> loaded)
    {
        var result = new List<FavoriteEntry>(AppConfig.Data.MaxFavoriteEntries);
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
            result.Add(new FavoriteEntry
            {
                Url = normalizedUrl,
                Title = title.Length == 0 ? normalizedUrl : title,
                SavedAt = EntryText.NormalizeUtcTimestamp(entry.SavedAt, fallbackTime),
            });

            if (result.Count == AppConfig.Data.MaxFavoriteEntries)
            {
                break;
            }
        }

        return result;
    }

    private static bool EntriesMatch(IReadOnlyList<FavoriteEntry?> loaded, IReadOnlyList<FavoriteEntry> sanitized)
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
                source.SavedAt != target.SavedAt)
            {
                return false;
            }
        }

        return true;
    }
}
