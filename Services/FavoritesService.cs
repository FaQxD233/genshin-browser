using System.IO;
using System.Text.Json;
using GenshinBrowser.Constants;
using GenshinBrowser.Models;

namespace GenshinBrowser.Services;

public sealed class FavoritesService
{
    private readonly string _favoritesPath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly object _entriesLock = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private List<FavoriteEntry> _entries = new();

    public FavoritesService(string favoritesPath)
    {
        _favoritesPath = favoritesPath;
        LoadFromDisk();
    }

    public IReadOnlyList<FavoriteEntry> GetEntries()
    {
        lock (_entriesLock)
        {
            return _entries.ToList();
        }
    }

    public async Task AddOrUpdateAsync(string url, string title)
    {
        List<FavoriteEntry> snapshot;
        lock (_entriesLock)
        {
            _entries.RemoveAll(item => string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase));
            _entries.Insert(0, new FavoriteEntry
            {
                Url = url,
                Title = title,
                SavedAt = DateTime.UtcNow,
            });

            if (_entries.Count > AppConfig.Data.MaxFavoriteEntries)
            {
                _entries = _entries.Take(AppConfig.Data.MaxFavoriteEntries).ToList();
            }

            snapshot = _entries.ToList();
        }

        await SaveAsync(snapshot).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string url)
    {
        List<FavoriteEntry> snapshot;
        lock (_entriesLock)
        {
            _entries.RemoveAll(item => string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase));
            snapshot = _entries.ToList();
        }

        await SaveAsync(snapshot).ConfigureAwait(false);
    }

    public bool Contains(string url)
    {
        lock (_entriesLock)
        {
            return _entries.Any(item => string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase));
        }
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
            // 文件损坏或无法访问，从空列表开始
            _entries = new List<FavoriteEntry>();
        }
    }

    private async Task SaveAsync(IReadOnlyList<FavoriteEntry> snapshot)
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await JsonFileWriter.WriteAtomicAsync(_favoritesPath, snapshot, _jsonOptions).ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
