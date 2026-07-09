using GenshinBrowser.Models;
using System.Windows;
using System.Windows.Input;

namespace GenshinBrowser.Windows;

public interface IControlBrowser
{
    event EventHandler? BrowserStateChanged;

    WindowMode CurrentMode { get; }

    double WindowOpacity { get; set; }

    Key ToggleModeKey { get; set; }

    ModifierKeys ToggleModeModifiers { get; set; }

    Key TogglePlaybackKey { get; set; }

    ModifierKeys TogglePlaybackModifiers { get; set; }

    string CurrentAddress { get; }

    string StatusMessage { get; }

    bool CanGoBack { get; }

    bool CanGoForward { get; }

    void GoBack();

    void GoForward();

    IReadOnlyList<HistoryEntry> HistoryEntries { get; }

    IReadOnlyList<FavoriteEntry> FavoriteEntries { get; }

    bool IsFavorite(string url);

    void ToggleWindowMode();

    Task ToggleVideoPlaybackAsync();

    void NavigateHome();

    void ReloadPage();

    void NavigateTo(string? input);

    Task AddCurrentPageToFavoritesAsync();

    Task RemoveFavoriteAsync(string url);

    Task ClearHistoryAsync();

    Task RemoveHistoryEntryAsync(string url);

    void SaveControlWindowBounds(double left, double top, double width, double height);

    bool RestoreControlWindowBounds(Window controlWindow);
}
