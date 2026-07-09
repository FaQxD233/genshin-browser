using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GenshinBrowser.Constants;
using GenshinBrowser.Models;
using GenshinBrowser.Services;
using GenshinBrowser.Windows;
using Microsoft.Web.WebView2.Core;
using Application = System.Windows.Application;

namespace GenshinBrowser;

public partial class MainWindow : Window, IControlBrowser
{
    private readonly SettingsService _settingsService;
    private readonly HistoryService _historyService;
    private readonly FavoritesService _favoritesService;
    private readonly WindowModeService _windowModeService;
    private readonly KeyboardHookService _keyboardHookService;

    private AppSettings _settings = new();
    private bool _browserReady;
    private bool _isShuttingDown;
    private bool _isRealClose;
    private string _currentAddress = string.Empty;
    private string _statusMessage = "正在初始化浏览器...";
    private ControlWindow? _controlWindow;
    private CancellationTokenSource? _settingsSaveCts;

    public event EventHandler? BrowserStateChanged;

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
        DragBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.Source is System.Windows.Controls.Button) return;
            DragMove();
        };
        LocationChanged += MainWindow_OnLocationOrSizeChanged;
        SizeChanged += MainWindow_OnLocationOrSizeChanged;
    }

    public WindowMode CurrentMode => _settings.WindowMode;

    public double WindowOpacity
    {
        get => _settings.WindowOpacity;
        set
        {
            if (Math.Abs(_settings.WindowOpacity - value) > 0.001)
            {
                _settings.WindowOpacity = value;
                ApplyWindowOpacity(value);
                QueueSettingsSave();
            }
        }
    }

    public Key ToggleModeKey
    {
        get => _settings.ToggleModeKey;
        set
        {
            if (_settings.ToggleModeKey != value)
            {
                _settings.ToggleModeKey = value;
                _keyboardHookService.ToggleModeVk = KeyInterop.VirtualKeyFromKey(value);
                QueueSettingsSave();
            }
        }
    }

    public ModifierKeys ToggleModeModifiers
    {
        get => _settings.ToggleModeModifiers;
        set
        {
            if (_settings.ToggleModeModifiers != value)
            {
                _settings.ToggleModeModifiers = value;
                _keyboardHookService.ToggleModeModifiers = value;
                QueueSettingsSave();
            }
        }
    }

    public Key TogglePlaybackKey
    {
        get => _settings.TogglePlaybackKey;
        set
        {
            if (_settings.TogglePlaybackKey != value)
            {
                _settings.TogglePlaybackKey = value;
                _keyboardHookService.TogglePlaybackVk = KeyInterop.VirtualKeyFromKey(value);
                QueueSettingsSave();
            }
        }
    }

    public ModifierKeys TogglePlaybackModifiers
    {
        get => _settings.TogglePlaybackModifiers;
        set
        {
            if (_settings.TogglePlaybackModifiers != value)
            {
                _settings.TogglePlaybackModifiers = value;
                _keyboardHookService.TogglePlaybackModifiers = value;
                QueueSettingsSave();
            }
        }
    }

    public string CurrentAddress => _currentAddress;

    public string StatusMessage => _statusMessage;

    public bool CanGoBack => BrowserView.CoreWebView2?.CanGoBack ?? false;

    public bool CanGoForward => BrowserView.CoreWebView2?.CanGoForward ?? false;

    public void GoBack()
    {
        if (CanGoBack)
        {
            BrowserView.GoBack();
        }
    }

    public void GoForward()
    {
        if (CanGoForward)
        {
            BrowserView.GoForward();
        }
    }

    public IReadOnlyList<HistoryEntry> HistoryEntries => _historyService.GetEntries();

    public IReadOnlyList<FavoriteEntry> FavoriteEntries => _favoritesService.GetEntries();

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
        _settings = await _settingsService.LoadAsync();
        RestoreWindowBounds();

        ApplyWindowOpacity(_settings.WindowOpacity);
        _keyboardHookService.ToggleModeVk = KeyInterop.VirtualKeyFromKey(_settings.ToggleModeKey);
        _keyboardHookService.ToggleModeModifiers = _settings.ToggleModeModifiers;
        _keyboardHookService.TogglePlaybackVk = KeyInterop.VirtualKeyFromKey(_settings.TogglePlaybackKey);
        _keyboardHookService.TogglePlaybackModifiers = _settings.TogglePlaybackModifiers;
        _windowModeService.ApplyMode(_settings.WindowMode);
        _keyboardHookService.IsGamingMode = _settings.WindowMode == WindowMode.Fixed;
        UpdateWindowTitle();

        _controlWindow = new ControlWindow(this);
        UpdateControlWindowVisibility();
        RefreshControlWindow();

        await EnsureWebView2RuntimeAsync();
        await InitializeBrowserAsync();

        _keyboardHookService.KPressed += KeyboardHookService_OnKPressed;
        _keyboardHookService.ModeTogglePressed += KeyboardHookService_OnModeTogglePressed;
        StartKeyboardHook();

        // 跟踪应用前台状态：自由模式下离开前台时禁用全局 K，避免影响其它软件输入
        Application.Current.Activated += App_OnActivated;
        Application.Current.Deactivated += App_OnDeactivated;
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "MainWindow_OnLoaded");
            System.Windows.MessageBox.Show($"启动失败：\n{ex.GetType().Name}: {ex.Message}", "Genshin Browser", MessageBoxButton.OK);
        }
    }

    private void App_OnActivated(object? sender, EventArgs e) => _keyboardHookService.IsAppActive = true;

    private void App_OnDeactivated(object? sender, EventArgs e) => _keyboardHookService.IsAppActive = false;

    private bool IsWebView2RuntimeInstalled()
    {
        try
        {
            return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsureWebView2RuntimeAsync()
    {
        if (IsWebView2RuntimeInstalled())
            return;

        var bootstrapperPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MicrosoftEdgeWebview2Setup.exe");
        if (!File.Exists(bootstrapperPath))
        {
            var msg = "未检测到 WebView2 Runtime（浏览器核心组件），且未找到自动安装程序。\n\n" +
                      "请手动下载安装：https://developer.microsoft.com/microsoft-edge/webview2/";
            throw new InvalidOperationException(msg);
        }

        _statusMessage = "正在安装 WebView2 Runtime...";
        BrowserStateChanged?.Invoke(this, EventArgs.Empty);
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = bootstrapperPath,
            Arguments = "/silent",
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("无法启动 WebView2 安装程序。");

        await process.WaitForExitAsync();

        if (!IsWebView2RuntimeInstalled())
            throw new InvalidOperationException("WebView2 Runtime 自动安装失败，请手动安装。");
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
            BrowserView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            ApplyWindowOpacity(_settings.WindowOpacity);

            // 让网页自身的背景也透明，否则页面深色 CSS 背景（如 GitHub/B 站 dark 主题）
            // 会盖住浮窗透明度，导致窗口看起来仍是黑色底色。仅覆盖背景，不影响布局。
            const string transparentBackgroundScript =
                "(() => { const s = document.createElement('style');" +
                "s.textContent = 'html,body{background:transparent !important;}';" +
                "document.head.appendChild(s); })();";
            await BrowserView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(transparentBackgroundScript);

            BrowserView.NavigationCompleted += BrowserView_OnNavigationCompleted;
            BrowserView.CoreWebView2.DocumentTitleChanged += BrowserView_OnDocumentTitleChanged;
            BrowserView.CoreWebView2.SourceChanged += BrowserView_OnSourceChanged;
            BrowserView.CoreWebView2.NewWindowRequested += BrowserView_OnNewWindowRequested;
            BrowserView.CoreWebView2.HistoryChanged += BrowserView_OnHistoryChanged;

            var startUrl = GetStartupUrl(_settings.LastUrl);
            _currentAddress = startUrl;
            BrowserView.CoreWebView2.Navigate(startUrl);
            // 对初始已加载的页面补执行一次（文档创建脚本只作用于之后的导航）
            _ = BrowserView.CoreWebView2.ExecuteScriptAsync(transparentBackgroundScript);
            SetStatusMessage("浏览器已就绪，登录态和缓存将自动保留。");
            _browserReady = true;
            UpdateWindowTitle();
            RefreshControlWindow();
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Initialize browser");
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
            // 使用当前 Source 而不是 _currentAddress，避免竞态条件
            if (e.IsSuccess)
            {
                var currentUrl = BrowserView.CoreWebView2.Source;
                var title = BrowserView.CoreWebView2.DocumentTitle;
                await _historyService.AddEntryAsync(currentUrl, string.IsNullOrWhiteSpace(title) ? currentUrl : title);
            }

            SetStatusMessage(e.IsSuccess ? "页面已加载。按 K 控制视频播放/暂停，按 F8 切换固定/自由模式。" : $"加载失败: {e.WebErrorStatus}");
            RefreshControlWindow();
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Navigation completed handling");
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
            FileLogger.LogException(ex, "Source changed handling");
            SetStatusMessage($"地址更新失败: {ex.Message}");
        }
    }

    private void BrowserView_OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // 拦截「在新窗口打开」的链接，统一在当前浏览器内导航，避免弹出无控制窗口的裸 WebView2
        if (string.IsNullOrEmpty(e.Uri))
        {
            return;
        }

        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && IsHttpOrHttps(uri))
        {
            e.Handled = true;
            BrowserView.CoreWebView2.Navigate(e.Uri);
        }
    }

    private void BrowserView_OnHistoryChanged(object? sender, object e)
    {
        RefreshControlWindow();
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
            FileLogger.LogException(ex, "Toggle video playback");
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
        _keyboardHookService.IsGamingMode = _settings.WindowMode == WindowMode.Fixed;
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
            FileLogger.LogException(ex, "Reload page");
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
            FileLogger.LogException(ex, "Add current page to favorites");
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
            FileLogger.LogException(ex, "Remove favorite");
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
            FileLogger.LogException(ex, "Clear history");
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
            FileLogger.LogException(ex, "Remove history entry");
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
            FileLogger.LogException(ex, "Navigate to target");
            SetStatusMessage($"打开失败: {ex.Message}");
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private async void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isRealClose)
        {
            return;
        }

        // 拦截当前的关闭，并在后台进行清理和异步保存
        e.Cancel = true;
        _isRealClose = true;

        await CleanupAndSaveAsync();

        // 重新调用 Close，此时 _isRealClose 为 true，将直接返回并退出
        Close();
    }

    private async Task CleanupAndSaveAsync()
    {
        _isShuttingDown = true;

        // 立即停止键盘钩子
        _keyboardHookService.Dispose();

        // 取消前台状态跟踪
        if (Application.Current is not null)
        {
            Application.Current.Activated -= App_OnActivated;
            Application.Current.Deactivated -= App_OnDeactivated;
        }

        // 取消待处理的保存操作
        _settingsSaveCts?.Cancel();
        _settingsSaveCts?.Dispose();
        _settingsSaveCts = null;

        // 取消事件订阅
        LocationChanged -= MainWindow_OnLocationOrSizeChanged;
        SizeChanged -= MainWindow_OnLocationOrSizeChanged;

        // 立即关闭控制窗口（不等待保存）
        if (_controlWindow is not null)
        {
            _controlWindow.AllowClose = true;
            _controlWindow.Close();
        }

        // 保存窗口位置到内存
        SaveWindowBounds();

        try
        {
            // 取消事件订阅
            if (BrowserView.CoreWebView2 is not null)
            {
                BrowserView.NavigationCompleted -= BrowserView_OnNavigationCompleted;
                BrowserView.CoreWebView2.DocumentTitleChanged -= BrowserView_OnDocumentTitleChanged;
                BrowserView.CoreWebView2.SourceChanged -= BrowserView_OnSourceChanged;
                BrowserView.CoreWebView2.NewWindowRequested -= BrowserView_OnNewWindowRequested;
                BrowserView.CoreWebView2.HistoryChanged -= BrowserView_OnHistoryChanged;
            }

            // 异步保存配置，不阻塞 UI 线程，让 WebView2 能继续泵送消息
            await _settingsService.SaveAsync(_settings);

            // 释放服务资源
            _settingsService.Dispose();
            _historyService.Dispose();
            _favoritesService.Dispose();
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Main window closing cleanup");
        }
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
            FileLogger.LogException(ex, "Save settings");
            if (!_isShuttingDown)
            {
                SetStatusMessage($"保存设置失败: {ex.Message}");
            }
        }
    }

    private void QueueSettingsSave()
    {
        var oldCts = _settingsSaveCts;
        _settingsSaveCts = new CancellationTokenSource();

        oldCts?.Cancel();
        oldCts?.Dispose();

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
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(RefreshControlWindow);
            return;
        }

        BrowserStateChanged?.Invoke(this, EventArgs.Empty);
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

        Title = pageTitle;
    }

    private void ApplyWindowOpacity(double opacity)
    {
        // WebView2CompositionControl 将浏览器内容渲染进 WPF 视觉树（Image），
        // 因此直接设置其 Opacity 即可让透明度对网页内容生效，无需操作 Win32 分层窗口。
        // 该控件的 Opacity 被声明为隐藏的 get-only 属性，需转型到 FrameworkElement 设置。
        var clamped = Math.Clamp(opacity, 0.1, 1.0);
        ((FrameworkElement)BrowserView).Opacity = clamped;
        FileLogger.LogDebug($"ApplyWindowOpacity: opacity={opacity}, clamped={clamped}");
    }
}
