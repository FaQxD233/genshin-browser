using System.IO;
using Microsoft.Win32;

namespace GenshinBrowser.Services;

/// <summary>
/// 数据目录（设置、收藏、浏览记录、下载记录、日志、浏览器数据）。启动时解析一次，优先级：
/// 环境变量 <see cref="EnvironmentOverride"/>（测试用）→ 注册表 HKCU\Software\GenshinBrowser 的 DataDirectory
/// → 默认 %LocalAppData%\GenshinBrowser。设置的位置不可用（不存在的盘、不能写）时本次退回默认位置，
/// 设置本身保留，下次可用时照常使用。不放在数据目录里，也不依赖 AppData 里的文件或链接。
/// </summary>
public static class AppPaths
{
    public const string EnvironmentOverride = "GENSHINBROWSER_DATA_DIR";

    private const string RegistryKeyPath = @"Software\GenshinBrowser";
    private const string DataDirectoryValue = "DataDirectory";
    private const string PendingMoveFromValue = "PendingMoveFrom";
    private const string SettingsFileName = "settings.json";

    private static readonly object InitLock = new();
    private static string? _dataRoot;

    public static string DefaultDataRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GenshinBrowser");

    /// <summary>本次使用的数据根目录；首次访问时解析（启动时 App 会先解析并处理待搬运的数据）。</summary>
    public static string DataRoot
    {
        get
        {
            lock (InitLock)
            {
                return _dataRoot ??= ResolveStartupRoot();
            }
        }
    }

    public static string WebViewProfile => Path.Combine(DataRoot, "WebViewProfile");

    public static string Logs => Path.Combine(DataRoot, "logs");

    /// <summary>设置了但本次不可用、退回默认位置的目录；主窗启动后提示。</summary>
    public static string? UnavailableConfiguredDirectory { get; private set; }

    /// <summary>本次启动刚把数据从这里搬到新位置（旧数据原样保留），主窗启动后提示。</summary>
    public static string? MovedFromDirectory { get; private set; }

    /// <summary>本次启动搬运数据失败的原因（已退回原来的位置），主窗启动后提示。</summary>
    public static string? MoveFailure { get; private set; }

    public static bool IsDefault(string path) => SamePath(path, DefaultDataRoot);

    /// <summary>目录里已有本程序的数据（有 settings.json）。</summary>
    public static bool HasAppData(string path) => File.Exists(Path.Combine(path, SettingsFileName));

    /// <summary>能用作数据目录：完整路径，可以创建，可以写入。</summary>
    public static bool IsUsable(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                return false;
            }

            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>两个目录相同，或一个在另一个里面（搬运时会复制进自己）。</summary>
    public static bool AreNested(string first, string second)
    {
        var a = Normalize(first);
        var b = Normalize(second);
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
               a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static bool SamePath(string first, string second) =>
        Normalize(first).Equals(Normalize(second), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 记下新的数据目录（下次启动生效）。<paramref name="moveFrom"/> 非空时下次启动先把它的数据复制过去。
    /// 选回默认位置时删掉注册表里的设置。
    /// </summary>
    public static void SaveDataDirectory(string newRoot, string? moveFrom)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
        if (IsDefault(newRoot))
        {
            key.DeleteValue(DataDirectoryValue, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(DataDirectoryValue, Normalize(newRoot));
        }

        if (moveFrom is null)
        {
            key.DeleteValue(PendingMoveFromValue, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(PendingMoveFromValue, Normalize(moveFrom));
        }
    }

    /// <summary>
    /// 启动时处理待搬运的数据：把旧目录复制到本次的数据目录，成功后清掉标记；失败时本次与之后都退回旧目录。
    /// 复制本身交给 <paramref name="copy"/>（App 用它显示进度），必须在任何数据文件被打开之前调用。
    /// </summary>
    public static void RunPendingMove(Action<string, string> copy)
    {
        string? moveFrom;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
            moveFrom = key?.GetValue(PendingMoveFromValue) as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            FileLogger.LogException(ex, "Read pending data directory move");
            return;
        }

        if (string.IsNullOrWhiteSpace(moveFrom))
        {
            return;
        }

        var target = DataRoot;
        if (UnavailableConfiguredDirectory is not null)
        {
            // 新位置这次不可用（例如盘没接上）：标记留着，下次可用时再搬
            return;
        }

        if (AreNested(moveFrom, target) || !Directory.Exists(moveFrom))
        {
            // 与新位置嵌套或旧数据已不在：没有可搬的，清掉标记
            ClearPendingMove();
            return;
        }

        try
        {
            // 标记在复制成功后才清：中途崩溃时下次启动会重新复制（同名覆盖）
            copy(moveFrom, target);
            MovedFromDirectory = moveFrom;
            ClearPendingMove();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLogger.LogException(ex, "Move data directory");
            MoveFailure = ex.Message;
            lock (InitLock)
            {
                _dataRoot = moveFrom;
            }

            // 之后也继续用旧位置：新位置里可能只复制了一部分
            TrySaveDataDirectory(moveFrom, moveFrom: null);
        }
    }

    private static void ClearPendingMove()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true);
            key?.DeleteValue(PendingMoveFromValue, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            FileLogger.LogException(ex, "Clear pending data directory move");
        }
    }

    private static void TrySaveDataDirectory(string root, string? moveFrom)
    {
        try
        {
            SaveDataDirectory(root, moveFrom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            FileLogger.LogException(ex, "Save data directory");
        }
    }

    /// <summary>
    /// 解析顺序（纯函数，便于测试）：环境变量 → 注册表 → 默认；前两者给了但不可用时退回默认，并返回不可用的那个。
    /// </summary>
    internal static (string Root, string? Unavailable) Resolve(
        string? environmentValue,
        string? registryValue,
        string defaultRoot,
        Func<string, bool> isUsable)
    {
        foreach (var candidate in new[] { environmentValue, registryValue })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var path = TryNormalize(candidate);
            return path is not null && isUsable(path) ? (path, null) : (defaultRoot, candidate.Trim());
        }

        return (defaultRoot, null);
    }

    private static string ResolveStartupRoot()
    {
        string? registryValue = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
            registryValue = key?.GetValue(DataDirectoryValue) as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 读不到设置时按默认位置启动
        }

        var (root, unavailable) = Resolve(
            Environment.GetEnvironmentVariable(EnvironmentOverride), registryValue, DefaultDataRoot, IsUsable);
        UnavailableConfiguredDirectory = unavailable;
        return root;
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    private static string? TryNormalize(string path)
    {
        try
        {
            return Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
