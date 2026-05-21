using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Input;
using GenshinBrowser.Models;
using GenshinBrowser.Services;
using Microsoft.Web.WebView2.Core;

namespace GenshinBrowser;

public partial class MainWindow : Window
{
    private const string DefaultUrl = "https://search.bilibili.com/all?keyword=%E5%8E%9F%E7%A5%9E%20%E6%94%BB%E7%95%A5";
    private const double HistoryPanelHeight = 100;

    private readonly ObservableCollection<HistoryItemViewModel> _historyItems = new();
    private readonly SettingsService _settingsService;
    private readonly HistoryService _historyService;
    private readonly WindowModeService _windowModeService;
    private readonly KeyboardHookService _keyboardHookService;
    private readonly DispatcherTimer _temporaryUiTimer;

    private AppSettings _settings = new();
    private bool _browserReady;
    private bool _temporaryUiVisible;

    public MainWindow()
    {
        InitializeComponent();

        var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GenshinBrowser");
        Directory.CreateDirectory(dataRoot);

        _settingsService = new SettingsService(Path.Combine(dataRoot, "settings.json"));
        _historyService = new HistoryService(Path.Combine(dataRoot, "history.json"));
        _windowModeService = new WindowModeService(this);
        _keyboardHookService = new KeyboardHookService();
        _temporaryUiTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4),
        };

        _temporaryUiTimer.Tick += TemporaryUiTimer_OnTick;

        HistoryListBox.ItemsSource = _historyItems;

        Loaded += MainWindow_OnLoaded;
        Closing += MainWindow_OnClosing;
        StateChanged += MainWindow_OnStateChanged;
        LocationChanged += MainWindow_OnLocationOrSizeChanged;
        SizeChanged += MainWindow_OnLocationOrSizeChanged;
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        _settings = await _settingsService.LoadAsync();
        RestoreWindowBounds();
        _windowModeService.ApplyMode(_settings.WindowMode);
        UpdateModeUi();
        LoadHistoryUi();

        _keyboardHookService.KPressed += KeyboardHookService_OnKPressed;
        _keyboardHookService.TemporaryUiRevealPressed += KeyboardHookService_OnTemporaryUiRevealPressed;
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
            BrowserView.CoreWebView2.Navigate(startUrl);
            AddressBarTextBox.Text = startUrl;
            StatusTextBlock.Text = "浏览器已就绪，登录态和缓存将自动保留。";
            _browserReady = true;
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"初始化失败: {ex.Message}";
        }
    }

    private async void BrowserView_OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!_browserReady || BrowserView.CoreWebView2 is null)
        {
            return;
        }

        var url = BrowserView.Source?.ToString() ?? BrowserView.CoreWebView2.Source;
        AddressBarTextBox.Text = url;
        _settings.LastUrl = url;
        await _settingsService.SaveAsync(_settings);

        var title = BrowserView.CoreWebView2.DocumentTitle;
        await _historyService.AddEntryAsync(url, string.IsNullOrWhiteSpace(title) ? url : title);
        LoadHistoryUi();

        StatusTextBlock.Text = e.IsSuccess ? "页面已加载。按 K 控制视频播放/暂停，按 F8 切换固定/自由模式。" : $"加载失败: {e.WebErrorStatus}";
    }

    private void BrowserView_OnDocumentTitleChanged(object? sender, object e)
    {
        if (BrowserView.CoreWebView2 is null)
        {
            return;
        }

        Title = string.IsNullOrWhiteSpace(BrowserView.CoreWebView2.DocumentTitle)
            ? "Genshin Browser"
            : $"{BrowserView.CoreWebView2.DocumentTitle} - Genshin Browser";
    }

    private void KeyboardHookService_OnKPressed(object? sender, EventArgs e)
    {
        _ = Dispatcher.InvokeAsync(ToggleVideoPlaybackAsync);
    }

    private void KeyboardHookService_OnModeTogglePressed(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(ToggleWindowMode);
    }

    private void KeyboardHookService_OnTemporaryUiRevealPressed(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(ShowTemporaryUiIfNeeded);
    }

    private async Task ToggleVideoPlaybackAsync()
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
            StatusTextBlock.Text = result switch
            {
                "\"play\"" => "已播放。",
                "\"pause\"" => "已暂停。",
                "\"no-video\"" => "当前页面没有可控制的视频。",
                _ => "已发送播放控制命令。"
            };
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"播放控制失败: {ex.Message}";
        }
    }

    private void ToggleWindowMode()
    {
        _settings.WindowMode = _settings.WindowMode == WindowMode.Fixed ? WindowMode.Free : WindowMode.Fixed;

        if (_settings.WindowMode == WindowMode.Fixed)
        {
            SaveWindowBounds();
        }

        _windowModeService.ApplyMode(_settings.WindowMode);
        _temporaryUiVisible = false;
        _temporaryUiTimer.Stop();
        UpdateModeUi();
        _ = _settingsService.SaveAsync(_settings);
    }

    private void UpdateModeUi()
    {
        var isFixed = _settings.WindowMode == WindowMode.Fixed;
        var showPanels = !isFixed || _temporaryUiVisible;

        ModeTextBlock.Text = isFixed ? "固定模式" : "自由模式";
        ToggleModeButton.Content = isFixed ? "切到自由" : "切到固定";
        TopToolbarBorder.Visibility = showPanels ? Visibility.Visible : Visibility.Collapsed;
        AddressBarBorder.Visibility = showPanels ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanelBorder.Visibility = showPanels ? Visibility.Visible : Visibility.Collapsed;
        HistoryRowDefinition.Height = showPanels ? new GridLength(HistoryPanelHeight) : new GridLength(0);

        StatusTextBlock.Text = isFixed
            ? (_temporaryUiVisible ? "固定模式，工具区临时显示中。" : "固定模式已开启。按 F7 临时显示工具区，按 F8 返回自由模式。")
            : "自由模式。可以移动窗口、查看历史并修改地址。";
    }

    private void LoadHistoryUi()
    {
        _historyItems.Clear();

        foreach (var item in _historyService.GetEntries())
        {
            _historyItems.Add(new HistoryItemViewModel(item));
        }
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        SaveWindowBounds();
        _keyboardHookService.Dispose();
    }

    private void MainWindow_OnStateChanged(object? sender, EventArgs e)
    {
    }

    private void MainWindow_OnLocationOrSizeChanged(object? sender, EventArgs e)
    {
        if (_settings.WindowMode == WindowMode.Free)
        {
            SaveWindowBounds();
        }
    }

    private void TemporaryUiTimer_OnTick(object? sender, EventArgs e)
    {
        _temporaryUiTimer.Stop();
        _temporaryUiVisible = false;
        UpdateModeUi();
    }

    private void ToggleModeButton_OnClick(object sender, RoutedEventArgs e)
    {
        ToggleWindowMode();
    }

    private async void PlayPauseButton_OnClick(object sender, RoutedEventArgs e)
    {
        await ToggleVideoPlaybackAsync();
    }

    private void HomeButton_OnClick(object sender, RoutedEventArgs e)
    {
        NavigateTo(DefaultUrl);
    }

    private void GoButton_OnClick(object sender, RoutedEventArgs e)
    {
        NavigateTo(AddressBarTextBox.Text);
    }

    private void ReloadButton_OnClick(object sender, RoutedEventArgs e)
    {
        BrowserView.Reload();
    }

    private void AddressBarTextBox_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateTo(AddressBarTextBox.Text);
        }
    }

    private void HistoryListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HistoryListBox.SelectedItem is HistoryItemViewModel item)
        {
            NavigateTo(item.Url);
        }
    }

    private void NavigateTo(string? input)
    {
        if (BrowserView.CoreWebView2 is null || string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        var target = input.Trim();
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            target = $"https://{target}";
        }

        BrowserView.CoreWebView2.Navigate(target);
    }

    private void ShowTemporaryUiIfNeeded()
    {
        if (_settings.WindowMode != WindowMode.Fixed)
        {
            return;
        }

        _temporaryUiVisible = true;
        _temporaryUiTimer.Stop();
        _temporaryUiTimer.Start();
        UpdateModeUi();
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

    private sealed class HistoryItemViewModel
    {
        public HistoryItemViewModel(HistoryEntry item)
        {
            Url = item.Url;
            DisplayText = $"{item.Title} ({item.VisitedAt:MM-dd HH:mm})";
        }

        public string Url { get; }

        public string DisplayText { get; }
    }
}
