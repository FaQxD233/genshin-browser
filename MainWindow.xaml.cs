using System.ComponentModel;
using System.IO;
using System.Windows;
using GenshinBrowser.Models;
using GenshinBrowser.Services;
using GenshinBrowser.Windows;
using Microsoft.Web.WebView2.Core;

namespace GenshinBrowser;

public partial class MainWindow : Window
{
    private const string DefaultUrl = "https://search.bilibili.com/all?keyword=%E5%8E%9F%E7%A5%9E%20%E6%94%BB%E7%95%A5";

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

        _keyboardHookService.KPressed += KeyboardHookService_OnKPressed;
        _keyboardHookService.ModeTogglePressed += KeyboardHookService_OnModeTogglePressed;
        _keyboardHookService.Start();

        await InitializeBrowserAsync();
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

            var startUrl = string.IsNullOrWhiteSpace(_settings.LastUrl) ? DefaultUrl : _settings.LastUrl;
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

        var url = BrowserView.Source?.ToString() ?? BrowserView.CoreWebView2.Source;
        _currentAddress = url;
        _settings.LastUrl = url;
        await _settingsService.SaveAsync(_settings);

        var title = BrowserView.CoreWebView2.DocumentTitle;
        await _historyService.AddEntryAsync(url, string.IsNullOrWhiteSpace(title) ? url : title);

        SetStatusMessage(e.IsSuccess ? "页面已加载。按 K 控制视频播放/暂停，按 F8 切换固定/自由模式。" : $"加载失败: {e.WebErrorStatus}");
        RefreshControlWindow();
    }

    private void BrowserView_OnDocumentTitleChanged(object? sender, object e)
    {
        UpdateWindowTitle();
    }

    private void KeyboardHookService_OnKPressed(object? sender, EventArgs e)
    {
        _ = Dispatcher.InvokeAsync(ToggleVideoPlaybackAsync);
    }

    private void KeyboardHookService_OnModeTogglePressed(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(ToggleWindowMode);
    }

    public async Task ToggleVideoPlaybackAsync()
    {
        if (BrowserView.CoreWebView2 is null)
        {
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
        _ = _settingsService.SaveAsync(_settings);
    }

    public void NavigateHome()
    {
        NavigateTo(DefaultUrl);
    }

    public void ReloadPage()
    {
        BrowserView.Reload();
    }

    public async Task AddCurrentPageToFavoritesAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentAddress))
        {
            return;
        }

        var title = BrowserView.CoreWebView2?.DocumentTitle;
        await _favoritesService.AddOrUpdateAsync(_currentAddress, string.IsNullOrWhiteSpace(title) ? _currentAddress : title);
        SetStatusMessage("已收藏当前页面。");
        RefreshControlWindow();
    }

    public async Task RemoveFavoriteAsync(string url)
    {
        await _favoritesService.RemoveAsync(url);
        SetStatusMessage("已取消收藏。");
        RefreshControlWindow();
    }

    public bool IsFavorite(string url)
    {
        return !string.IsNullOrWhiteSpace(url) && _favoritesService.Contains(url);
    }

    public void SaveControlWindowBounds(double left, double top, double width, double height)
    {
        _settings.ControlWindowLeft = left;
        _settings.ControlWindowTop = top;
        _settings.ControlWindowWidth = width;
        _settings.ControlWindowHeight = height;
        _ = _settingsService.SaveAsync(_settings);
    }

    public void RestoreControlWindowBounds(Window controlWindow)
    {
        if (_settings.ControlWindowWidth > 0)
        {
            controlWindow.Width = _settings.ControlWindowWidth;
        }

        if (_settings.ControlWindowHeight > 0)
        {
            controlWindow.Height = _settings.ControlWindowHeight;
        }

        if (_settings.ControlWindowLeft >= 0 && _settings.ControlWindowTop >= 0)
        {
            controlWindow.Left = _settings.ControlWindowLeft;
            controlWindow.Top = _settings.ControlWindowTop;
        }
    }

    public void NavigateTo(string? input)
    {
        if (BrowserView.CoreWebView2 is null || string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        var target = input.Trim();
        if (!Uri.TryCreate(target, UriKind.Absolute, out _))
        {
            target = $"https://{target}";
        }

        BrowserView.CoreWebView2.Navigate(target);
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        _isShuttingDown = true;
        SaveWindowBounds();
        _keyboardHookService.Dispose();

        if (_controlWindow is not null)
        {
            _controlWindow.AllowClose = true;
            _controlWindow.SaveWindowBounds();
            _controlWindow.Close();
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
