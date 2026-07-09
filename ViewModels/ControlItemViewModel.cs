using GenshinBrowser.Models;
using GenshinBrowser.Utils;

namespace GenshinBrowser.ViewModels;

public sealed class ControlItemViewModel : ViewModelBase, IEquatable<ControlItemViewModel>
{
    private string _url = string.Empty;
    private string _title = string.Empty;
    private string _timeDisplay = string.Empty;

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
        TimeDisplay = TimeFormatter.FormatRelativeTime(item.VisitedAt);
    }

    public void Update(FavoriteEntry item)
    {
        Url = item.Url;
        Title = item.Title;
        TimeDisplay = TimeFormatter.FormatRelativeTime(item.SavedAt);
    }

    public void UpdateFrom(ControlItemViewModel item)
    {
        Url = item.Url;
        Title = item.Title;
        TimeDisplay = item.TimeDisplay;
    }

    public bool Equals(ControlItemViewModel? other)
    {
        return other is not null && string.Equals(Url, other.Url, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object? obj) => Equals(obj as ControlItemViewModel);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Url);
}
