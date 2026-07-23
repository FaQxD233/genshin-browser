using GenshinBrowser.Utils;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace GenshinBrowser.Models;

public sealed class DownloadItem : INotifyPropertyChanged
{
    private string fileName = string.Empty;
    private string sourceUri = string.Empty;
    private string filePath = string.Empty;
    private long totalBytes;
    private long receivedBytes;
    private DownloadState state = DownloadState.InProgress;

    public string FileName
    {
        get => fileName;
        set => SetProperty(ref fileName, value ?? string.Empty);
    }

    public string SourceUri
    {
        get => sourceUri;
        set
        {
            if (SetProperty(ref sourceUri, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanRetry));
            }
        }
    }

    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    public string FilePath
    {
        get => filePath;
        set
        {
            if (SetProperty(ref filePath, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanOpen));
                OnPropertyChanged(nameof(CanOpenInFolder));
            }
        }
    }

    public long TotalBytes
    {
        get => totalBytes;
        set
        {
            if (SetProperty(ref totalBytes, value))
            {
                OnPropertyChanged(nameof(Progress));
                OnPropertyChanged(nameof(SizeText));
            }
        }
    }

    public long ReceivedBytes
    {
        get => receivedBytes;
        set
        {
            if (SetProperty(ref receivedBytes, value))
            {
                OnPropertyChanged(nameof(Progress));
                OnPropertyChanged(nameof(SizeText));
            }
        }
    }

    public DownloadState State
    {
        get => state;
        set
        {
            if (SetProperty(ref state, value))
            {
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(CanRetry));
                OnPropertyChanged(nameof(CanOpen));
                OnPropertyChanged(nameof(CanOpenInFolder));
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    public double Progress => TotalBytes > 0 ? (double)ReceivedBytes / TotalBytes : 0;

    public bool IsRunning => State == DownloadState.InProgress;

    public bool CanCancel => State == DownloadState.InProgress;

    public bool CanOpen => State == DownloadState.Completed && File.Exists(FilePath);

    public bool CanOpenInFolder
    {
        get
        {
            string? directory = Path.GetDirectoryName(FilePath);
            return State != DownloadState.Canceled &&
                   !string.IsNullOrEmpty(directory) &&
                   Directory.Exists(directory);
        }
    }

    public bool CanRetry =>
        State is DownloadState.Canceled or DownloadState.Interrupted &&
        EntryText.TryValidateHttpUrl(SourceUri, out _);

    public string StateText => State switch
    {
        DownloadState.InProgress => "下载中",
        DownloadState.Completed => "已完成",
        DownloadState.Canceled => "已取消",
        DownloadState.Interrupted => "已中断",
        _ => string.Empty,
    };

    public string SizeText => TotalBytes <= 0
        ? FormatBytes(ReceivedBytes)
        : $"{FormatBytes(ReceivedBytes)} / {FormatBytes(TotalBytes)}";

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
        OnPropertyChanged(propertyName);
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
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
        };
    }
}

public enum DownloadState
{
    InProgress,
    Completed,
    Canceled,
    Interrupted,
}
