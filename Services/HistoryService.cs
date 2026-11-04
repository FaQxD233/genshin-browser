using GenshinBrowser.Constants;
using GenshinBrowser.Models;

namespace GenshinBrowser.Services;

/// <summary>浏览记录（history.json）：最近访问在前，最多 <see cref="AppConfig.Data.MaxHistoryEntries"/> 条。</summary>
public sealed class HistoryService : UrlEntryStore<HistoryEntry>
{
    public HistoryService(string historyPath)
        : base(
            historyPath,
            AppConfig.Data.MaxHistoryEntries,
            AppConfig.Data.MaxHistoryFileSizeBytes,
            AppConfig.Data.HistorySaveDebounceMs,
            "history")
    {
    }

    /// <summary>追加 / 更新一条浏览记录（同一网址移到最前）。内存立即生效，写盘防抖合并。</summary>
    public Task AddEntryAsync(string url, string title) => UpsertAsync(url, title);

    internal static List<HistoryEntry> SanitizeEntries(IEnumerable<HistoryEntry?> loaded) =>
        Sanitize(loaded, AppConfig.Data.MaxHistoryEntries);
}
