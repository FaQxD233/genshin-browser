using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using GenshinBrowser.Constants;
using GenshinBrowser.Models;
using GenshinBrowser.Services;
using GenshinBrowser.Windows;
using Microsoft.Web.WebView2.Core;
using Application = System.Windows.Application;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace GenshinBrowser;

public partial class MainWindow : Window, IControlBrowser
{
    private readonly SettingsService _settingsService;
    private readonly HistoryService _historyService;
    private readonly FavoritesService _favoritesService;
    private readonly WindowModeService _windowModeService;
    private readonly KeyboardHookService _keyboardHookService;
    private readonly DownloadsService _downloadsService = new();

    private AppSettings _settings = new();
    private bool _browserReady;
    private bool _isShuttingDown;
    private bool _isRealClose;
    private bool _isNavigating;
    /// <summary>
    /// 为 false 时忽略 Location/SizeChanged 对边界的写回，
    /// 防止启动默认坐标在配置恢复前覆盖 settings.json。
    /// </summary>
    private bool _persistWindowBounds;
    private string _currentAddress = string.Empty;
    private string _statusMessage = LocalizationService.Get("Status.InitBrowser", "正在初始化浏览器...");
    private StatusLevel _lastStatusLevel = StatusLevel.Info;
    private ControlWindow? _controlWindow;
    private CancellationTokenSource? _settingsSaveCts;

    // 最大化状态：透明无边框窗口不能用 WindowState.Maximized（会覆盖任务栏），
    // 这里用手工记录工作区矩形并保存还原前的边界。
    private bool _isMaximized;
    private Rect _savedBounds;

    // 标题栏：浏览模式常驻；浮窗模式自动隐藏（顶部感应唤出）。
    // 显示时窗口向下 +30，不挤占 WebView 内容高度。
    // 内容区高度是尺寸真源：标题栏显隐只做 Height = content ± 30 的绝对赋值，
    // 禁止用 ActualHeight 累加减，否则 DPI/布局取整会每次漂移 1px。
    private bool _isTitleBarVisible = true;
    private bool _adjustingTitleBarBounds;
    private double _contentAreaHeight = 370;
    private DispatcherTimer? _titleBarHideTimer;
    private DispatcherTimer? _modeToastTimer;

    // 主窗移动/缩放时，控制窗尺寸显示与跟随位置的 UI 防抖
    private DispatcherTimer? _windowBoundsUiDebounceTimer;

    public event EventHandler? BrowserStateChanged;
    public event EventHandler? ZoomChanged;
    public event EventHandler? DownloadsChanged;

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

        // 在窗口首次显示前同步加载并恢复边界，避免先以默认位置出现再跳、
        // 以及 Loaded 异步期间默认坐标被错误写回 settings.json。
        _settings = _settingsService.Load();
        RestoreWindowBounds();
        ThemeService.Apply(_settings.ThemeMode);
        LocalizationService.Apply(_settings.Language);
        ApplyWindowOpacity(_settings.WindowOpacity);
        _windowModeService.ApplyMode(_settings.WindowMode);
        UpdateModeToggleButton();

        Loaded += MainWindow_OnLoaded;
        Closing += MainWindow_OnClosing;
        DragBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.Source is System.Windows.Controls.Button) return;
            if (e.ClickCount >= 2)
            {
                ToggleMaximize();
                return;
            }
            if (_isMaximized)
            {
                // 最大化时拖动标题栏 → 还原并跟随鼠标
                RestoreFromMaximizeOnDrag(e);
                return;
            }
            DragMove();
        };
        PreviewKeyDown += MainWindow_OnPreviewKeyDown;
        LocationChanged += MainWindow_OnLocationOrSizeChanged;
        SizeChanged += MainWindow_OnLocationOrSizeChanged;
        SourceInitialized += MainWindow_OnSourceInitialized;
        ContentRendered += MainWindow_OnContentRendered;
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

    public double ZoomFactor
    {
        get
        {
            // WebView2 初始化前访问 ZoomFactor 会抛异常；控制窗 VM 构造时就会读一次。
            try
            {
                return BrowserView.CoreWebView2 is null ? 1.0 : BrowserView.ZoomFactor;
            }
            catch
            {
                return 1.0;
            }
        }
        set => SetZoom(value);
    }

    public ObservableCollection<DownloadItem> Downloads => _downloadsService.Downloads;

    public string CurrentAddress => _currentAddress;

    public string StatusMessage => _statusMessage;

    public StatusLevel LastStatusLevel => _lastStatusLevel;

    public bool CanGoBack => BrowserView.CoreWebView2?.CanGoBack ?? false;

    public bool CanGoForward => BrowserView.CoreWebView2?.CanGoForward ?? false;

    public bool IsNavigating => _isNavigating;

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

    private void MainWindow_OnSourceInitialized(object? sender, EventArgs e)
    {
        // HWND / DPI 已就绪：再 clamp 一次，确保高分屏上位置不偏移
        RestoreWindowBounds();
    }

    private void MainWindow_OnContentRendered(object? sender, EventArgs e)
    {
        // 首帧渲染完成后再允许持久化边界，避免启动过程中的默认坐标写回磁盘
        _persistWindowBounds = true;
        ContentRendered -= MainWindow_OnContentRendered;
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
        // 配置已在构造函数同步加载并恢复；此处只接键盘钩子 / 控制窗 / WebView
        _statusMessage = LocalizationService.Get("Status.InitBrowser", "正在初始化浏览器...");

        _keyboardHookService.ToggleModeVk = KeyInterop.VirtualKeyFromKey(_settings.ToggleModeKey);
        _keyboardHookService.ToggleModeModifiers = _settings.ToggleModeModifiers;
        _keyboardHookService.TogglePlaybackVk = KeyInterop.VirtualKeyFromKey(_settings.TogglePlaybackKey);
        _keyboardHookService.TogglePlaybackModifiers = _settings.TogglePlaybackModifiers;
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

        // 跟踪应用前台状态：浏览模式下离开前台时禁用全局 K，避免影响其它软件输入
        Application.Current.Activated += App_OnActivated;
        Application.Current.Deactivated += App_OnDeactivated;
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "MainWindow_OnLoaded");
            System.Windows.MessageBox.Show(
                LocalizationService.Format("Status.StartupFailed", ex.GetType().Name, ex.Message),
                LocalizationService.Get("App.Title", "Genshin Browser"),
                MessageBoxButton.OK);
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

        _statusMessage = LocalizationService.Get("Status.InstallingWebView2", "正在安装 WebView2 Runtime...");
        BrowserStateChanged?.Invoke(this, EventArgs.Empty);
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = bootstrapperPath,
            Arguments = "/silent",
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("无法启动 WebView2 安装程序。");

        await process.WaitForExitAsync();

        if (!IsWebView2RuntimeInstalled())
            throw new InvalidOperationException(LocalizationService.Get("Status.WebView2InstallFailed", "WebView2 Runtime 自动安装失败，请手动安装。"));
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
            // CompositionControl + 透明浮窗必须用真正的 Transparent，非 0 alpha 会导致白屏/合成失败。
            // 光标问题用窗口近透明背景解决，切勿把 DefaultBackgroundColor 改成 Alpha>0。
            BrowserView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            ApplyWindowOpacity(_settings.WindowOpacity);

            // 页面初始化脚本：透明背景 + 取消播放器 cursor:none
            await BrowserView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(PageBootstrapScript);

            // ForceCursor 已保证可见箭头；再兜底拦截 null/None
            BrowserView.Cursor = System.Windows.Input.Cursors.Arrow;
            var cursorDescriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                FrameworkElement.CursorProperty, typeof(FrameworkElement));
            cursorDescriptor?.AddValueChanged(BrowserView, (_, _) =>
            {
                if (BrowserView.Cursor is null ||
                    ReferenceEquals(BrowserView.Cursor, System.Windows.Input.Cursors.None))
                {
                    BrowserView.Cursor = System.Windows.Input.Cursors.Arrow;
                }
            });

            BrowserView.NavigationStarting += BrowserView_OnNavigationStarting;
            BrowserView.NavigationCompleted += BrowserView_OnNavigationCompleted;
            BrowserView.CoreWebView2.DocumentTitleChanged += BrowserView_OnDocumentTitleChanged;
            BrowserView.CoreWebView2.SourceChanged += BrowserView_OnSourceChanged;
            BrowserView.CoreWebView2.NewWindowRequested += BrowserView_OnNewWindowRequested;
            BrowserView.CoreWebView2.HistoryChanged += BrowserView_OnHistoryChanged;
            BrowserView.CoreWebView2.DownloadStarting += BrowserView_OnDownloadStarting;

            var startUrl = GetStartupUrl(_settings.LastUrl);
            _currentAddress = startUrl;
            SetNavigating(true);
            BrowserView.CoreWebView2.Navigate(startUrl);
            // 对初始已加载的页面补执行一次（文档创建脚本只作用于之后的导航）
            _ = BrowserView.CoreWebView2.ExecuteScriptAsync(PageBootstrapScript);
            // 按当前窗口模式应用标题栏（浏览常驻 / 浮窗隐藏）
            ApplyTitleBarForCurrentMode(forceHideInFixed: true);
            SetStatusMessage(LocalizationService.Get("Status.BrowserReady"), StatusLevel.Success);
            _browserReady = true;
            UpdateWindowTitle();
            RefreshControlWindow();
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Initialize browser");
            SetNavigating(false);
            SetStatusMessage(LocalizationService.Format("Status.InitFailed", ex.Message), StatusLevel.Error);
        }
    }

    private void BrowserView_OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.IsRedirected)
        {
            return;
        }

        SetNavigating(true);
    }

    private async void BrowserView_OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!_browserReady || BrowserView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            SetNavigating(false);

            // 使用当前 Source 而不是 _currentAddress，避免竞态条件
            if (e.IsSuccess)
            {
                var currentUrl = BrowserView.CoreWebView2.Source;
                var title = BrowserView.CoreWebView2.DocumentTitle;
                await _historyService.AddEntryAsync(currentUrl, string.IsNullOrWhiteSpace(title) ? currentUrl : title);
                // SPA / 文档创建脚本漏绑时补一次
                _ = BrowserView.CoreWebView2.ExecuteScriptAsync(PageBootstrapScript);
            }

            if (e.IsSuccess)
            {
                SetStatusMessage(LocalizationService.Get("Status.PageLoaded"), StatusLevel.Success);
            }
            else
            {
                SetStatusMessage(LocalizationService.Format("Status.LoadFailed", e.WebErrorStatus), StatusLevel.Error);
            }

            RefreshControlWindow();
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Navigation completed handling");
            SetNavigating(false);
            SetStatusMessage(LocalizationService.Format("Status.RecordStateFailed", ex.Message), StatusLevel.Error);
        }
    }

    private void BrowserView_OnDocumentTitleChanged(object? sender, object e)
    {
        UpdateWindowTitle();
    }

    private void BrowserView_OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        // 当页面 URL 改变时（包括用户点击链接、重定向、B 站 SPA 切集等），立即更新地址栏与 LastUrl
        if (!_browserReady || BrowserView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            if (CaptureCurrentUrlToSettings())
            {
                QueueSettingsSave();
                RefreshControlWindow();
                // SPA（如 B 站分 P / 相关推荐）不一定触发完整 NavigationCompleted，补记历史
                _ = RecordHistoryForCurrentSourceAsync();
            }
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Source changed handling");
            SetStatusMessage(LocalizationService.Format("Status.AddressUpdateFailed", ex.Message), StatusLevel.Error);
        }
    }

    /// <summary>
    /// 将 WebView 当前 Source 同步到内存中的地址与 LastUrl。返回是否发生了变化。
    /// </summary>
    private bool CaptureCurrentUrlToSettings()
    {
        if (BrowserView.CoreWebView2 is null)
        {
            return false;
        }

        try
        {
            var newUrl = BrowserView.CoreWebView2.Source;
            if (string.IsNullOrWhiteSpace(newUrl))
            {
                return false;
            }

            if (string.Equals(_currentAddress, newUrl, StringComparison.Ordinal)
                && string.Equals(_settings.LastUrl, newUrl, StringComparison.Ordinal))
            {
                return false;
            }

            _currentAddress = newUrl;
            _settings.LastUrl = newUrl;
            return true;
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Capture current URL");
            return false;
        }
    }

    private async Task RecordHistoryForCurrentSourceAsync()
    {
        if (!_browserReady || BrowserView.CoreWebView2 is null || _isShuttingDown)
        {
            return;
        }

        try
        {
            var currentUrl = BrowserView.CoreWebView2.Source;
            if (string.IsNullOrWhiteSpace(currentUrl))
            {
                return;
            }

            // 给 SPA 一点时间更新 document.title
            await Task.Delay(400).ConfigureAwait(true);
            if (_isShuttingDown || BrowserView.CoreWebView2 is null)
            {
                return;
            }

            // 若这 400ms 内又切到了别的地址，丢弃这次记录（由新的 SourceChanged 负责）
            if (!string.Equals(BrowserView.CoreWebView2.Source, currentUrl, StringComparison.Ordinal))
            {
                return;
            }

            var title = BrowserView.CoreWebView2.DocumentTitle;
            await _historyService.AddEntryAsync(currentUrl, string.IsNullOrWhiteSpace(title) ? currentUrl : title)
                .ConfigureAwait(true);
            if (!_isShuttingDown)
            {
                RefreshControlWindow();
            }
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Record history for source change");
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

    private void MainWindow_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        // Ctrl+= / Ctrl+(numpad +)：放大
        if (e.Key == Key.OemPlus || e.Key == Key.Add)
        {
            e.Handled = true;
            ZoomBy(0.1);
        }
        // Ctrl+- / Ctrl-(numpad -)：缩小
        else if (e.Key == Key.OemMinus || e.Key == Key.Subtract)
        {
            e.Handled = true;
            ZoomBy(-0.1);
        }
        // Ctrl+0 / Ctrl+(numpad 0)：重置缩放
        else if (e.Key == Key.D0 || e.Key == Key.NumPad0)
        {
            e.Handled = true;
            SetZoom(1.0);
        }
    }

    private void BrowserView_OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        try
        {
            var operation = e.DownloadOperation;
            var filePath = e.ResultFilePath ?? string.Empty;
            var fileName = !string.IsNullOrEmpty(filePath) ? Path.GetFileName(filePath) : LocalizationService.Get("Downloads.DefaultFileName", "下载文件");

            var item = new DownloadItem
            {
                FileName = fileName,
                FilePath = filePath,
                TotalBytes = operation.TotalBytesToReceive > 0 ? (long)operation.TotalBytesToReceive : 0,
                ReceivedBytes = (long)operation.BytesReceived,
            };

            _downloadsService.Track(item, operation);

            operation.BytesReceivedChanged += (_, _) => OnDownloadProgress(item, operation);
            operation.StateChanged += (_, _) => OnDownloadStateChanged(item, operation);

            SetStatusMessage(LocalizationService.Format("Status.DownloadStarted", item.FileName), StatusLevel.Info);
            DownloadsChanged?.Invoke(this, EventArgs.Empty);
            RefreshControlWindow();
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "DownloadStarting");
        }
    }

    private void OnDownloadProgress(DownloadItem item, CoreWebView2DownloadOperation operation)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => OnDownloadProgress(item, operation));
            return;
        }

        item.ReceivedBytes = (long)operation.BytesReceived;
        if (operation.TotalBytesToReceive > 0)
        {
            item.TotalBytes = (long)operation.TotalBytesToReceive;
        }
        DownloadsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDownloadStateChanged(DownloadItem item, CoreWebView2DownloadOperation operation)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => OnDownloadStateChanged(item, operation));
            return;
        }

        switch (operation.State)
        {
            case CoreWebView2DownloadState.Completed:
                _downloadsService.MarkCompleted(item);
                SetStatusMessage(LocalizationService.Format("Status.DownloadCompleted", item.FileName), StatusLevel.Success);
                break;
            case CoreWebView2DownloadState.Interrupted:
                _downloadsService.MarkInterrupted(item);
                SetStatusMessage(LocalizationService.Format("Status.DownloadInterrupted", item.FileName), StatusLevel.Warning);
                break;
        }

        DownloadsChanged?.Invoke(this, EventArgs.Empty);
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
            SetStatusMessage(LocalizationService.Get("Status.BrowserNotReady"), StatusLevel.Warning);
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
            switch (result)
            {
                case "\"play\"":
                    SetStatusMessage(LocalizationService.Get("Status.Played"), StatusLevel.Success);
                    break;
                case "\"pause\"":
                    SetStatusMessage(LocalizationService.Get("Status.Paused"), StatusLevel.Success);
                    break;
                case "\"no-video\"":
                    SetStatusMessage(LocalizationService.Get("Status.NoVideo"), StatusLevel.Warning);
                    break;
                default:
                    SetStatusMessage(LocalizationService.Get("Status.PlaybackCommandSent"), StatusLevel.Info);
                    break;
            }
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Toggle video playback");
            SetStatusMessage(LocalizationService.Format("Status.PlaybackFailed", ex.Message), StatusLevel.Error);
        }
    }

    public void ToggleWindowMode()
    {
        SetWindowMode(_settings.WindowMode == WindowMode.Fixed ? WindowMode.Free : WindowMode.Fixed);
    }

    public void SetWindowMode(WindowMode mode)
    {
        if (_settings.WindowMode == mode)
        {
            return;
        }

        // 切换前先记下当前位置/尺寸，浏览/浮窗共用同一组边界
        SaveWindowBounds();

        _settings.WindowMode = mode;

        _windowModeService.ApplyMode(_settings.WindowMode);
        _keyboardHookService.IsGamingMode = _settings.WindowMode == WindowMode.Fixed;
        // 浏览：标题栏常驻；浮窗：自动隐藏（向下延长策略不改内容区高度）
        ApplyTitleBarForCurrentMode(forceHideInFixed: true);
        UpdateModeToggleButton();

        var enteringFloating = _settings.WindowMode == WindowMode.Fixed;
        // 首次进入浮窗：较长引导；之后仅短 toast + 描边闪
        if (enteringFloating && !_settings.HasSeenFloatingModeHint)
        {
            _settings.HasSeenFloatingModeHint = true;
            ShowMainModeToast(
                LocalizationService.Get("Mode.FirstFloatingHint", "已进入浮窗：置顶并隐藏控制台。按 F8 返回浏览；移到窗口顶部可显示标题栏。"),
                TimeSpan.FromSeconds(3.2));
            SetStatusMessage(LocalizationService.Get("Mode.FixedOn"), StatusLevel.Success);
        }
        else
        {
            ShowMainModeToast(
                enteringFloating
                    ? LocalizationService.Get("Mode.ToastFloating", "浮窗")
                    : LocalizationService.Get("Mode.ToastBrowsing", "浏览"),
                TimeSpan.FromSeconds(1.1));
            SetStatusMessage(
                enteringFloating
                    ? LocalizationService.Get("Mode.FixedOn")
                    : LocalizationService.Get("Mode.FreeOn"),
                StatusLevel.Info);
        }

        FlashModeBorder(enteringFloating);
        UpdateControlWindowVisibility();
        RefreshControlWindow();
        UpdateWindowTitle();
        QueueSettingsSave();
    }

    public void ReloadPage()
    {
        if (BrowserView.CoreWebView2 is null)
        {
            SetStatusMessage(LocalizationService.Get("Status.BrowserNotReady"), StatusLevel.Warning);
            return;
        }

        try
        {
            SetNavigating(true);
            BrowserView.Reload();
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Reload page");
            SetNavigating(false);
            SetStatusMessage(LocalizationService.Format("Status.ReloadFailed", ex.Message), StatusLevel.Error);
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
            SetStatusMessage(LocalizationService.Get("Status.FavoriteAdded"), StatusLevel.Success);
            RefreshControlWindow();
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Add current page to favorites");
            SetStatusMessage(LocalizationService.Format("Status.FavoriteAddFailed", ex.Message), StatusLevel.Error);
        }
    }

    public async Task RemoveFavoriteAsync(string url)
    {
        try
        {
            await _favoritesService.RemoveAsync(url);
            SetStatusMessage(LocalizationService.Get("Status.FavoriteRemoved"), StatusLevel.Success);
            RefreshControlWindow();
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Remove favorite");
            SetStatusMessage(LocalizationService.Format("Status.FavoriteRemoveFailed", ex.Message), StatusLevel.Error);
        }
    }

    public bool IsFavorite(string url)
    {
        return !string.IsNullOrWhiteSpace(url) && _favoritesService.Contains(url);
    }

    public async Task RemoveHistoryEntryAsync(string url)
    {
        try
        {
            await _historyService.RemoveAsync(url);
            SetStatusMessage(LocalizationService.Get("Status.HistoryRemoved"), StatusLevel.Success);
            RefreshControlWindow();
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Remove history entry");
            SetStatusMessage(LocalizationService.Format("Status.HistoryRemoveFailed", ex.Message), StatusLevel.Error);
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
            SetStatusMessage(LocalizationService.Get("Status.OnlyHttp"), StatusLevel.Warning);
            return;
        }

        try
        {
            SetNavigating(true);
            BrowserView.CoreWebView2.Navigate(target);
            _currentAddress = target;
            SetStatusMessage(LocalizationService.Format("Status.Opening", target), StatusLevel.Info);
            RefreshControlWindow();
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Navigate to target");
            SetNavigating(false);
            SetStatusMessage(LocalizationService.Format("Status.OpenFailed", ex.Message), StatusLevel.Error);
        }
    }

    public void RestoreDefaultSettings()
    {
        WindowOpacity = 1.0;
        ApplyWindowOpacity(1.0);
        ToggleModeKey = Key.F8;
        ToggleModeModifiers = ModifierKeys.None;
        TogglePlaybackKey = Key.K;
        TogglePlaybackModifiers = ModifierKeys.None;
        SetZoom(1.0);
        QueueSettingsSave();
    }

    public string ThemeMode
    {
        get => ThemeService.Normalize(_settings.ThemeMode);
        set
        {
            var mode = ThemeService.Normalize(value);
            if (string.Equals(_settings.ThemeMode, mode, StringComparison.OrdinalIgnoreCase))
            {
                ThemeService.Apply(mode);
                return;
            }

            _settings.ThemeMode = mode;
            ThemeService.Apply(mode);
            QueueSettingsSave();
        }
    }

    public string UiLanguage
    {
        get => LocalizationService.Normalize(_settings.Language);
        set
        {
            var lang = LocalizationService.Normalize(value);
            if (string.Equals(_settings.Language, lang, StringComparison.OrdinalIgnoreCase))
            {
                LocalizationService.Apply(lang);
                return;
            }

            _settings.Language = lang;
            LocalizationService.Apply(lang);
            QueueSettingsSave();
        }
    }

    public double BrowserWindowWidth
    {
        get => ActualWidth > 0 ? ActualWidth : Width;
        set => ApplyBrowserWindowSize(value, BrowserWindowHeight);
    }

    public double BrowserWindowHeight
    {
        // 控制面板显示的是内容区高度（不含临时加高的标题栏）
        get => GetContentAreaHeight();
        set => ApplyBrowserWindowSize(BrowserWindowWidth, value);
    }

    public void MoveBrowserToCorner(string corner)
    {
        if (_isMaximized)
        {
            ToggleMaximize();
        }

        var workArea = WindowBoundsHelper.GetWorkArea(this);
        var key = (corner ?? string.Empty).Trim();
        // 贴边：无空隙
        var left = workArea.Left;
        var top = workArea.Top;
        switch (key)
        {
            case "TopRight":
                left = workArea.Right - Width;
                top = workArea.Top;
                break;
            case "BottomLeft":
                left = workArea.Left;
                top = workArea.Bottom - Height;
                break;
            case "BottomRight":
                left = workArea.Right - Width;
                top = workArea.Bottom - Height;
                break;
            default:
                // TopLeft
                left = workArea.Left;
                top = workArea.Top;
                break;
        }

        Left = left;
        Top = top;
        WindowBoundsHelper.ClampToWorkArea(this, workArea);
        SaveWindowBounds();
        QueueSettingsSave();
        RefreshControlWindow();

        var toastKey = key switch
        {
            "TopRight" => "Toast.MovedTopRight",
            "BottomLeft" => "Toast.MovedBottomLeft",
            "BottomRight" => "Toast.MovedBottomRight",
            _ => "Toast.MovedTopLeft",
        };
        var fallback = key switch
        {
            "TopRight" => "已移动到右上角",
            "BottomLeft" => "已移动到左下角",
            "BottomRight" => "已移动到右下角",
            _ => "已移动到左上角",
        };
        SetStatusMessage(LocalizationService.Get(toastKey, fallback), StatusLevel.Success);
    }

    private void ApplyBrowserWindowSize(double width, double height)
    {
        if (_isMaximized)
        {
            ToggleMaximize();
        }

        var workArea = WindowBoundsHelper.GetWorkArea(this);
        var minWidth = MinWidth > 0 ? MinWidth : 640;
        var minHeight = MinHeight > 0 ? MinHeight : 360;
        var maxWidth = Math.Max(minWidth, workArea.Width);
        // height 参数是内容区高度；标题栏显示时窗口再 +30
        var contentHeight = Math.Clamp(height, minHeight, Math.Max(minHeight, workArea.Height));
        _contentAreaHeight = contentHeight;
        var windowHeight = contentHeight + (_isTitleBarVisible ? TitleBarExpandedHeight : 0);
        var maxHeight = Math.Max(minHeight, workArea.Height);

        _adjustingTitleBarBounds = true;
        try
        {
            Width = Math.Clamp(width, minWidth, maxWidth);
            Height = Math.Clamp(windowHeight, minHeight, maxHeight);
            WindowBoundsHelper.ClampToWorkArea(this, workArea);
        }
        finally
        {
            _adjustingTitleBarBounds = false;
        }

        SaveWindowBounds();
        QueueSettingsSave();
        RefreshControlWindow();
    }

    /// <summary>
    /// 内容区（WebView）高度：优先用内部真源，避免 ActualHeight 取整误差。
    /// </summary>
    private double GetContentAreaHeight()
    {
        if (_contentAreaHeight > 0)
        {
            return _contentAreaHeight;
        }

        var height = Height > 0 ? Height : ActualHeight;
        if (_isTitleBarVisible)
        {
            height = Math.Max(MinHeight, height - TitleBarExpandedHeight);
        }

        return height;
    }

    /// <summary>
    /// 用户拖拽/设置改尺寸后，把内容区高度同步为真源。
    /// 标题栏程序化加减高度时不要调用。
    /// </summary>
    private void CaptureContentAreaHeightFromWindow()
    {
        var windowHeight = ActualHeight > 0 ? ActualHeight : Height;
        if (windowHeight <= 0)
        {
            return;
        }

        _contentAreaHeight = _isTitleBarVisible
            ? Math.Max(MinHeight, windowHeight - TitleBarExpandedHeight)
            : Math.Max(MinHeight, windowHeight);
    }

    public void CancelDownload(DownloadItem item)
    {
        if (_downloadsService.TryCancel(item))
        {
            SetStatusMessage(LocalizationService.Format("Status.DownloadCanceled", item.FileName), StatusLevel.Success);
            DownloadsChanged?.Invoke(this, EventArgs.Empty);
            RefreshControlWindow();
        }
    }

    public void OpenDownloadFile(DownloadItem item)
    {
        if (!_downloadsService.OpenFile(item))
        {
            SetStatusMessage(LocalizationService.Get("Status.CannotOpenFile"), StatusLevel.Warning);
            RefreshControlWindow();
        }
    }

    public void OpenDownloadFolder(DownloadItem item)
    {
        if (!_downloadsService.OpenFolder(item))
        {
            SetStatusMessage(LocalizationService.Get("Status.CannotOpenFolder"), StatusLevel.Warning);
            RefreshControlWindow();
        }
    }

    public void ClearFinishedDownloads()
    {
        _downloadsService.ClearFinished();
        DownloadsChanged?.Invoke(this, EventArgs.Empty);
        RefreshControlWindow();
    }

    private void ModeToggleButton_OnClick(object sender, RoutedEventArgs e) => ToggleWindowMode();

    private void MinButton_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxButton_OnClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    /// <summary>
    /// 同步标题栏模式按钮图标与动作型 tooltip：浮窗=锁，浏览=解锁。
    /// </summary>
    private void UpdateModeToggleButton()
    {
        if (ModeToggleIcon is not null)
        {
            // 浮窗 E785(锁)，浏览 E718(解锁) — 与 ControlWindowViewModel 一致
            ModeToggleIcon.Text = _settings.WindowMode == WindowMode.Fixed ? "" : "";
        }

        if (ModeToggleButton is not null)
        {
            // 动作型 tooltip：写清点下去会进入哪一侧
            ModeToggleButton.ToolTip = _settings.WindowMode == WindowMode.Fixed
                ? LocalizationService.Get("Mode.SwitchToBrowsingTooltip", "返回浏览（显示控制台）(F8)")
                : LocalizationService.Get("Mode.SwitchToFloatingTooltip", "切换到浮窗（置顶，隐藏控制台）(F8)");
        }
    }

    private void ShowMainModeToast(string message, TimeSpan duration)
    {
        if (ModeToastBorder is null || ModeToastText is null)
        {
            return;
        }

        ModeToastText.Text = message;
        ModeToastBorder.Visibility = Visibility.Visible;

        var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };
        var slide = new System.Windows.Media.Animation.DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };
        ModeToastBorder.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        if (ModeToastBorder.RenderTransform is System.Windows.Media.TranslateTransform tt)
        {
            tt.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slide);
        }

        _modeToastTimer ??= new DispatcherTimer();
        _modeToastTimer.Tick -= ModeToastTimer_OnTick;
        _modeToastTimer.Tick += ModeToastTimer_OnTick;
        _modeToastTimer.Interval = duration;
        _modeToastTimer.Stop();
        _modeToastTimer.Start();
    }

    private void ModeToastTimer_OnTick(object? sender, EventArgs e)
    {
        _modeToastTimer?.Stop();
        if (ModeToastBorder is null)
        {
            return;
        }

        var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160));
        fadeOut.Completed += (_, _) =>
        {
            if (ModeToastBorder is not null)
            {
                ModeToastBorder.Visibility = Visibility.Collapsed;
            }
        };
        ModeToastBorder.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    /// <summary>
    /// 模式切换时主窗边缘短暂描边闪（浏览蓝 / 浮窗橙）。
    /// </summary>
    private void FlashModeBorder(bool floating)
    {
        if (ModeFlashBorder is null)
        {
            return;
        }

        var color = floating
            ? System.Windows.Media.Color.FromRgb(0xF0, 0xA0, 0x20)
            : System.Windows.Media.Color.FromRgb(0x2F, 0x81, 0xF7);
        ModeFlashBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(color);

        var anim = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(0.95, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90)))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        });
        anim.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(720)))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
        });
        ModeFlashBorder.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void ToggleMaximize()
    {
        if (!_isMaximized)
        {
            _savedBounds = new Rect(Left, Top, Width, Height);
            var work = SystemParameters.WorkArea;
            Left = work.Left;
            Top = work.Top;
            Width = work.Width;
            Height = work.Height;
            _isMaximized = true;
            MaxIcon.Text = "\uE73F";
        }
        else
        {
            Left = _savedBounds.Left;
            Top = _savedBounds.Top;
            Width = _savedBounds.Width;
            Height = _savedBounds.Height;
            _isMaximized = false;
            MaxIcon.Text = "\uE740";
        }
    }

    private void RestoreFromMaximizeOnDrag(MouseButtonEventArgs e)
    {
        // 拖动最大化窗口标题栏时还原窗口尺寸，并让窗口跟随鼠标
        var work = SystemParameters.WorkArea;
        var ratio = e.GetPosition(this).X / ActualWidth;
        ToggleMaximize();
        Left = e.GetPosition(null).X - _savedBounds.Width * ratio;
        Top = e.GetPosition(null).Y - SystemParameters.CaptionHeight / 2.0;
        Left = Math.Max(work.Left, Math.Min(Left, work.Right - Width));
        Top = Math.Max(work.Top, Math.Min(Top, work.Bottom - Height));
        DragMove();
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

        if (_windowBoundsUiDebounceTimer is not null)
        {
            _windowBoundsUiDebounceTimer.Stop();
            _windowBoundsUiDebounceTimer.Tick -= WindowBoundsUiDebounceTimer_OnTick;
            _windowBoundsUiDebounceTimer = null;
        }

        // 取消事件订阅
        LocationChanged -= MainWindow_OnLocationOrSizeChanged;
        SizeChanged -= MainWindow_OnLocationOrSizeChanged;

        // 立即关闭控制窗口（不等待保存）
        if (_controlWindow is not null)
        {
            _controlWindow.AllowClose = true;
            _controlWindow.Close();
        }

        // 保存窗口位置到内存（最大化时保留还原前的边界）
        SaveWindowBounds();

        try
        {
            // 退出前强制用当前页面 URL 刷新 LastUrl，避免 SPA 导航 / 防抖取消导致恢复到旧地址
            CaptureCurrentUrlToSettings();

            // 取消事件订阅
            _titleBarHideTimer?.Stop();
            if (_titleBarHideTimer is not null)
            {
                _titleBarHideTimer.Tick -= TitleBarHideTimer_OnTick;
            }

            if (BrowserView.CoreWebView2 is not null)
            {
                BrowserView.NavigationCompleted -= BrowserView_OnNavigationCompleted;
                BrowserView.CoreWebView2.DocumentTitleChanged -= BrowserView_OnDocumentTitleChanged;
                BrowserView.CoreWebView2.SourceChanged -= BrowserView_OnSourceChanged;
                BrowserView.CoreWebView2.NewWindowRequested -= BrowserView_OnNewWindowRequested;
                BrowserView.CoreWebView2.HistoryChanged -= BrowserView_OnHistoryChanged;
                BrowserView.CoreWebView2.DownloadStarting -= BrowserView_OnDownloadStarting;
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
        // 启动恢复 / 最大化 / 标题栏扩展窗口期间不写回边界
        if (!_persistWindowBounds || _isMaximized || _isShuttingDown || _adjustingTitleBarBounds)
        {
            return;
        }

        // 用户拖拽/系统改尺寸：刷新内容区真源，再持久化
        CaptureContentAreaHeightFromWindow();

        // 浏览 / 浮窗模式都记住主窗位置与尺寸（落盘已有 QueueSettingsSave 防抖）
        SaveWindowBounds();
        QueueSettingsSave();

        // 控制窗宽高显示 / 跟随位置：防抖，避免拖动时每帧全量刷新列表
        QueueControlWindowBoundsUiRefresh();
    }

    private void QueueControlWindowBoundsUiRefresh()
    {
        if (_controlWindow is null || _isShuttingDown)
        {
            return;
        }

        _windowBoundsUiDebounceTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(AppConfig.Ui.WindowBoundsUiDebounceMs),
        };
        _windowBoundsUiDebounceTimer.Tick -= WindowBoundsUiDebounceTimer_OnTick;
        _windowBoundsUiDebounceTimer.Tick += WindowBoundsUiDebounceTimer_OnTick;
        _windowBoundsUiDebounceTimer.Stop();
        _windowBoundsUiDebounceTimer.Start();
    }

    private void WindowBoundsUiDebounceTimer_OnTick(object? sender, EventArgs e)
    {
        _windowBoundsUiDebounceTimer?.Stop();
        if (_isShuttingDown || _controlWindow is null)
        {
            return;
        }

        _controlWindow.RefreshWindowSizeDisplay();

        if (_settings.WindowMode == WindowMode.Free)
        {
            _controlWindow.ShowNearBrowserWindow();
        }
    }

    private void RestoreWindowBounds()
    {
        var width = _settings.WindowWidth > 0 ? _settings.WindowWidth : Width;
        // settings 存的是内容区高度；标题栏显示时窗口额外 +30
        var contentHeight = _settings.WindowHeight > 0 ? _settings.WindowHeight : GetContentAreaHeight();
        contentHeight = Math.Max(MinHeight > 0 ? MinHeight : 360, contentHeight);
        _contentAreaHeight = contentHeight;
        var showTitleBar = _settings.WindowMode == WindowMode.Free;
        var height = contentHeight + (showTitleBar ? TitleBarExpandedHeight : 0);
        var left = _settings.WindowLeft;
        var top = _settings.WindowTop;

        if (double.IsNaN(left) || double.IsInfinity(left))
        {
            left = 100;
        }

        if (double.IsNaN(top) || double.IsInfinity(top))
        {
            top = 100;
        }

        // 与将要应用的标题栏状态对齐，避免先用错高度再闪一下
        _isTitleBarVisible = showTitleBar;
        if (TitleBarRow is not null)
        {
            TitleBarRow.Height = new GridLength(showTitleBar ? TitleBarExpandedHeight : 0);
        }

        if (TitleBarHitZone is not null)
        {
            TitleBarHitZone.IsHitTestVisible = !showTitleBar;
        }

        Width = width;
        Height = height;
        Left = left;
        Top = top;
        WindowBoundsHelper.ClampToWorkArea(this);
    }

    private void SaveWindowBounds()
    {
        if (WindowState != WindowState.Normal || _isMaximized || _adjustingTitleBarBounds)
        {
            return;
        }

        // 优先用 Actual*（布局完成后更准）；未完成布局时回退到 Width/Height
        var width = ActualWidth > 0 ? ActualWidth : Width;
        if (width <= 0 || double.IsNaN(Left) || double.IsNaN(Top))
        {
            return;
        }

        // 持久化内容区高度真源，不用 ActualHeight 反推（会引入 DPI 取整漂移）
        var height = GetContentAreaHeight();
        if (height <= 0)
        {
            return;
        }

        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settings.WindowWidth = width;
        _settings.WindowHeight = height;
    }

    private void StartKeyboardHook()
    {
        if (!_keyboardHookService.Start(out var errorCode))
        {
            SetStatusMessage(LocalizationService.Format("Status.HotkeyInstallFailed", errorCode), StatusLevel.Error);
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
                SetStatusMessage(LocalizationService.Format("Status.SaveSettingsFailed", ex.Message), StatusLevel.Error);
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

    private void SetStatusMessage(string message, StatusLevel level = StatusLevel.Info)
    {
        _statusMessage = message;
        _lastStatusLevel = level;
        _controlWindow?.RefreshFromBrowser();
    }

    private void SetNavigating(bool isNavigating)
    {
        if (_isNavigating == isNavigating)
        {
            return;
        }

        _isNavigating = isNavigating;
        RefreshControlWindow();
    }

    private void UpdateWindowTitle()
    {
        var documentTitle = BrowserView.CoreWebView2 is null || string.IsNullOrWhiteSpace(BrowserView.CoreWebView2.DocumentTitle)
            ? null
            : BrowserView.CoreWebView2.DocumentTitle;

        var pageTitle = documentTitle is null
            ? "Genshin Browser"
            : $"{documentTitle} - Genshin Browser";

        Title = pageTitle;

        // 标题栏内显示短标题，避免被右侧按钮挤掉
        if (TitleBarTitleText is not null)
        {
            TitleBarTitleText.Text = documentTitle ?? LocalizationService.Get("App.Title", "Genshin Browser");
        }
    }

    private const double TitleBarExpandedHeight = 30;

    /// <summary>
    /// 注入到每个页面：透明背景 + 取消播放器 cursor:none。
    /// </summary>
    private const string PageBootstrapScript = """
(() => {
  try {
    if (!document.getElementById('gb-page-bootstrap-style')) {
      const s = document.createElement('style');
      s.id = 'gb-page-bootstrap-style';
      s.textContent = [
        'html,body{background:transparent !important;}',
        'video,video *{cursor:default !important;}',
        '.bpx-player-container,.bpx-player-video-wrap,.bpx-player-video-area,',
        '.bpx-player-video-perch,.bilibili-player,.bilibili-player-area,',
        '.bilibili-player-video-wrap,.bilibili-player-video-perch{cursor:default !important;}'
      ].join('');
      (document.documentElement || document.head || document.body).appendChild(s);
    }
  } catch (_) {}
})();
""";

    /// <summary>
    /// 按窗口模式应用标题栏：浏览常驻，浮窗默认隐藏。
    /// </summary>
    private void ApplyTitleBarForCurrentMode(bool forceHideInFixed)
    {
        if (_settings.WindowMode == WindowMode.Free)
        {
            _titleBarHideTimer?.Stop();
            SetTitleBarVisible(true);
            return;
        }

        // 浮窗模式：默认隐藏；若鼠标已在顶部带内则保持显示
        if (forceHideInFixed && !DragBar.IsMouseOver && !IsMouseInTitleBarBand())
        {
            _titleBarHideTimer?.Stop();
            SetTitleBarVisible(false);
            return;
        }

        if (!DragBar.IsMouseOver && !IsMouseInTitleBarBand())
        {
            ScheduleTitleBarAutoHide();
        }
    }

    private void TitleBarArea_OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // 进入感应区或标题栏：取消隐藏并显示
        _titleBarHideTimer?.Stop();
        SetTitleBarVisible(true);
    }

    private void TitleBarArea_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // 仅标题栏（DragBar）绑定 Leave。感应区不绑 Leave：
        // 显示时关掉感应区命中会伪触发 Leave，否则会立刻开隐藏计时导致抖动。
        if (DragBar.IsMouseOver)
        {
            return;
        }

        // 浏览模式：标题栏常驻，离开也不收回
        if (_settings.WindowMode == WindowMode.Free)
        {
            _titleBarHideTimer?.Stop();
            return;
        }

        ScheduleTitleBarAutoHide();
    }

    private void ScheduleTitleBarAutoHide()
    {
        if (_settings.WindowMode != WindowMode.Fixed)
        {
            return;
        }

        // 离开后 1 秒再收回：窗口高度减回，内容区尺寸不变
        _titleBarHideTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _titleBarHideTimer.Tick -= TitleBarHideTimer_OnTick;
        _titleBarHideTimer.Tick += TitleBarHideTimer_OnTick;
        _titleBarHideTimer.Stop();
        _titleBarHideTimer.Start();
    }

    private void TitleBarHideTimer_OnTick(object? sender, EventArgs e)
    {
        _titleBarHideTimer?.Stop();

        // 浏览模式始终常驻
        if (_settings.WindowMode == WindowMode.Free)
        {
            SetTitleBarVisible(true);
            return;
        }

        // 仍在标题栏，或仍停在顶部感应高度内，则保持显示
        if (DragBar.IsMouseOver || IsMouseInTitleBarBand())
        {
            return;
        }

        SetTitleBarVisible(false);
    }

    /// <summary>
    /// 鼠标是否仍在窗口顶部标题栏高度带内（与感应区同高）。
    /// </summary>
    private bool IsMouseInTitleBarBand()
    {
        try
        {
            var pos = System.Windows.Input.Mouse.GetPosition(this);
            return pos.Y >= 0 && pos.Y < TitleBarExpandedHeight
                   && pos.X >= 0 && pos.X <= ActualWidth;
        }
        catch
        {
            return false;
        }
    }

    private void SetTitleBarVisible(bool visible)
    {
        if (_isTitleBarVisible == visible)
        {
            // 常驻时感应区必须关闭；隐藏态必须打开，避免状态被外部改坏
            TitleBarHitZone.IsHitTestVisible = !visible;
            return;
        }

        var wasVisible = _isTitleBarVisible;
        _isTitleBarVisible = visible;
        // 显示时关掉感应区，避免挡住标题栏；隐藏后重新开启
        TitleBarHitZone.IsHitTestVisible = !visible;

        // 用行高占位显示标题栏；同时窗口 Height ±30，使 WebView 内容区高度不变
        TitleBarRow.Height = new GridLength(visible ? TitleBarExpandedHeight : 0);
        AdjustWindowHeightForTitleBar(visible, wasVisible);
    }

    /// <summary>
    /// 标题栏显示时窗口向下加高 30；隐藏时减回。不改 Top，因此是向下延长。
    /// 以内容区高度真源做绝对赋值：Height = content [+ 30]，禁止 ActualHeight ± 30 累加。
    /// 最大化时不改窗口外框（内容区已铺满工作区）。
    /// </summary>
    private void AdjustWindowHeightForTitleBar(bool nowVisible, bool wasVisible)
    {
        if (nowVisible == wasVisible || _isMaximized || WindowState != WindowState.Normal)
        {
            return;
        }

        if (_contentAreaHeight <= 0)
        {
            CaptureContentAreaHeightFromWindow();
        }

        var contentHeight = Math.Max(MinHeight > 0 ? MinHeight : 360, _contentAreaHeight);
        _contentAreaHeight = contentHeight;
        var targetHeight = contentHeight + (nowVisible ? TitleBarExpandedHeight : 0);

        // 向下延长时避免超出工作区底部：必要时略微上移，优先保证内容完整
        var workArea = WindowBoundsHelper.GetWorkArea(this);
        var maxBottom = workArea.Bottom;
        var newBottom = Top + targetHeight;
        var newTop = Top;
        if (newBottom > maxBottom)
        {
            newTop = Math.Max(workArea.Top, maxBottom - targetHeight);
            // 工作区不够高时，只压缩窗口外框，不改写内容区真源（避免来回漂移）
            targetHeight = Math.Min(targetHeight, Math.Max(MinHeight, maxBottom - newTop));
        }

        _adjustingTitleBarBounds = true;
        try
        {
            if (Math.Abs(newTop - Top) > 0.1)
            {
                Top = newTop;
            }

            // 与当前 Height 相同则跳过，减少无意义布局与 SizeChanged
            if (Math.Abs(Height - targetHeight) > 0.1)
            {
                Height = targetHeight;
            }
        }
        finally
        {
            _adjustingTitleBarBounds = false;
        }
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

    private void SetZoom(double factor)
    {
        if (BrowserView.CoreWebView2 is null)
        {
            return;
        }

        var clamped = Math.Clamp(factor, 0.25, 5.0);
        BrowserView.ZoomFactor = clamped;
        ZoomChanged?.Invoke(this, EventArgs.Empty);
        SetStatusMessage(LocalizationService.Format("Status.Zoom", Math.Round(clamped * 100)), StatusLevel.Info);
        RefreshControlWindow();
    }

    private void ZoomBy(double delta)
    {
        SetZoom(BrowserView.ZoomFactor + delta);
    }
}
