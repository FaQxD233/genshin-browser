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
    private int _version;
    private int _savedVersion;
    private bool _disposed;

    /// <summary>
    /// 缓存的只读快照。仅在 _entries 变更时重建，避免每次 GetEntries() 都 ToList() 分配新副本。
    /// UI 读取用；落盘必须用 <see cref="CreatePersistSnapshot"/> 的独立拷贝。
    /// </summary>
    private IReadOnlyList<FavoriteEntry>? _snapshotCache;

    public FavoritesService(string favoritesPath)
    {
        _favoritesPath = favoritesPath;
        _version = LoadFromDisk() ? 1 : 0;
    }

    public IReadOnlyList<FavoriteEntry> GetEntries()
    {
        lock (_entriesLock)
        {
            return _snapshotCache ??= _entries.ToArray();
        }
    }

    public async Task AddOrUpdateAsync(string url, string title)
    {
        if (!EntryText.TryNormalizeHttpUrl(url, out var normalizedUrl))
        {
            return;
        }

        var safeTitle = EntryText.TruncateTitle(title);
        if (safeTitle.Length == 0)
        {
            safeTitle = normalizedUrl;
        }

        IReadOnlyList<FavoriteEntry> snapshot;
        int version;
        lock (_entriesLock)
        {
            _entries.RemoveAll(item => string.Equals(item.Url, normalizedUrl, StringComparison.OrdinalIgnoreCase));
            _entries.Insert(0, new FavoriteEntry
            {
                Url = normalizedUrl,
                Title = safeTitle,
                SavedAt = DateTime.UtcNow,
            });

            if (_entries.Count > AppConfig.Data.MaxFavoriteEntries)
            {
                _entries.RemoveRange(AppConfig.Data.MaxFavoriteEntries, _entries.Count - AppConfig.Data.MaxFavoriteEntries);
            }

            _snapshotCache = null;
            _version++;
            version = _version;
            snapshot = CreatePersistSnapshot();
            _snapshotCache = _entries.ToArray();
        }

        await SaveAsync(snapshot, version).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string url)
    {
        if (!EntryText.TryNormalizeHttpUrl(url, out var normalizedUrl))
        {
            return;
        }

        IReadOnlyList<FavoriteEntry> snapshot;
        int version;
        lock (_entriesLock)
        {
            _entries.RemoveAll(item => string.Equals(item.Url, normalizedUrl, StringComparison.OrdinalIgnoreCase));
            _snapshotCache = null;
            _version++;
            version = _version;
            snapshot = CreatePersistSnapshot();
            _snapshotCache = _entries.ToArray();
        }

        await SaveAsync(snapshot, version).ConfigureAwait(false);
    }

    public bool Contains(string url)
    {
        if (!EntryText.TryNormalizeHttpUrl(url, out var normalizedUrl))
        {
            return false;
        }

        lock (_entriesLock)
        {
            return _entries.Any(item => string.Equals(item.Url, normalizedUrl, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _saveGate.Dispose();
    }

    public async Task FlushAsync()
    {
        while (true)
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

    private bool LoadFromDisk()
    {
        if (!File.Exists(_favoritesPath))
        {
            return false;
        }

        try
        {
            var json = JsonFileWriter.ReadAllTextBounded(_favoritesPath, AppConfig.Data.MaxFavoritesFileSizeBytes);
            var loaded = JsonSerializer.Deserialize<List<FavoriteEntry?>>(json) ?? new List<FavoriteEntry?>();
            _entries = SanitizeEntries(loaded);
            return !EntriesMatch(loaded, _entries);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            FileLogger.LogException(ex, "Load favorites");
            // 文件损坏或无法访问，从空列表开始
            _entries = new List<FavoriteEntry>();
            return false;
        }
    }

    private async Task SaveAsync(IReadOnlyList<FavoriteEntry> snapshot, int version)
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
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
            _saveGate.Release();
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
