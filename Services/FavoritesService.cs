using GenshinBrowser.Constants;
using GenshinBrowser.Models;

namespace GenshinBrowser.Services;

/// <summary>收藏（favorites.json）：最近收藏在前，最多 <see cref="AppConfig.Data.MaxFavoriteEntries"/> 条。</summary>
public sealed class FavoritesService : UrlEntryStore<FavoriteEntry>
{
    public FavoritesService(string favoritesPath)
        : base(
            favoritesPath,
            AppConfig.Data.MaxFavoriteEntries,
            AppConfig.Data.MaxFavoritesFileSizeBytes,
            AppConfig.Data.FavoritesSaveDebounceMs,
            "favorites")
    {
    }

    /// <summary>添加 / 更新收藏（同一网址移到最前）。内存立即生效，写盘防抖合并。</summary>
    public Task AddOrUpdateAsync(string url, string title) => UpsertAsync(url, title);

    internal static List<FavoriteEntry> SanitizeEntries(IEnumerable<FavoriteEntry?> loaded) =>
        Sanitize(loaded, AppConfig.Data.MaxFavoriteEntries);
}
