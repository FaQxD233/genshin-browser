using GenshinBrowser.Models;
using GenshinBrowser.Utils;

namespace GenshinBrowser.ViewModels;

public sealed class ControlItemViewModel : ViewModelBase, IEquatable<ControlItemViewModel>
{
    private string _url = string.Empty;
    private string _title = string.Empty;
    private string _timeDisplay = string.Empty;
    // 缓存上次格式化时间，避免未变化时重复 FormatRelativeTime（资源查找 + 字符串分配）
    private DateTime _lastFormattedUtc;

    public ControlItemViewModel(HistoryEntry item)
    {
        Update(item);
    }

    public ControlItemViewModel(FavoriteEntry item)
    {
        Update(item);
    }

    public string Url
    {
        get => _url;
        private set => SetProperty(ref _url, value);
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string TimeDisplay
    {
        get => _timeDisplay;
        private set => SetProperty(ref _timeDisplay, value);
    }

    public void Update(HistoryEntry item)
    {
        Url = item.Url;
        Title = item.Title;
        // 仅在时间戳变化时才重新格式化；同一条历史被反复刷新时跳过资源查找与字符串分配
        if (_lastFormattedUtc != item.VisitedAt)
        {
            _lastFormattedUtc = item.VisitedAt;
            TimeDisplay = TimeFormatter.FormatRelativeTime(item.VisitedAt);
        }
    }

    public void Update(FavoriteEntry item)
    {
        Url = item.Url;
        Title = item.Title;
        if (_lastFormattedUtc != item.SavedAt)
        {
            _lastFormattedUtc = item.SavedAt;
            TimeDisplay = TimeFormatter.FormatRelativeTime(item.SavedAt);
        }
    }

    public void UpdateFrom(ControlItemViewModel item)
    {
        Url = item.Url;
        Title = item.Title;
        if (_lastFormattedUtc != item._lastFormattedUtc)
        {
            _lastFormattedUtc = item._lastFormattedUtc;
            TimeDisplay = item.TimeDisplay;
        }
    }

    public bool Equals(ControlItemViewModel? other)
    {
        return other is not null && string.Equals(Url, other.Url, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object? obj) => Equals(obj as ControlItemViewModel);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Url);
}
