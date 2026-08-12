using System.IO;
using System.Threading.Channels;
using GenshinBrowser.Constants;

namespace GenshinBrowser.Services;

public static class FileLogger
{
    /// <summary>
    /// 单个日志文件大小上限（字节）。超过时滚动到 .1.log / .2.log，避免单日日志无限膨胀。
    /// </summary>
    private const long MaxLogFileSizeBytes = 5 * 1024 * 1024; // 5 MB

    /// <summary>
    /// 同一日志文件最多保留的滚动份数（.1.log ~ .N.log）。
    /// </summary>
    private const int MaxRolledFiles = 3;

    /// <summary>
    /// 无界写盘队列。SingleReader=true：所有磁盘 IO（含目录创建与日志滚动）只在单个后台线程
    /// 串行执行；调用线程（含 UI 线程）仅做字符串格式化与入队，不再同步阻塞写盘。
    /// </summary>
    private static readonly Channel<(string LogPath, string Entry)> Queue =
        Channel.CreateUnbounded<(string, string)>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>已入队但尚未写完的条目数，供 <see cref="FlushAsync"/> 等待排空。</summary>
    private static int _pendingCount;

    private static readonly Task WriterTask = Task.Run(WriterLoopAsync);

    public static void LogException(Exception exception, string context)
    {
        try
        {
            var logPath = ResolveLogPath();
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {context}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";
            Enqueue(logPath, entry);
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
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [DEBUG] {message}{Environment.NewLine}";
            Enqueue(logPath, entry);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 等待所有已入队日志写完。关闭流程应在退出前调用，避免进程结束丢日志。
    /// 带超时保护：后台写盘线程意外退出时不会无限等待。
    /// </summary>
    public static async Task FlushAsync(int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Volatile.Read(ref _pendingCount) > 0)
        {
            if (Environment.TickCount64 >= deadline)
            {
                return;
            }

            await Task.Delay(10).ConfigureAwait(false);
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

    private static void Enqueue(string logPath, string entry)
    {
        Interlocked.Increment(ref _pendingCount);
        if (!Queue.Writer.TryWrite((logPath, entry)))
        {
            // 队列已 Complete（正常流程不会发生）；计数回滚避免 FlushAsync 永久等待。
            Interlocked.Decrement(ref _pendingCount);
        }
    }

    private static async Task WriterLoopAsync()
    {
        try
        {
            while (await Queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (Queue.Reader.TryRead(out var item))
                {
                    try
                    {
                        WriteEntry(item.LogPath, item.Entry);
                    }
                    catch
                    {
                        // 单条日志写盘失败不影响后续日志。
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _pendingCount);
                    }
                }
            }
        }
        catch
        {
            // 后台写盘循环意外退出；后续日志会堆积在无界队列中但不会崩溃应用。
        }
    }

    /// <summary>
    /// 后台线程执行：创建目录 + 追加写入（含滚动）。仅在 <see cref="WriterLoopAsync"/> 单线程调用。
    /// </summary>
    private static void WriteEntry(string logPath, string entry)
    {
        var directory = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        AppendWithRolling(logPath, entry);
    }

    private static string ResolveLogPath()
    {
        var logRoot = ResolveLogRoot();
        return Path.Combine(logRoot, $"{DateTime.Now:yyyy-MM-dd}.log");
    }

    private static string ResolveLogRoot()
    {
        var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GenshinBrowser");
        return Path.Combine(dataRoot, "logs");
    }

    /// <summary>
    /// 追加日志。若当前文件超过 <see cref="MaxLogFileSizeBytes"/>，先滚动到 .1.log/.2.log/... 再写入新内容。
    /// 滚动失败不影响写入。仅在后台写盘线程串行调用，无需加锁。
    /// </summary>
    private static void AppendWithRolling(string logPath, string entry)
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
