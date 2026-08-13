using System.IO;
using System.Text;
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
    /// 有界写盘队列（容量 1000）。SingleReader=true：所有磁盘 IO（含目录创建与日志滚动）只在单个后台线程
    /// 串行执行；队列满时 DropWrite 静默丢弃新日志，绝不阻塞 UI 线程或造成 OOM。
    /// </summary>
    private static readonly Channel<(string LogPath, string Entry)> Queue =
        Channel.CreateBounded<(string, string)>(new BoundedChannelOptions(1000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });

    /// <summary>
    /// 当前正在写盘的条目数（0 或 1）。在 TryRead 之前递增、WriteEntry 完成后递减。
    /// FlushAsync 必须同时等待 Queue.Reader.Count == 0 与 _activeWriteCount == 0，
    /// 否则会在最后一条日志写完前返回（Count 在 TryRead 时即减，早于写盘完成）。
    /// </summary>
    private static int _activeWriteCount;

    /// <summary>后台写盘线程是否已异常终止。</summary>
    private static volatile bool _writerFaulted;

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
    /// 等待队列中所有日志写完（包括正在写盘的最后一条）。关闭流程应在退出前调用，避免进程结束丢日志。
    /// 带超时保护：后台写盘线程意外退出时不会无限等待。
    /// </summary>
    public static async Task FlushAsync(int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        // 双条件：队列空 AND 当前没有正在写盘的条目。
        // 仅看 Count 不够：TryRead 时 Count 即减，但 WriteEntry 可能尚未完成。
        while (Queue.Reader.Count > 0 || Volatile.Read(ref _activeWriteCount) > 0)
        {
            if (_writerFaulted || Environment.TickCount64 >= deadline)
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
        // DropWrite 模式下 TryWrite 永远不阻塞：队列满时返回 true 但条目被静默丢弃。
        // 不维护"已入队"计数器（DropWrite 无法可靠回滚），FlushAsync 改用 Count + _activeWriteCount 双条件。
        Queue.Writer.TryWrite((logPath, entry));
    }

    private static async Task WriterLoopAsync()
    {
        try
        {
            while (await Queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (true)
                {
                    // 先标记"正在写"，再 TryRead。若 TryRead 失败则回滚标记并退出内层循环。
                    // 这样 FlushAsync 的 `_activeWriteCount > 0` 检查不会漏掉"刚 TryRead 成功、WriteEntry 尚未开始"的窗口。
                    Interlocked.Increment(ref _activeWriteCount);
                    if (!Queue.Reader.TryRead(out var item))
                    {
                        Interlocked.Decrement(ref _activeWriteCount);
                        break;
                    }

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
                        Interlocked.Decrement(ref _activeWriteCount);
                    }
                }
            }
        }
        catch
        {
            // 后台写盘循环意外退出；标记 fault 避免 FlushAsync 盲目等待
            _writerFaulted = true;
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
        if (LogRootOverride is { } overrideRoot)
        {
            return overrideRoot;
        }

        var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GenshinBrowser");
        return Path.Combine(dataRoot, "logs");
    }

    /// <summary>测试专用：覆盖日志目录。生产代码不应设置。</summary>
    internal static string? LogRootOverride;

    /// <summary>
    /// 追加日志。若当前文件超过 <see cref="MaxLogFileSizeBytes"/>，先滚动到 .1.log/.2.log/... 再写入新内容。
    /// 滚动失败不影响写入。仅在后台写盘线程串行调用，无需加锁。
    /// </summary>
    private static void AppendWithRolling(string logPath, string entry)
    {
        // entry.Length 是 UTF-16 字符数，而文件以 UTF-8 写入；中文字符 UTF-8 占 3 字节。
        // 用 UTF-8 字节数比较，避免阈值被低估。
        var entryByteCount = Encoding.UTF8.GetByteCount(entry);
        try
        {
            if (File.Exists(logPath))
            {
                var info = new FileInfo(logPath);
                if (info.Length + entryByteCount > MaxLogFileSizeBytes)
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
