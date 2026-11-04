using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using GenshinBrowser.Constants;
using GenshinBrowser.Models;
using GenshinBrowser.Utils;
using Microsoft.Web.WebView2.Core;

namespace GenshinBrowser.Services;

/// <summary>
/// 维护下载任务列表并提供打开文件 / 打开所在文件夹 / 取消下载能力。
/// 下载进度由 WebView2 的 <see cref="Microsoft.Web.WebView2.Core.CoreWebView2DownloadOperation"/>
/// 事件驱动，本服务仅持有引用并更新可观察模型。
/// </summary>
public sealed class DownloadsService : IDisposable
{
    private readonly object _gate = new();
    private readonly object _persistLock = new();
    private readonly Dictionary<DownloadItem, CoreWebView2DownloadOperation> _operations = new();
    private readonly string? _downloadsPath;
    // 没给路径（单测）时为 null，不落盘
    private readonly DebouncedJsonSaver<List<PersistedDownloadEntry>>? _saver;

    public DownloadsService(string? downloadsPath = null)
    {
        _downloadsPath = downloadsPath;
        if (downloadsPath is not null)
        {
            _saver = new DebouncedJsonSaver<List<PersistedDownloadEntry>>(
                downloadsPath,
                JsonFileWriter.CompactOptions,
                AppConfig.Data.DownloadSaveDebounceMs,
                "Save downloads",
                _persistLock,
                CreatePersistSnapshot);
        }
    }

    /// <summary>启动加载时读不出来的情况（原文件已改名保留，或改名失败、本次不写盘）；正常为 null。</summary>
    public DataFileProblem? LoadProblem { get; private set; }

    /// <summary>
    /// 异步从磁盘加载下载记录。构造函数不再同步读盘，避免阻塞首帧。
    /// 必须从 UI 线程调用：读盘与反序列化在线程池执行，回到调用方（UI）线程后才填充 <see cref="Downloads"/>（ObservableCollection）。
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_downloadsPath is null || _saver is null)
        {
            return;
        }

        var (loaded, problem) = await DataFile.ReadAsync<List<PersistedDownloadEntry?>>(
            _downloadsPath, AppConfig.Data.MaxDownloadsFileSizeBytes, options: null, "Load downloads").ConfigureAwait(true);
        LoadProblem = problem;
        if (problem is { PreservedPath: null })
        {
            _saver.BlockWrites();
        }

        if (loaded is null)
        {
            return;
        }

        var sanitized = SanitizeEntries(loaded);
        // 加载期间 Track 的新任务已插在最前，磁盘里的旧记录接在后面
        foreach (var item in sanitized)
        {
            Downloads.Add(item);
        }

        // 磁盘内容需要清洗（如上次退出时还在下载的记为中断）时写回一次
        if (!EntriesMatch(loaded, sanitized))
        {
            QueuePersist();
        }
    }

    public ObservableCollection<DownloadItem> Downloads { get; } = new();

    public void Track(DownloadItem item, CoreWebView2DownloadOperation operation)
    {
        lock (_gate)
        {
            _operations[item] = operation;
        }

        Downloads.Insert(0, item);
        TrimFinishedExcess();
        QueuePersist();
    }

    public void Restart(
        DownloadItem item,
        CoreWebView2DownloadOperation operation,
        string sourceUri,
        string filePath,
        long totalBytes,
        long receivedBytes)
    {
        lock (_gate)
        {
            _operations[item] = operation;
        }

        item.SourceUri = sourceUri;
        item.FilePath = filePath;
        var fileName = Path.GetFileName(filePath);
        if (!string.IsNullOrEmpty(fileName))
        {
            item.FileName = fileName;
        }
        item.TotalBytes = Math.Max(0, totalBytes);
        item.ReceivedBytes = Math.Max(0, receivedBytes);
        item.StartedAtUtc = DateTime.UtcNow;
        item.State = DownloadState.InProgress;

        var index = Downloads.IndexOf(item);
        if (index > 0)
        {
            Downloads.Move(index, 0);
        }

        QueuePersist();
    }

    /// <summary>
    /// 下载列表超过上限时，从尾部移除最旧的已完成/取消/中断项，
    /// 避免长期累积占用内存。进行中的任务不会被自动移除。
    /// </summary>
    private void TrimFinishedExcess()
    {
        var max = AppConfig.Data.MaxDownloadItems;
        for (var i = Downloads.Count - 1; i >= 0 && Downloads.Count > max; i--)
        {
            var state = Downloads[i].State;
            if (state is DownloadState.Completed or DownloadState.Canceled or DownloadState.Interrupted)
            {
                Downloads.RemoveAt(i);
            }
        }
    }

    public bool TryCancel(DownloadItem item)
    {
        CoreWebView2DownloadOperation? operation;
        lock (_gate)
        {
            if (!_operations.TryGetValue(item, out operation))
            {
                return false;
            }
        }

        try
        {
            operation.Cancel();
            item.State = DownloadState.Canceled;
            lock (_gate)
            {
                _operations.Remove(item);
            }

            QueuePersist();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void MarkCompleted(DownloadItem item)
    {
        lock (_gate)
        {
            _operations.Remove(item);
        }

        item.State = DownloadState.Completed;
        TrimFinishedExcess();
        QueuePersist();
    }

    public void MarkInterrupted(DownloadItem item)
    {
        lock (_gate)
        {
            _operations.Remove(item);
        }

        item.State = DownloadState.Interrupted;
        TrimFinishedExcess();
        QueuePersist();
    }

    public void MarkCanceled(DownloadItem item)
    {
        lock (_gate)
        {
            _operations.Remove(item);
        }

        item.State = DownloadState.Canceled;
        TrimFinishedExcess();
        QueuePersist();
    }

    /// <summary>
    /// 直接 shell 执行风险过高的扩展名：UseShellExecute 打开这些文件等于运行程序 / 脚本、导入注册表或安装应用。
    /// FilePath 持久化自磁盘上的 downloads.json，被篡改时可诱导「打开文件」按钮执行任意代码，
    /// 因此这些类型不直接执行，改为在资源管理器中定位文件由用户手动决定。
    /// </summary>
    private static readonly HashSet<string> DirectExecuteRiskyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msp", ".bat", ".cmd", ".com", ".scr", ".pif", ".hta", ".cpl", ".msc", ".jar",
        ".lnk", ".url", ".scf", ".vbs", ".vbe", ".js", ".jse", ".ws", ".wsf", ".wsh", ".ps1", ".psm1", ".msh",
        ".reg", ".chm", ".application", ".appinstaller", ".appx", ".appxbundle", ".msix", ".msixbundle",
    };

    public bool OpenFile(DownloadItem item)
    {
        if (string.IsNullOrEmpty(item.FilePath) || !File.Exists(item.FilePath))
        {
            return false;
        }

        var extension = Path.GetExtension(item.FilePath);
        if (DirectExecuteRiskyExtensions.Contains(extension))
        {
            // 高危类型不 shell 执行：资源管理器里选中该文件，交给用户判断；
            // /select 参数形式 explorer 解析失败时退回仅打开所在文件夹。
            return RevealInExplorer(item.FilePath) || OpenFolder(item);
        }

        // 本程序以管理员身份运行：经资源管理器以普通权限打开；打不开时退回在文件夹中显示，不提权打开
        return ShellLauncher.TryOpen(item.FilePath, Path.GetDirectoryName(item.FilePath)) ||
               RevealInExplorer(item.FilePath);
    }

    /// <summary>在资源管理器中打开并选中指定文件。路径以独立参数传递，不经 shell 解析。</summary>
    private static bool RevealInExplorer(string filePath)
    {
        try
        {
            var startInfo = new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
            };
            startInfo.ArgumentList.Add("/select,");
            startInfo.ArgumentList.Add(filePath);
            Process.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool OpenFolder(DownloadItem item)
    {
        if (string.IsNullOrEmpty(item.FilePath))
        {
            return false;
        }

        var dir = Path.GetDirectoryName(item.FilePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
            };
            startInfo.ArgumentList.Add(dir);
            Process.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void ClearFinished()
    {
        // 倒序 RemoveAt 避免 Where().ToList() + 正序 Remove 的 O(n²)：
        // ObservableCollection.Remove(item) 内部是 O(n) 线性查找 + 移位，
        // 循环执行整体 O(n²)；倒序 RemoveAt 直接按索引删除，整体 O(n)。
        var removed = 0;
        for (var i = Downloads.Count - 1; i >= 0; i--)
        {
            var state = Downloads[i].State;
            if (state is DownloadState.Completed or DownloadState.Canceled or DownloadState.Interrupted)
            {
                Downloads.RemoveAt(i);
                removed++;
            }
        }

        if (removed > 0)
        {
            QueuePersist();
        }
    }

    /// <summary>立即写入尚未落盘的改动。关闭前在 UI 线程调用（快照要读下载列表）；Dispose 不再写盘。</summary>
    public Task FlushAsync() => _saver?.FlushAsync() ?? Task.CompletedTask;

    public void Dispose() => _saver?.Dispose();

    private void QueuePersist() => _saver?.MarkChanged();

    private List<PersistedDownloadEntry> CreatePersistSnapshot()
    {
        return Downloads.Take(AppConfig.Data.MaxDownloadItems)
            .Select(item => new PersistedDownloadEntry
            {
                FileName = item.FileName,
                SourceUri = item.SourceUri,
                FilePath = item.FilePath,
                TotalBytes = item.TotalBytes,
                ReceivedBytes = item.ReceivedBytes,
                State = item.State,
                StartedAtUtc = item.StartedAtUtc,
            })
            .ToList();
    }

    private static bool EntriesMatch(
        IReadOnlyList<PersistedDownloadEntry?> loaded,
        IReadOnlyList<DownloadItem> sanitized)
    {
        if (loaded.Count != sanitized.Count)
        {
            return false;
        }

        for (var i = 0; i < sanitized.Count; i++)
        {
            var source = loaded[i];
            var target = sanitized[i];
            if (source is null ||
                !string.Equals(source.FileName, target.FileName, StringComparison.Ordinal) ||
                !string.Equals(source.SourceUri, target.SourceUri, StringComparison.Ordinal) ||
                !string.Equals(source.FilePath, target.FilePath, StringComparison.Ordinal) ||
                source.TotalBytes != target.TotalBytes ||
                source.ReceivedBytes != target.ReceivedBytes ||
                source.State != target.State ||
                source.StartedAtUtc != target.StartedAtUtc)
            {
                return false;
            }
        }

        return true;
    }

    private static List<DownloadItem> SanitizeEntries(IEnumerable<PersistedDownloadEntry?> loaded)
    {
        var result = new List<DownloadItem>(AppConfig.Data.MaxDownloadItems);
        var fallbackTime = DateTime.UtcNow;
        foreach (var entry in loaded)
        {
            if (entry is null)
            {
                continue;
            }

            var filePath = (entry.FilePath ?? string.Empty).Trim();
            if (filePath.Length > 32_767)
            {
                filePath = string.Empty;
            }

            var sourceUri = EntryText.TryValidateHttpUrl(entry.SourceUri, out var validSourceUri)
                ? validSourceUri
                : string.Empty;
            var fileName = EntryText.TruncateTitle(entry.FileName);
            if (fileName.Length == 0)
            {
                fileName = !string.IsNullOrEmpty(filePath)
                    ? Path.GetFileName(filePath)
                    : "download";
            }

            var state = Enum.IsDefined(entry.State) ? entry.State : DownloadState.Interrupted;
            if (state == DownloadState.InProgress)
            {
                state = DownloadState.Interrupted;
            }

            var receivedBytes = Math.Max(0, entry.ReceivedBytes);
            var totalBytes = Math.Max(0, entry.TotalBytes);
            if (totalBytes > 0)
            {
                receivedBytes = Math.Min(receivedBytes, totalBytes);
            }

            result.Add(new DownloadItem
            {
                FileName = fileName,
                SourceUri = sourceUri,
                FilePath = filePath,
                TotalBytes = totalBytes,
                ReceivedBytes = receivedBytes,
                State = state,
                StartedAtUtc = EntryText.NormalizeUtcTimestamp(entry.StartedAtUtc, fallbackTime),
            });

            if (result.Count == AppConfig.Data.MaxDownloadItems)
            {
                break;
            }
        }

        return result;
    }

    private sealed class PersistedDownloadEntry
    {
        public string FileName { get; set; } = string.Empty;

        public string SourceUri { get; set; } = string.Empty;

        public string FilePath { get; set; } = string.Empty;

        public long TotalBytes { get; set; }

        public long ReceivedBytes { get; set; }

        public DownloadState State { get; set; }

        public DateTime StartedAtUtc { get; set; }
    }
}
