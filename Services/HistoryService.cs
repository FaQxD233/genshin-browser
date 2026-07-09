using System.IO;
using System.Text.Json;
using GenshinBrowser.Constants;
using GenshinBrowser.Models;

namespace GenshinBrowser.Services;

public sealed class HistoryService : IDisposable
{
    private readonly string _historyPath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly object _entriesLock = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private List<HistoryEntry> _entries = new();

    public HistoryService(string historyPath)
    {
        _historyPath = historyPath;
        LoadFromDisk();
    }

    public IReadOnlyList<HistoryEntry> GetEntries()
    {
        lock (_entriesLock)
        {
            return _entries.ToList();
        }
    }

    public async Task AddEntryAsync(string url, string title)
    {
        List<HistoryEntry> snapshot;
        lock (_entriesLock)
        {
            _entries.RemoveAll(item => string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase));
            _entries.Insert(0, new HistoryEntry
            {
                Url = url,
                Title = title,
                VisitedAt = DateTime.UtcNow,
            });

            if (_entries.Count > AppConfig.Data.MaxHistoryEntries)
            {
                _entries.RemoveRange(AppConfig.Data.MaxHistoryEntries, _entries.Count - AppConfig.Data.MaxHistoryEntries);
            }

            snapshot = _entries.ToList();
        }

        await SaveAsync(snapshot).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string url)
    {
        List<HistoryEntry> snapshot;
        lock (_entriesLock)
        {
            _entries.RemoveAll(item => string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase));
            snapshot = _entries.ToList();
        }

        await SaveAsync(snapshot).ConfigureAwait(false);
    }

    public async Task ClearAllAsync()
    {
        List<HistoryEntry> snapshot;
        lock (_entriesLock)
        {
            _entries.Clear();
            snapshot = _entries.ToList();
        }

        await SaveAsync(snapshot).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _saveGate.Dispose();
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
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            FileLogger.LogException(ex, "Load history");
            // 文件损坏或无法访问，从空列表开始
            _entries = new List<HistoryEntry>();
        }
    }

    private async Task SaveAsync(IReadOnlyList<HistoryEntry> snapshot)
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await JsonFileWriter.WriteAtomicAsync(_historyPath, snapshot, _jsonOptions).ConfigureAwait(false);
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
}
