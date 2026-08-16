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
    // 上次格式化时的本地日期：相对时间文案（今天/昨天）跨天后必须重算，即使时间戳未变
    private DateTime _lastFormattedLocalDate;

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
        // 仅在时间戳或本地日期变化时才重新格式化；同一条历史被反复刷新时跳过资源查找与字符串分配。
        // 日期参与判定使跨午夜后第一次 Update 即刷新「今天/昨天」文案。
        if (_lastFormattedUtc != item.VisitedAt || _lastFormattedLocalDate != DateTime.Now.Date)
        {
            _lastFormattedUtc = item.VisitedAt;
            _lastFormattedLocalDate = DateTime.Now.Date;
            TimeDisplay = TimeFormatter.FormatRelativeTime(item.VisitedAt);
        }
    }

    public void Update(FavoriteEntry item)
    {
        Url = item.Url;
        Title = item.Title;
        if (_lastFormattedUtc != item.SavedAt || _lastFormattedLocalDate != DateTime.Now.Date)
        {
            _lastFormattedUtc = item.SavedAt;
            _lastFormattedLocalDate = DateTime.Now.Date;
            TimeDisplay = TimeFormatter.FormatRelativeTime(item.SavedAt);
        }
    }

    /// <summary>跨天时由控制窗 ViewModel 定时触发：重算相对时间文案并通知绑定。</summary>
    public void RefreshRelativeTime()
    {
        if (_lastFormattedUtc != default)
        {
            _lastFormattedLocalDate = DateTime.Now.Date;
            TimeDisplay = TimeFormatter.FormatRelativeTime(_lastFormattedUtc);
        }
    }

    public void UpdateFrom(ControlItemViewModel item)
    {
        Url = item.Url;
        Title = item.Title;
        // TimeDisplay 参与比较：跨天刷新只改 TimeDisplay 不改时间戳，
        // 若仅比较时间戳会跳过同步，可见列表拿不到刷新后的相对时间文案。
        if (_lastFormattedUtc != item._lastFormattedUtc ||
            !string.Equals(TimeDisplay, item.TimeDisplay, StringComparison.Ordinal))
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
