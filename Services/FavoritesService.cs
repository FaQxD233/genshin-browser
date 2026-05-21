using System.IO;
using System.Text.Json;
using GenshinBrowser.Models;

namespace GenshinBrowser.Services;

public sealed class FavoritesService
{
    private const int MaxEntries = 100;
    private readonly string _favoritesPath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private List<FavoriteEntry> _entries = new();

    public FavoritesService(string favoritesPath)
    {
        _favoritesPath = favoritesPath;
        LoadFromDisk();
    }

    public IReadOnlyList<FavoriteEntry> GetEntries()
    {
        return _entries;
    }

    public async Task AddOrUpdateAsync(string url, string title)
    {
        _entries.RemoveAll(item => string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase));
        _entries.Insert(0, new FavoriteEntry
        {
            Url = url,
            Title = title,
            SavedAt = DateTime.Now,
        });

        if (_entries.Count > MaxEntries)
        {
            _entries = _entries.Take(MaxEntries).ToList();
        }

        await SaveAsync();
    }

    public async Task RemoveAsync(string url)
    {
        _entries.RemoveAll(item => string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase));
        await SaveAsync();
    }

    public bool Contains(string url)
    {
        return _entries.Any(item => string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase));
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
        catch
        {
            _entries = new List<FavoriteEntry>();
        }
    }

    private async Task SaveAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_favoritesPath)!);
        await using var stream = File.Create(_favoritesPath);
        await JsonSerializer.SerializeAsync(stream, _entries, _jsonOptions);
    }
}
