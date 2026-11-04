using GenshinBrowser.Models;
using GenshinBrowser.Utils;

namespace GenshinBrowser.Services;

/// <summary>
/// 收藏与浏览记录共用：按网址（归一后、忽略大小写）去重的条目列表，最新的在前，超过上限丢掉最旧的。
/// 内存立即生效，写盘交给 <see cref="DebouncedJsonSaver{TSnapshot}"/>；启动时异步加载，读不出来时原文件改名保留（见 <see cref="DataFile"/>）。
/// </summary>
public abstract class UrlEntryStore<T> : IDisposable
    where T : class, IUrlEntry, new()
{
    private readonly string _path;
    private readonly int _maxEntries;
    private readonly int _maxFileBytes;
    private readonly string _logName;
    private readonly object _entriesLock = new();
    // URL 镜像集合：O(1) 判断网址是否已在列表里（Contains 每次导航都会调），避免每次新增都全表扫描
    private readonly HashSet<string> _urlSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly DebouncedJsonSaver<List<T>> _saver;
    private List<T> _entries = new();

    /// <summary>
    /// 缓存的只读包装。仅在列表变更时作废，避免每次 GetEntries() 都分配；落盘用 <see cref="CreatePersistSnapshot"/> 的独立拷贝。
    /// </summary>
    private IReadOnlyList<T>? _snapshotCache;

    protected UrlEntryStore(string path, int maxEntries, int maxFileBytes, int saveDebounceMs, string logName)
    {
        _path = path;
        _maxEntries = maxEntries;
        _maxFileBytes = maxFileBytes;
        _logName = logName;
        _saver = new DebouncedJsonSaver<List<T>>(
            path, JsonFileWriter.CompactOptions, saveDebounceMs, $"Save {logName}", _entriesLock, CreatePersistSnapshot);
    }

    /// <summary>启动加载时读不出来的情况（原文件已改名保留，或改名失败、本次不写盘）；正常为 null。</summary>
    public DataFileProblem? LoadProblem { get; private set; }

    /// <summary>
    /// 异步从磁盘加载（构造函数不读盘，避免阻塞首帧）。应在 UI 显示列表前调用一次。
    /// 加载期间已有的新增排在磁盘数据之前、同一网址以内存里的为准，不会被整体覆盖掉。
    /// </summary>
    public async Task InitializeAsync()
    {
        var (loaded, problem) = await DataFile.ReadAsync<List<T?>>(_path, _maxFileBytes, options: null, $"Load {_logName}")
            .ConfigureAwait(false);
        var sanitized = Sanitize(loaded ?? new List<T?>(), _maxEntries);
        bool changed;
        lock (_entriesLock)
        {
            LoadProblem = problem;
            if (problem is { PreservedPath: null })
            {
                _saver.BlockWrites();
            }

            _entries = _entries.Count == 0 ? sanitized : MergeLoadedAfterPending(_entries, sanitized);
            RebuildUrlSetLocked();
            // 磁盘内容需要清洗（非法网址、重复、超长标题等）或混入了加载期间的新增时，写回一次
            changed = loaded is not null && !EntriesMatch(loaded, _entries);
        }

        if (changed)
        {
            _saver.MarkChanged();
        }
    }

    public IReadOnlyList<T> GetEntries()
    {
        lock (_entriesLock)
        {
            return _snapshotCache ??= _entries.AsReadOnly();
        }
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

    /// <summary>删除指定网址的条目。内存立即生效，写盘防抖合并。</summary>
    public Task RemoveAsync(string url)
    {
        if (!EntryText.TryNormalizeHttpUrl(url, out var normalizedUrl))
        {
            return Task.CompletedTask;
        }

        lock (_entriesLock)
        {
            if (!_urlSet.Remove(normalizedUrl))
            {
                return Task.CompletedTask;
            }

            _entries.RemoveAll(entry => string.Equals(entry.Url, normalizedUrl, StringComparison.OrdinalIgnoreCase));
            _snapshotCache = null;
        }

        _saver.MarkChanged();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 导入（配置导入）：<paramref name="merge"/> 为 true 时与现有条目合并，同一网址保留时间较新的一条；
    /// false 时换成导入的列表。在锁内完成，导入期间新增的不会丢；完成后立即落盘，不等防抖。
    /// </summary>
    public async Task ImportAsync(IEnumerable<T?> entries, bool merge)
    {
        var incoming = Sanitize(entries, _maxEntries);
        lock (_entriesLock)
        {
            _entries = merge
                ? EntryMerge.MergeByUrl(_entries, incoming, entry => entry.Url, entry => entry.Timestamp, _maxEntries)
                : incoming;
            RebuildUrlSetLocked();
        }

        _saver.MarkChanged();
        await FlushAsync().ConfigureAwait(false);
    }

    /// <summary>立即写入尚未落盘的改动。关闭应用前应调用；Dispose 不再写盘。</summary>
    public Task FlushAsync() => _saver.FlushAsync();

    public void Dispose()
    {
        // 关闭路径应先 await FlushAsync()；此处不再同步写盘，避免 UI 关闭时卡死。
        _saver.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>新增或更新一条（同一网址移到最前）。内存立即生效，写盘防抖合并。</summary>
    protected Task UpsertAsync(string url, string title)
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
            // 仅在网址已存在时才删旧条目（O(n)），新网址不做全表扫描
            if (_urlSet.Contains(normalizedUrl))
            {
                _entries.RemoveAll(entry => string.Equals(entry.Url, normalizedUrl, StringComparison.OrdinalIgnoreCase));
            }

            _entries.Insert(0, Create(normalizedUrl, safeTitle, DateTime.UtcNow));
            _urlSet.Add(normalizedUrl);
            if (_entries.Count > _maxEntries)
            {
                for (var i = _maxEntries; i < _entries.Count; i++)
                {
                    _urlSet.Remove(_entries[i].Url);
                }

                _entries.RemoveRange(_maxEntries, _entries.Count - _maxEntries);
            }

            _snapshotCache = null;
        }

        _saver.MarkChanged();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 清洗外部来的条目（磁盘文件、导入的配置）：网址须是 http(s)，归一后去重；标题截断，空标题用网址；时间归一；最多 <paramref name="maxEntries"/> 条。
    /// </summary>
    protected static List<T> Sanitize(IEnumerable<T?> loaded, int maxEntries)
    {
        var result = new List<T>(maxEntries);
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
            result.Add(Create(
                normalizedUrl,
                title.Length == 0 ? normalizedUrl : title,
                EntryText.NormalizeUtcTimestamp(entry.Timestamp, fallbackTime)));
            if (result.Count == maxEntries)
            {
                break;
            }
        }

        return result;
    }

    private static T Create(string url, string title, DateTime timestamp) =>
        new T { Url = url, Title = title, Timestamp = timestamp };

    /// <summary>加载期间已有的新增在前（同一网址以它为准），磁盘数据在后，截到上限。</summary>
    private List<T> MergeLoadedAfterPending(List<T> pending, List<T> loaded)
    {
        var merged = new List<T>(pending.Count + loaded.Count);
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in pending.Concat(loaded))
        {
            if (urls.Add(entry.Url))
            {
                merged.Add(entry);
            }
        }

        if (merged.Count > _maxEntries)
        {
            merged.RemoveRange(_maxEntries, merged.Count - _maxEntries);
        }

        return merged;
    }

    private void RebuildUrlSetLocked()
    {
        _urlSet.Clear();
        foreach (var entry in _entries)
        {
            _urlSet.Add(entry.Url);
        }

        _snapshotCache = null;
    }

    /// <summary>在持锁状态下生成落盘用独立拷贝：序列化在锁外进行，期间 UI 仍可能改列表。</summary>
    private List<T> CreatePersistSnapshot() =>
        _entries.Select(entry => Create(entry.Url, entry.Title, entry.Timestamp)).ToList();

    private static bool EntriesMatch(IReadOnlyList<T?> loaded, IReadOnlyList<T> sanitized)
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
                source.Timestamp != target.Timestamp)
            {
                return false;
            }
        }

        return true;
    }
}
