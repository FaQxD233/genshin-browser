using System.IO;
using System.Text.Json;
using GenshinBrowser.Models;

namespace GenshinBrowser.Services;

public sealed class HistoryService
{
    private const int MaxEntries = 50;
    private readonly string _historyPath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private List<HistoryEntry> _entries = new();

    public HistoryService(string historyPath)
    {
        _historyPath = historyPath;
        LoadFromDisk();
    }

    public IReadOnlyList<HistoryEntry> GetEntries()
    {
        return _entries;
    }

    public async Task AddEntryAsync(string url, string title)
    {
        _entries.RemoveAll(item => string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase));
        _entries.Insert(0, new HistoryEntry
        {
            Url = url,
            Title = title,
            VisitedAt = DateTime.Now,
        });

        if (_entries.Count > MaxEntries)
        {
            _entries = _entries.Take(MaxEntries).ToList();
        }

        await SaveAsync();
    }

    private void LoadFromDisk()
    {
        if (!File.Exists(_historyPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_historyPath);
            _entries = JsonSerializer.Deserialize<List<HistoryEntry>>(json) ?? new List<HistoryEntry>();
        }
        catch
        {
            _entries = new List<HistoryEntry>();
        }
    }

    private async Task SaveAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);
        await using var stream = File.Create(_historyPath);
        await JsonSerializer.SerializeAsync(stream, _entries, _jsonOptions);
    }
}
