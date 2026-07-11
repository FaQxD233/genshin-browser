using System.Collections.ObjectModel;
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

    /// <summary>
    /// 是否正在导航加载中（用于地址栏进度指示）。
    /// </summary>
    bool IsNavigating { get; }

    /// <summary>
    /// 最近一次状态消息的级别，供控制窗决定是否弹出 Toast。
    /// </summary>
    StatusLevel LastStatusLevel { get; }

    /// <summary>
    /// 当前页面缩放系数，1.0 = 100%。
    /// </summary>
    double ZoomFactor { get; set; }

    /// <summary>
    /// 缩放系数变化时触发，供控制窗口刷新百分比显示。
    /// </summary>
    event EventHandler? ZoomChanged;

    /// <summary>
    /// 下载列表（可观察），供下载面板绑定。
    /// </summary>
    ObservableCollection<DownloadItem> Downloads { get; }

    /// <summary>
    /// 下载列表发生变化（新增 / 状态变更）时触发。
    /// </summary>
    event EventHandler? DownloadsChanged;

    void GoBack();

    void GoForward();

    IReadOnlyList<HistoryEntry> HistoryEntries { get; }

    IReadOnlyList<FavoriteEntry> FavoriteEntries { get; }

    bool IsFavorite(string url);

    void ToggleWindowMode();

    Task ToggleVideoPlaybackAsync();

    void ReloadPage();

    void NavigateTo(string? input);

    Task AddCurrentPageToFavoritesAsync();

    Task RemoveFavoriteAsync(string url);

    Task RemoveHistoryEntryAsync(string url);

    void SaveControlWindowBounds(double left, double top, double width, double height);

    bool RestoreControlWindowBounds(Window controlWindow);

    /// <summary>
    /// 恢复浮窗设置（不透明度、快捷键、缩放）为默认值。
    /// </summary>
    void RestoreDefaultSettings();

    /// <summary>
    /// 当前主题：Dark / Light。
    /// </summary>
    string ThemeMode { get; set; }

    /// <summary>
    /// 当前界面语言：zh-CN / en-US。
    /// </summary>
    string UiLanguage { get; set; }

    void CancelDownload(DownloadItem item);

    void OpenDownloadFile(DownloadItem item);

    void OpenDownloadFolder(DownloadItem item);

    void ClearFinishedDownloads();
}
