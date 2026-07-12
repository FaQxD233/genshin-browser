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

    /// <summary>
    /// 缓存的只读快照。仅在 _entries 变更时重建，避免每次 GetEntries() 都 ToList() 分配新副本。
    /// UI 读取用；落盘必须用 <see cref="CreatePersistSnapshot"/> 的独立拷贝。
    /// </summary>
    private IReadOnlyList<FavoriteEntry>? _snapshotCache;

    public FavoritesService(string favoritesPath)
    {
        _favoritesPath = favoritesPath;
        LoadFromDisk();
    }

    public IReadOnlyList<FavoriteEntry> GetEntries()
    {
        lock (_entriesLock)
        {
            return _snapshotCache ??= _entries.AsReadOnly();
        }
    }

    public async Task AddOrUpdateAsync(string url, string title)
    {
        var normalizedUrl = UrlNormalizer.Normalize(url);
        var safeTitle = EntryText.TruncateTitle(title);

        IReadOnlyList<FavoriteEntry> snapshot;
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
            snapshot = CreatePersistSnapshot();
            _snapshotCache = _entries.AsReadOnly();
        }

        await SaveAsync(snapshot).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string url)
    {
        var normalizedUrl = UrlNormalizer.Normalize(url);

        IReadOnlyList<FavoriteEntry> snapshot;
        lock (_entriesLock)
        {
            _entries.RemoveAll(item => string.Equals(item.Url, normalizedUrl, StringComparison.OrdinalIgnoreCase));
            _snapshotCache = null;
            snapshot = CreatePersistSnapshot();
            _snapshotCache = _entries.AsReadOnly();
        }

        await SaveAsync(snapshot).ConfigureAwait(false);
    }

    public bool Contains(string url)
    {
        var normalizedUrl = UrlNormalizer.Normalize(url);
        lock (_entriesLock)
        {
            return _entries.Any(item => string.Equals(item.Url, normalizedUrl, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Dispose()
    {
        _saveGate.Dispose();
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

    private void LoadFromDisk()
    {
        if (!File.Exists(_favoritesPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_favoritesPath);
            _entries = JsonSerializer.Deserialize<List<FavoriteEntry>>(json) ?? new List<FavoriteEntry>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            FileLogger.LogException(ex, "Load favorites");
            // 文件损坏或无法访问，从空列表开始
            _entries = new List<FavoriteEntry>();
        }
    }

    private async Task SaveAsync(IReadOnlyList<FavoriteEntry> snapshot)
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await JsonFileWriter.WriteAtomicAsync(_favoritesPath, snapshot, JsonFileWriter.CompactOptions).ConfigureAwait(false);
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
}
