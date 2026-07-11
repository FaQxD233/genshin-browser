using System.IO;
using GenshinBrowser.Constants;

namespace GenshinBrowser.Services;

public static class FileLogger
{
    private static readonly object SyncRoot = new();

    public static void LogException(Exception exception, string context)
    {
        try
        {
            var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GenshinBrowser");
            var logRoot = Path.Combine(dataRoot, "logs");
            Directory.CreateDirectory(logRoot);

            var logPath = Path.Combine(logRoot, $"{DateTime.Now:yyyy-MM-dd}.log");
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {context}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";

            lock (SyncRoot)
            {
                File.AppendAllText(logPath, entry);
            }
        }
        catch
        {
            // Logging must never break browser workflows.
        }
    }

    public static void LogDebug(string message)
    {
        try
        {
            var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GenshinBrowser");
            var logRoot = Path.Combine(dataRoot, "logs");
            Directory.CreateDirectory(logRoot);

            var logPath = Path.Combine(logRoot, $"{DateTime.Now:yyyy-MM-dd}.log");
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [DEBUG] {message}{Environment.NewLine}";

            lock (SyncRoot)
            {
                File.AppendAllText(logPath, entry);
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// 清理超过保留天数的旧日志文件。应在启动时调用一次。
    /// </summary>
    public static void PurgeOldLogs(int retentionDays = AppConfig.Data.LogRetentionDays)
    {
        try
        {
            var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GenshinBrowser");
            var logRoot = Path.Combine(dataRoot, "logs");
            if (!Directory.Exists(logRoot))
            {
                return;
            }

            var cutoff = DateTime.Now.AddDays(-retentionDays);
            foreach (var file in Directory.EnumerateFiles(logRoot, "*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // 单个文件删除失败不影响其他文件
                }
            }
        }
        catch
        {
            // 清理失败不影响启动
        }
    }
}
