using System.ComponentModel;
using System.IO;
using System.Windows;
using GenshinBrowser.Constants;
using GenshinBrowser.Models;
using GenshinBrowser.Services;
using GenshinBrowser.Windows;
using Microsoft.Web.WebView2.Core;

namespace GenshinBrowser;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly HistoryService _historyService;
    private readonly FavoritesService _favoritesService;
    private readonly WindowModeService _windowModeService;
    private readonly KeyboardHookService _keyboardHookService;

    private AppSettings _settings = new();
    private bool _browserReady;
    private bool _isShuttingDown;
    private string _currentAddress = string.Empty;
    private string _statusMessage = "正在初始化浏览器...";
    private ControlWindow? _controlWindow;
    private CancellationTokenSource? _settingsSaveCts;

    public MainWindow()
    {
        InitializeComponent();

        var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GenshinBrowser");
        Directory.CreateDirectory(dataRoot);

        _settingsService = new SettingsService(Path.Combine(dataRoot, "settings.json"));
        _historyService = new HistoryService(Path.Combine(dataRoot, "history.json"));
        _favoritesService = new FavoritesService(Path.Combine(dataRoot, "favorites.json"));
        _windowModeService = new WindowModeService(this);
        _keyboardHookService = new KeyboardHookService();

        Loaded += MainWindow_OnLoaded;
        Closing += MainWindow_OnClosing;
        LocationChanged += MainWindow_OnLocationOrSizeChanged;
        SizeChanged += MainWindow_OnLocationOrSizeChanged;
    }

    public WindowMode CurrentMode => _settings.WindowMode;

    public string CurrentAddress => _currentAddress;

    public string StatusMessage => _statusMessage;

    public IReadOnlyList<HistoryEntry> HistoryEntries => _historyService.GetEntries();

    public IReadOnlyList<FavoriteEntry> FavoriteEntries => _favoritesService.GetEntries();

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        _settings = await _settingsService.LoadAsync();
        RestoreWindowBounds();
        _windowModeService.ApplyMode(_settings.WindowMode);
        UpdateWindowTitle();

        _controlWindow = new ControlWindow(this);
        UpdateControlWindowVisibility();
        RefreshControlWindow();

        await InitializeBrowserAsync();

        _keyboardHookService.KPressed += KeyboardHookService_OnKPressed;
        _keyboardHookService.ModeTogglePressed += KeyboardHookService_OnModeTogglePressed;
        StartKeyboardHook();
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GenshinBrowser", "WebViewProfile");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await BrowserView.EnsureCoreWebView2Async(environment);

            BrowserView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            BrowserView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            BrowserView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            BrowserView.NavigationCompleted += BrowserView_OnNavigationCompleted;
            BrowserView.CoreWebView2.DocumentTitleChanged += BrowserView_OnDocumentTitleChanged;
            BrowserView.CoreWebView2.SourceChanged += BrowserView_OnSourceChanged;

            var startUrl = GetStartupUrl(_settings.LastUrl);
            _currentAddress = startUrl;
            BrowserView.CoreWebView2.Navigate(startUrl);
            SetStatusMessage("浏览器已就绪，登录态和缓存将自动保留。");
            _browserReady = true;
            UpdateWindowTitle();
            RefreshControlWindow();
        }
        catch (Exception ex)
        {
            SetStatusMessage($"初始化失败: {ex.Message}");
        }
    }

    private async void BrowserView_OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!_browserReady || BrowserView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            // 地址已经在 SourceChanged 中更新，这里只处理导航完成后的任务
            if (e.IsSuccess)
            {
                var title = BrowserView.CoreWebView2.DocumentTitle;
                await _historyService.AddEntryAsync(_currentAddress, string.IsNullOrWhiteSpace(title) ? _currentAddress : title);
            }

            SetStatusMessage(e.IsSuccess ? "页面已加载。按 K 控制视频播放/暂停，按 F8 切换固定/自由模式。" : $"加载失败: {e.WebErrorStatus}");
            RefreshControlWindow();
        }
        catch (Exception ex)
        {
            SetStatusMessage($"记录页面状态失败: {ex.Message}");
        }
    }

    private void BrowserView_OnDocumentTitleChanged(object? sender, object e)
    {
        UpdateWindowTitle();
    }

    private void BrowserView_OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        // 当页面 URL 改变时（包括用户点击链接、重定向等），立即更新地址栏
        if (!_browserReady || BrowserView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            var newUrl = BrowserView.CoreWebView2.Source;
            if (_currentAddress != newUrl)
            {
                _currentAddress = newUrl;
                _settings.LastUrl = newUrl;
                QueueSettingsSave(); // 异步保存配置
                RefreshControlWindow(); // 立即刷新控制窗口的地址栏
            }
        }
        catch (Exception ex)
        {
            SetStatusMessage($"地址更新失败: {ex.Message}");
        }
    }

    private void KeyboardHookService_OnKPressed(object? sender, EventArgs e)
    {
        if (_isShuttingDown)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() => _ = ToggleVideoPlaybackAsync());
    }

    private void KeyboardHookService_OnModeTogglePressed(object? sender, EventArgs e)
    {
        if (_isShuttingDown)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(ToggleWindowMode);
    }

    public async Task ToggleVideoPlaybackAsync()
    {
        if (!_browserReady || BrowserView.CoreWebView2 is null)
        {
            SetStatusMessage("浏览器尚未就绪。");
            return;
        }

        const string script = @"(() => {
  const videos = Array.from(document.querySelectorAll('video'));
  const video = videos.find(v => {
    const rect = v.getBoundingClientRect();
    return rect.width > 0 && rect.height > 0;
  }) || videos[0];

  if (!video) {
    return 'no-video';
  }

  if (video.paused) {
    video.play();
    return 'play';
  }

  video.pause();
  return 'pause';
})();";

        try
        {
            var result = await BrowserView.CoreWebView2.ExecuteScriptAsync(script);
            SetStatusMessage(result switch
            {
                "\"play\"" => "已播放。",
                "\"pause\"" => "已暂停。",
                "\"no-video\"" => "当前页面没有可控制的视频。",
                _ => "已发送播放控制命令。"
            });
        }
        catch (Exception ex)
        {
            SetStatusMessage($"播放控制失败: {ex.Message}");
        }
    }

    public void ToggleWindowMode()
    {
        _settings.WindowMode = _settings.WindowMode == WindowMode.Fixed ? WindowMode.Free : WindowMode.Fixed;

        if (_settings.WindowMode == WindowMode.Fixed)
        {
            SaveWindowBounds();
        }

        _windowModeService.ApplyMode(_settings.WindowMode);
        SetStatusMessage(_settings.WindowMode == WindowMode.Fixed
            ? "固定模式已开启。按 F8 返回自由模式。"
            : "自由模式。控制窗口已打开。可以移动窗口、查看历史并修改地址。");
        UpdateControlWindowVisibility();
        RefreshControlWindow();
        UpdateWindowTitle();
        QueueSettingsSave();
    }

    public void NavigateHome()
    {
        NavigateTo(AppConfig.Browser.DefaultUrl);
    }

    public void ReloadPage()
    {
        if (BrowserView.CoreWebView2 is null)
        {
            SetStatusMessage("浏览器尚未就绪。");
            return;
        }

        try
        {
            BrowserView.Reload();
        }
        catch (Exception ex)
        {
            SetStatusMessage($"刷新失败: {ex.Message}");
        }
    }

    public async Task AddCurrentPageToFavoritesAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentAddress))
        {
            return;
        }

        try
        {
            var title = BrowserView.CoreWebView2?.DocumentTitle;
            await _favoritesService.AddOrUpdateAsync(_currentAddress, string.IsNullOrWhiteSpace(title) ? _currentAddress : title);
            SetStatusMessage("已收藏当前页面。");
            RefreshControlWindow();
        }
        catch (Exception ex)
        {
            SetStatusMessage($"收藏失败: {ex.Message}");
        }
    }

    public async Task RemoveFavoriteAsync(string url)
    {
        try
        {
            await _favoritesService.RemoveAsync(url);
            SetStatusMessage("已取消收藏。");
            RefreshControlWindow();
        }
        catch (Exception ex)
        {
            SetStatusMessage($"取消收藏失败: {ex.Message}");
        }
    }

    public bool IsFavorite(string url)
    {
        return !string.IsNullOrWhiteSpace(url) && _favoritesService.Contains(url);
    }

    public async Task ClearHistoryAsync()
    {
        try
        {
            await _historyService.ClearAllAsync();
            SetStatusMessage("已清空浏览历史。");
            RefreshControlWindow();
        }
        catch (Exception ex)
        {
            SetStatusMessage($"清空历史失败: {ex.Message}");
        }
    }

    public async Task RemoveHistoryEntryAsync(string url)
    {
        try
        {
            await _historyService.RemoveAsync(url);
            SetStatusMessage("已从历史记录中移除。");
            RefreshControlWindow();
        }
        catch (Exception ex)
        {
            SetStatusMessage($"移除失败: {ex.Message}");
        }
    }

    public void SaveControlWindowBounds(double left, double top, double width, double height)
    {
        _settings.ControlWindowLeft = left;
        _settings.ControlWindowTop = top;
        _settings.ControlWindowWidth = width;
        _settings.ControlWindowHeight = height;
        QueueSettingsSave();
    }

    public bool RestoreControlWindowBounds(Window controlWindow)
    {
        if (_settings.ControlWindowWidth > 0)
        {
            controlWindow.Width = _settings.ControlWindowWidth;
        }

        if (_settings.ControlWindowHeight > 0)
        {
            controlWindow.Height = _settings.ControlWindowHeight;
        }

        var restoredPosition = false;
        if (_settings.ControlWindowLeft >= 0 && _settings.ControlWindowTop >= 0)
        {
            controlWindow.Left = _settings.ControlWindowLeft;
            controlWindow.Top = _settings.ControlWindowTop;
            restoredPosition = true;
        }

        WindowBoundsHelper.ClampToWorkArea(controlWindow);
        return restoredPosition;
    }

    public void NavigateTo(string? input)
    {
        if (BrowserView.CoreWebView2 is null || string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        var target = BuildNavigationTarget(input);
        if (target is null)
        {
            SetStatusMessage("只支持打开 http/https 页面。");
            return;
        }

        try
        {
            BrowserView.CoreWebView2.Navigate(target);
            _currentAddress = target;
            SetStatusMessage($"正在打开: {target}");
            RefreshControlWindow();
        }
        catch (Exception ex)
        {
            SetStatusMessage($"打开失败: {ex.Message}");
        }
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        _isShuttingDown = true;

        // 立即停止键盘钩子
        _keyboardHookService.Dispose();

        // 取消待处理的保存操作
        _settingsSaveCts?.Cancel();
        _settingsSaveCts?.Dispose();

        // 立即关闭控制窗口（不等待保存）
        if (_controlWindow is not null)
        {
            _controlWindow.AllowClose = true;
            _controlWindow.Close();
        }

        // 保存窗口位置到内存
        SaveWindowBounds();

        // 异步清理 WebView2 和保存配置（不阻塞关闭）
        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                // 取消事件订阅
                if (BrowserView.CoreWebView2 is not null)
                {
                    BrowserView.NavigationCompleted -= BrowserView_OnNavigationCompleted;
                    BrowserView.CoreWebView2.DocumentTitleChanged -= BrowserView_OnDocumentTitleChanged;
                }

                // 后台保存配置
                await _settingsService.SaveAsync(_settings).ConfigureAwait(false);
            }
            catch
            {
                // 关闭时忽略错误
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void MainWindow_OnLocationOrSizeChanged(object? sender, EventArgs e)
    {
        if (_settings.WindowMode == WindowMode.Free)
        {
            SaveWindowBounds();
            _controlWindow?.ShowNearBrowserWindow();
        }
    }

    private void RestoreWindowBounds()
    {
        if (_settings.WindowWidth > 0)
        {
            Width = _settings.WindowWidth;
        }

        if (_settings.WindowHeight > 0)
        {
            Height = _settings.WindowHeight;
        }

        Left = _settings.WindowLeft;
        Top = _settings.WindowTop;
        WindowBoundsHelper.ClampToWorkArea(this);
    }

    private void SaveWindowBounds()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
    }

    private void StartKeyboardHook()
    {
        if (!_keyboardHookService.Start(out var errorCode))
        {
            SetStatusMessage($"全局热键安装失败，Win32 错误码: {errorCode}。控制面板按钮仍可使用。");
        }
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            await _settingsService.SaveAsync(_settings);
        }
        catch (Exception ex)
        {
            if (!_isShuttingDown)
            {
                SetStatusMessage($"保存设置失败: {ex.Message}");
            }
        }
    }

    private void QueueSettingsSave()
    {
        _settingsSaveCts?.Cancel();
        _settingsSaveCts?.Dispose();  // 修复：正确释放资源
        _settingsSaveCts = new CancellationTokenSource();
        _ = SaveSettingsDebouncedAsync(_settingsSaveCts.Token);
    }

    private async Task SaveSettingsDebouncedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(AppConfig.Ui.SettingsSaveDebounceMs, cancellationToken).ConfigureAwait(false);
            await SaveSettingsAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 被新的保存请求取消，这是预期行为
        }
    }

    private static string GetStartupUrl(string savedUrl)
    {
        if (Uri.TryCreate(savedUrl?.Trim(), UriKind.Absolute, out var uri) && IsHttpOrHttps(uri))
        {
            return uri.ToString();
        }

        return AppConfig.Browser.DefaultUrl;
    }

    private static string? BuildNavigationTarget(string? input)
    {
        var target = input?.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        if (Uri.TryCreate(target, UriKind.Absolute, out var absoluteUri))
        {
            if (IsHttpOrHttps(absoluteUri))
            {
                return absoluteUri.ToString();
            }

            if (target.Contains("://", StringComparison.Ordinal))
            {
                return null;
            }
        }

        if (LooksLikeWebAddress(target))
        {
            var hasHttpScheme = target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            var prefixedTarget = hasHttpScheme ? target : $"https://{target}";

            if (Uri.TryCreate(prefixedTarget, UriKind.Absolute, out var uri) && IsHttpOrHttps(uri))
            {
                return uri.ToString();
            }
        }

        return $"https://search.bilibili.com/all?keyword={Uri.EscapeDataString(target)}";
    }

    private static bool IsHttpOrHttps(Uri uri)
    {
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }

    private static bool LooksLikeWebAddress(string input)
    {
        if (input.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var separatorIndex = input.IndexOfAny(new[] { '/', '?', '#' });
        var hostPart = separatorIndex >= 0 ? input[..separatorIndex] : input;
        if (string.IsNullOrWhiteSpace(hostPart))
        {
            return false;
        }

        var colonIndex = hostPart.LastIndexOf(':');
        if (colonIndex > 0 && hostPart.IndexOf(':') == colonIndex)
        {
            hostPart = hostPart[..colonIndex];
        }

        return hostPart.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (hostPart.Contains('.', StringComparison.Ordinal) && Uri.CheckHostName(hostPart) != UriHostNameType.Unknown);
    }

    private void UpdateControlWindowVisibility()
    {
        if (_controlWindow is null || _isShuttingDown)
        {
            return;
        }

        if (_settings.WindowMode == WindowMode.Free)
        {
            _controlWindow.ShowNearBrowserWindow();
            _controlWindow.Show();
            UpdateWindowTitle();
            return;
        }

        _controlWindow.Hide();
        UpdateWindowTitle();
    }

    private void RefreshControlWindow()
    {
        _controlWindow?.RefreshFromBrowser();
    }

    private void SetStatusMessage(string message)
    {
        _statusMessage = message;
        _controlWindow?.RefreshFromBrowser();
    }

    private void UpdateWindowTitle()
    {
        var pageTitle = BrowserView.CoreWebView2 is null || string.IsNullOrWhiteSpace(BrowserView.CoreWebView2.DocumentTitle)
            ? "Genshin Browser"
            : $"{BrowserView.CoreWebView2.DocumentTitle} - Genshin Browser";

        var panelHint = _settings.WindowMode == WindowMode.Free
            ? "F8关闭控制面板"
            : "F8打开控制面板";

        Title = $"{pageTitle} [{panelHint}]";
    }
}
