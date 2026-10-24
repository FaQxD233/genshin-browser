namespace GenshinBrowser.Models;

/// <summary>
/// 配置导入 / 导出文件（单个 JSON）：设置、收藏、浏览记录。缺的段导入时跳过。
/// Format / Version 不给默认值：别的 JSON 反序列化进来时保持空，才能被识别成「不是配置文件」。
/// </summary>
public sealed class ConfigBundle
{
    public const string FormatName = "GenshinBrowser.Config";

    public const int CurrentVersion = 1;

    public string? Format { get; set; }

    public int Version { get; set; }

    public DateTime ExportedAt { get; set; }

    public AppSettings? Settings { get; set; }

    public List<FavoriteEntry>? Favorites { get; set; }

    public List<HistoryEntry>? History { get; set; }
}
