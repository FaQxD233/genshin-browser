using System.ComponentModel;
using System.Runtime.CompilerServices;
using GenshinBrowser.Services;

namespace GenshinBrowser.Models;

/// <summary>
/// 单个下载任务的可观察视图模型，供下载面板绑定。
/// </summary>
public sealed class DownloadItem : INotifyPropertyChanged
{
    private long _receivedBytes;
    private long _totalBytes;
    private DownloadState _state = DownloadState.InProgress;
    private string _filePath = string.Empty;
    private bool _canCancel = true;
    private bool _canOpen;

    /// <summary>
    /// 显示用的文件名（来自下载路径或 URI）。
    /// </summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>
    /// 原始下载地址，用于文件名兜底与展示。
    /// </summary>
    public string SourceUri { get; init; } = string.Empty;

    public string FilePath
    {
        get => _filePath;
        set { if (SetProperty(ref _filePath, value)) OnPropertyChanged(nameof(CanOpenInFolder)); }
    }

    public long TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (SetProperty(ref _totalBytes, value))
            {
                OnPropertyChanged(nameof(Progress));
                OnPropertyChanged(nameof(SizeText));
            }
        }
    }

    public long ReceivedBytes
    {
        get => _receivedBytes;
        set
        {
            if (SetProperty(ref _receivedBytes, value))
            {
                OnPropertyChanged(nameof(Progress));
                OnPropertyChanged(nameof(SizeText));
            }
        }
    }

    public DownloadState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(IsRunning));
                CanCancel = value == DownloadState.InProgress;
                CanOpen = value == DownloadState.Completed;
            }
        }
    }

    public double Progress => TotalBytes > 0 ? (double)ReceivedBytes / TotalBytes : 0;

    public bool IsRunning => State == DownloadState.InProgress;

    public string StateText => State switch
    {
        DownloadState.InProgress => LocalizationService.Get("Downloads.State.InProgress", "下载中"),
        DownloadState.Completed => LocalizationService.Get("Downloads.State.Completed", "已完成"),
        DownloadState.Canceled => LocalizationService.Get("Downloads.State.Canceled", "已取消"),
        DownloadState.Interrupted => LocalizationService.Get("Downloads.State.Interrupted", "已中断"),
        _ => string.Empty
    };

    /// <summary>
    /// 语言切换后刷新状态文案绑定。
    /// </summary>
    public void NotifyLanguageChanged()
    {
        OnPropertyChanged(nameof(StateText));
    }

    public string SizeText
    {
        get
        {
            if (TotalBytes <= 0)
            {
                return FormatBytes(ReceivedBytes);
            }

            return $"{FormatBytes(ReceivedBytes)} / {FormatBytes(TotalBytes)}";
        }
    }

    public bool CanCancel
    {
        get => _canCancel;
        private set => SetProperty(ref _canCancel, value);
    }

    public bool CanOpen
    {
        get => _canOpen;
        private set => SetProperty(ref _canOpen, value);
    }

    public bool CanOpenInFolder => !string.IsNullOrEmpty(FilePath) && State != DownloadState.Canceled;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            return "0 B";
        }

        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
        };
    }
}

public enum DownloadState
{
    InProgress,
    Completed,
    Canceled,
    Interrupted
}
