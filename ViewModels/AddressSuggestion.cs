using GenshinBrowser.Constants;
using GenshinBrowser.Models;
using GenshinBrowser.Utils;

namespace GenshinBrowser.ViewModels;

/// <summary>地址栏联想的一条候选（来自收藏或历史，不可变）。</summary>
public sealed record AddressSuggestion(string Title, string DisplayUrl, string Url, bool IsFavorite)
{
    /// <summary>
    /// 按输入构建候选：收藏在前、历史按最近访问在后，URL 去重（忽略大小写），最多 <paramref name="maxCount"/> 条。
    /// 匹配规则与列表搜索一致（<see cref="EntryText.MatchesQuery"/>）。输入为空或与当前地址相同时不给候选。
    /// 数据取服务层真源：控制窗 VM 只同步可见 Tab，隐藏 Tab 的缓存可能过期。
    /// </summary>
    internal static IReadOnlyList<AddressSuggestion> Build(
        IReadOnlyList<FavoriteEntry> favorites,
        IReadOnlyList<HistoryEntry> history,
        string? input,
        string? currentAddress,
        int maxCount = AppConfig.Ui.MaxAddressSuggestions)
    {
        var query = input?.Trim() ?? string.Empty;
        if (query.Length == 0 ||
            maxCount <= 0 ||
            string.Equals(query, currentAddress?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<AddressSuggestion>();
        }

        var results = new List<AddressSuggestion>(maxCount);
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool TryAdd(string url, string title, bool isFavorite)
        {
            if (results.Count >= maxCount)
            {
                return false;
            }

            if (EntryText.MatchesQuery(title, url, query) && seenUrls.Add(url))
            {
                var displayUrl = EntryText.ToDisplayUrl(url);
                results.Add(new AddressSuggestion(
                    string.IsNullOrWhiteSpace(title) ? displayUrl : title,
                    displayUrl,
                    url,
                    isFavorite));
            }

            return true;
        }

        foreach (var favorite in favorites)
        {
            if (!TryAdd(favorite.Url, favorite.Title, isFavorite: true))
            {
                return results;
            }
        }

        foreach (var entry in history)
        {
            if (!TryAdd(entry.Url, entry.Title, isFavorite: false))
            {
                break;
            }
        }

        return results;
    }
}
