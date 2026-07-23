using GenshinBrowser.Models;

namespace GenshinBrowser.Browser;

public interface IBrowserSession
{
    event EventHandler<BrowserStateChangedEventArgs>? BrowserStateChanged;

    event EventHandler? DownloadsChanged;

    WindowMode CurrentMode { get; }

    double WindowOpacity { get; set; }

    HotkeyGesture ToggleModeHotkey { get; }

    HotkeyGesture TogglePlaybackHotkey { get; }

    string CurrentAddress { get; }

    string DocumentTitle { get; }

    string StatusMessage { get; }

    StatusLevel LastStatusLevel { get; }

    bool CanGoBack { get; }

    bool CanGoForward { get; }

    bool IsNavigating { get; }

    double ZoomFactor { get; set; }

    string ThemeMode { get; set; }

    string UiLanguage { get; set; }

    double BrowserWindowWidth { get; }

    double BrowserWindowHeight { get; }

    IReadOnlyList<DownloadItem> Downloads { get; }

    IReadOnlyList<HistoryEntry> HistoryEntries { get; }

    IReadOnlyList<FavoriteEntry> FavoriteEntries { get; }

    bool TrySetToggleModeHotkey(HotkeyGesture gesture);

    bool TrySetTogglePlaybackHotkey(HotkeyGesture gesture);

    bool IsFavorite(string url);

    void GoBack();

    void GoForward();

    void ReloadPage();

    void NavigateTo(string? input);

    void ToggleWindowMode();

    void SetWindowMode(WindowMode mode);

    Task ToggleVideoPlaybackAsync();

    Task AddCurrentPageToFavoritesAsync();

    Task RemoveFavoriteAsync(string url);

    Task RemoveHistoryEntryAsync(string url);

    void MoveBrowserToCorner(WindowCorner corner);

    void ResizeBrowserWindow(double width, double height);

    void RestoreDefaultSettings();

    void CancelDownload(DownloadItem item);

    void RetryDownload(DownloadItem item);

    void OpenDownloadFile(DownloadItem item);

    void OpenDownloadFolder(DownloadItem item);

    void ClearFinishedDownloads();

    void SetHotkeyRecordingActive(bool active);

    Task<bool> ClearBrowsingDataAsync(bool silent = false);
}
