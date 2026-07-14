using System.Collections.ObjectModel;
using GenshinBrowser.Models;
using System.Windows;
using System.Windows.Input;

namespace GenshinBrowser.Windows;

public interface IControlBrowser
{
    /// <summary>
    /// 浏览器状态变化。订阅方应根据 <see cref="BrowserStateChangedEventArgs.Kind"/> 做增量刷新，
    /// 避免每次状态文案变化都同步历史/收藏列表。
    /// </summary>
    event EventHandler<BrowserStateChangedEventArgs>? BrowserStateChanged;

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

    /// <summary>
    /// 切换到指定窗口模式（浏览 / 浮窗）。已是目标模式时为 no-op。
    /// </summary>
    void SetWindowMode(WindowMode mode);

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
    /// 当前主题偏好：Dark / Light / System。
    /// </summary>
    string ThemeMode { get; set; }

    /// <summary>
    /// 当前界面语言：zh-CN / en-US。
    /// </summary>
    string UiLanguage { get; set; }

    /// <summary>
    /// 浏览器主窗当前宽度（逻辑像素）。
    /// </summary>
    double BrowserWindowWidth { get; set; }

    /// <summary>
    /// 浏览器主窗当前高度（逻辑像素）。
    /// </summary>
    double BrowserWindowHeight { get; set; }

    /// <summary>
    /// 将浏览器主窗贴到当前显示器工作区指定角（无边距）。
    /// corner: TopLeft / TopRight / BottomLeft / BottomRight
    /// </summary>
    void MoveBrowserToCorner(string corner);

    void CancelDownload(DownloadItem item);

    void RetryDownload(DownloadItem item);

    void OpenDownloadFile(DownloadItem item);

    void OpenDownloadFolder(DownloadItem item);

    void ClearFinishedDownloads();

    /// <summary>
    /// 清理 WebView2 浏览数据（磁盘缓存、DOM 存储、Service Worker）。
    /// 保留 Cookie、浏览历史、下载记录、自动填充和已保存密码；应用内收藏夹/历史不受影响。
    /// </summary>
    /// <param name="silent">为 true 时不写状态栏、不刷新控制窗（启动自动清理用）。</param>
    Task<bool> ClearBrowsingDataAsync(bool silent = false);
}
