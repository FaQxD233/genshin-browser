using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using GenshinBrowser.Models;

namespace GenshinBrowser.Services;

/// <summary>
/// 维护下载任务列表并提供打开文件 / 打开所在文件夹 / 取消下载能力。
/// 下载进度由 WebView2 的 <see cref="Microsoft.Web.WebView2.Core.CoreWebView2DownloadOperation"/>
/// 事件驱动，本服务仅持有引用并更新可观察模型。
/// </summary>
public sealed class DownloadsService
{
    private readonly object _gate = new();
    private readonly Dictionary<DownloadItem, object> _operations = new();

    public ObservableCollection<DownloadItem> Downloads { get; } = new();

    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _operations.Count;
            }
        }
    }

    public void Track(DownloadItem item, object operation)
    {
        lock (_gate)
        {
            _operations[item] = operation;
        }

        Downloads.Insert(0, item);
    }

    public bool TryCancel(DownloadItem item)
    {
        object? op;
        lock (_gate)
        {
            if (!_operations.TryGetValue(item, out op))
            {
                return false;
            }
        }

        try
        {
            // CoreWebView2DownloadOperation.Cancel() — 通过反射避免在服务层硬依赖 WebView2 类型
            op?.GetType().GetMethod("Cancel")?.Invoke(op, null);
            item.State = DownloadState.Canceled;
            lock (_gate)
            {
                _operations.Remove(item);
            }

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
    }

    public void MarkInterrupted(DownloadItem item)
    {
        lock (_gate)
        {
            _operations.Remove(item);
        }

        item.State = DownloadState.Interrupted;
    }

    public bool OpenFile(DownloadItem item)
    {
        if (string.IsNullOrEmpty(item.FilePath) || !File.Exists(item.FilePath))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true });
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
            Process.Start(new ProcessStartInfo("explorer.exe", $"{dir}"));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void ClearFinished()
    {
        var toRemove = Downloads.Where(d => d.State is DownloadState.Completed or DownloadState.Canceled or DownloadState.Interrupted).ToList();
        foreach (var item in toRemove)
        {
            Downloads.Remove(item);
        }
    }
}
