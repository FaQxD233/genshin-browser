using GenshinBrowser.Constants;

namespace GenshinBrowser.Utils;

/// <summary>
/// 历史 / 收藏等条目文本的统一处理。
/// </summary>
public static class EntryText
{
    /// <summary>
    /// 截断标题：去空白，超过 <see cref="AppConfig.Data.MaxEntryTitleLength"/> 时裁切。
    /// </summary>
    public static string TruncateTitle(string? title, int maxLength = AppConfig.Data.MaxEntryTitleLength)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        if (maxLength < 1)
        {
            maxLength = AppConfig.Data.MaxEntryTitleLength;
        }

        var trimmed = title.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
