using System.IO;
using GenshinBrowser.Constants;

namespace GenshinBrowser.Services;

public static class FileLogger
{
    private static readonly object SyncRoot = new();

    /// <summary>
    /// 单个日志文件大小上限（字节）。超过时滚动到 .1.log / .2.log，避免单日日志无限膨胀。
    /// </summary>
    private const long MaxLogFileSizeBytes = 5 * 1024 * 1024; // 5 MB

    /// <summary>
    /// 同一日志文件最多保留的滚动份数（.1.log ~ .N.log）。
    /// </summary>
    private const int MaxRolledFiles = 3;

    public static void LogException(Exception exception, string context)
    {
        try
        {
            var logPath = ResolveLogPath();
            if (logPath is null)
            {
                return;
            }

            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {context}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";
            AppendWithRolling(logPath, entry);
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
            var logPath = ResolveLogPath();
            if (logPath is null)
            {
                return;
            }

            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [DEBUG] {message}{Environment.NewLine}";
            AppendWithRolling(logPath, entry);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 清理超过保留天数的旧日志文件（含滚动文件 .N.log）。应在启动时调用一次。
    /// </summary>
    public static void PurgeOldLogs(int retentionDays = AppConfig.Data.LogRetentionDays)
    {
        try
        {
            var logRoot = ResolveLogRoot();
            if (!Directory.Exists(logRoot))
            {
                return;
            }

            var cutoff = DateTime.Now.AddDays(-retentionDays);
            // *.log 同时匹配当前日志（yyyy-MM-dd.log）与滚动文件（yyyy-MM-dd.N.log），
            // 二者都以 .log 为后缀，按最后写入时间统一裁决。
            foreach (var file in Directory.EnumerateFiles(logRoot, "*.log", SearchOption.TopDirectoryOnly))
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

    private static string? ResolveLogPath()
    {
        var logRoot = ResolveLogRoot();
        Directory.CreateDirectory(logRoot);
        return Path.Combine(logRoot, $"{DateTime.Now:yyyy-MM-dd}.log");
    }

    private static string ResolveLogRoot()
    {
        var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GenshinBrowser");
        return Path.Combine(dataRoot, "logs");
    }

    /// <summary>
    /// 在锁内追加日志。若当前文件超过 <see cref="MaxLogFileSizeBytes"/>，先滚动到 .1.log/.2.log/... 再写入新内容。
    /// 滚动失败不影响写入。
    /// </summary>
    private static void AppendWithRolling(string logPath, string entry)
    {
        lock (SyncRoot)
        {
            try
            {
                if (File.Exists(logPath))
                {
                    var info = new FileInfo(logPath);
                    if (info.Length + entry.Length > MaxLogFileSizeBytes)
                    {
                        RollLogs(logPath);
                    }
                }
            }
            catch
            {
                // 滚动失败不阻止本次写入
            }

            File.AppendAllText(logPath, entry);
        }
    }

    /// <summary>
    /// 滚动：当前 .log → .1.log，旧 .1.log → .2.log，依此类推；超过 <see cref="MaxRolledFiles"/> 的最旧文件被删除。
    /// </summary>
    private static void RollLogs(string logPath)
    {
        // 从最旧开始向后删除，避免覆盖时丢失中间文件
        var oldest = Path.ChangeExtension(logPath, $".{MaxRolledFiles}.log");
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var i = MaxRolledFiles - 1; i >= 1; i--)
        {
            var src = Path.ChangeExtension(logPath, $".{i}.log");
            var dst = Path.ChangeExtension(logPath, $".{i + 1}.log");
            if (File.Exists(src))
            {
                File.Move(src, dst, overwrite: true);
            }
        }

        // 当前 .log → .1.log
        File.Move(logPath, Path.ChangeExtension(logPath, ".1.log"), overwrite: true);
    }
}
