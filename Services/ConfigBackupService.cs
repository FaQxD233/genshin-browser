using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using GenshinBrowser.Constants;
using GenshinBrowser.Models;

namespace GenshinBrowser.Services;

/// <summary>导入配置失败的原因，对应控制窗提示文案。</summary>
public enum ConfigImportError
{
    NotConfigFile,
    NewerVersion,
    Corrupted,
    TooLarge,
}

public sealed class ConfigImportException : Exception
{
    public ConfigImportException(ConfigImportError error, Exception? innerException = null)
        : base(error.ToString(), innerException)
    {
        Error = error;
    }

    public ConfigImportError Error { get; }
}

/// <summary>
/// 配置导入 / 导出（单个 JSON）：设置、收藏、浏览记录。登录状态、Cookie、站点数据和下载记录不在内。
/// 读入的文件是外部输入：先查大小、格式标记与版本，再把每一段过各自的清洗。
/// </summary>
internal static class ConfigBackupService
{
    /// <summary>导入前自动备份的位置（数据目录下），每次导入覆盖；导入不满意可再导入它撤回。</summary>
    public const string BackupFolderName = "backups";

    public const string PreImportBackupFileName = "before-import.json";

    // 缩进 + 不转义中文：导出文件可以直接用记事本查看
    private static readonly JsonSerializerOptions FileOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public static string SuggestFileName(DateTime localNow) =>
        $"GenshinBrowser-config-{localNow.ToString("yyyyMMdd-HHmm", System.Globalization.CultureInfo.InvariantCulture)}.json";

    /// <summary>
    /// 生成导出内容。窗口模式、首次浮窗引导、缓存检查时间属于本机运行状态，清成默认值；导入时也不使用。
    /// </summary>
    public static ConfigBundle Create(
        AppSettings settings,
        IReadOnlyList<FavoriteEntry> favorites,
        IReadOnlyList<HistoryEntry> history,
        DateTime exportedAtUtc)
    {
        var snapshot = SettingsService.CreateSnapshot(settings);
        snapshot.WindowMode = WindowMode.Free;
        snapshot.HasSeenFloatingModeHint = false;
        snapshot.LastWebView2CacheCheckUtc = DateTime.MinValue;

        return new ConfigBundle
        {
            Format = ConfigBundle.FormatName,
            Version = ConfigBundle.CurrentVersion,
            ExportedAt = exportedAtUtc,
            Settings = snapshot,
            Favorites = favorites.Select(e => new FavoriteEntry { Url = e.Url, Title = e.Title, SavedAt = e.SavedAt }).ToList(),
            History = history.Select(e => new HistoryEntry { Url = e.Url, Title = e.Title, VisitedAt = e.VisitedAt }).ToList(),
        };
    }

    public static Task WriteAsync(string path, ConfigBundle bundle) =>
        JsonFileWriter.WriteAtomicAsync(path, bundle, FileOptions);

    /// <summary>读取并校验配置文件；不可用时抛 <see cref="ConfigImportException"/>，读盘本身的 IO 异常原样抛出。</summary>
    public static async Task<ConfigBundle> ReadAsync(string path, int maxBytes = AppConfig.Data.MaxConfigFileSizeBytes)
    {
        string json;
        try
        {
            json = await JsonFileWriter.ReadAllTextBoundedAsync(path, maxBytes).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            throw new ConfigImportException(ConfigImportError.TooLarge, ex);
        }

        return Parse(json);
    }

    /// <summary>
    /// 解析并校验：格式标记不对、版本比本程序新、JSON 损坏都拒绝；通过后设置、收藏、浏览记录各过一遍清洗
    /// （越界值拉回范围、冲突热键让位、网址归一去重），缺的段保持 null。
    /// </summary>
    public static ConfigBundle Parse(string json)
    {
        ConfigBundle? bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<ConfigBundle>(json, FileOptions);
        }
        catch (JsonException ex)
        {
            throw new ConfigImportException(ConfigImportError.Corrupted, ex);
        }

        if (bundle is null ||
            !string.Equals(bundle.Format, ConfigBundle.FormatName, StringComparison.Ordinal) ||
            bundle.Version < 1)
        {
            throw new ConfigImportException(ConfigImportError.NotConfigFile);
        }

        if (bundle.Version > ConfigBundle.CurrentVersion)
        {
            throw new ConfigImportException(ConfigImportError.NewerVersion);
        }

        bundle.Settings = bundle.Settings is null ? null : SettingsService.Normalize(bundle.Settings);
        bundle.Favorites = bundle.Favorites is null ? null : FavoritesService.SanitizeEntries(bundle.Favorites);
        bundle.History = bundle.History is null ? null : HistoryService.SanitizeEntries(bundle.History);
        return bundle;
    }
}
