namespace GenshinBrowser.Utils;

/// <summary>
/// 按网址合并两组条目（收藏、浏览记录导入共用）。两边的网址都应已归一（见 <see cref="EntryText.TryNormalizeHttpUrl"/>）。
/// </summary>
public static class EntryMerge
{
    /// <summary>
    /// 合并：同一网址（忽略大小写）保留时间较新的一条，按时间倒序，最多 <paramref name="maxCount"/> 条。
    /// 时间相同时保留 <paramref name="current"/> 里的那条。
    /// </summary>
    public static List<T> MergeByUrl<T>(
        IEnumerable<T> current,
        IEnumerable<T> incoming,
        Func<T, string> url,
        Func<T, DateTime> time,
        int maxCount)
    {
        var byUrl = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in current.Concat(incoming))
        {
            var key = url(entry);
            if (!byUrl.TryGetValue(key, out var existing) || time(entry) > time(existing))
            {
                byUrl[key] = entry;
            }
        }

        return byUrl.Values.OrderByDescending(time).Take(maxCount).ToList();
    }

    /// <summary><paramref name="current"/> 里有、<paramref name="incoming"/> 里没有的网址数：换成导入的列表时会被替换掉的条数。</summary>
    public static int CountMissing<T>(IEnumerable<T> current, IEnumerable<T> incoming, Func<T, string> url)
    {
        var incomingUrls = new HashSet<string>(incoming.Select(url), StringComparer.OrdinalIgnoreCase);
        return current.Count(entry => !incomingUrls.Contains(url(entry)));
    }
}
