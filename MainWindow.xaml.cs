using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using GenshinBrowser.Constants;
using GenshinBrowser.Models;
using GenshinBrowser.Services;
using GenshinBrowser.Utils;
using GenshinBrowser.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
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
    private readonly DownloadsService _downloadsService;

    private AppSettings _settings = new();
    private bool _browserReady;
    private WebView2CompositionControl? _browserEventControl;
    private CoreWebView2? _browserEventCore;
    private bool _isShuttingDown;
    private bool _closeCleanupStarted;
    private bool _isRealClose;
    private bool _isNavigating;
    private bool _browserRecoveryInProgress;
    private int _rendererUnresponsiveCount;
    private DateTime _lastRendererUnresponsiveUtc = DateTime.MinValue;
    /// <summary>
    /// 首屏 NavigationCompleted 后才触发自动缓存检查，避免与首屏加载抢 IO。
    /// </summary>
    private bool _autoCacheCheckScheduled;
    private CancellationTokenSource? _cacheCheckCts;
    private Task? _cacheCheckTask;
    private string? _webViewUserDataFolder;
    // 启动预加载的 WebView2 环境：OnLoaded 一开始就并行拉起浏览器进程
    //（启动最慢一环），首次初始化消费一次；失败/缺运行时返回 null 走原路径
    private Task<CoreWebView2Environment?>? _environmentPreloadTask;
    /// <summary>
    /// 为 false 时忽略 Location/SizeChanged 对边界的写回，
    /// 防止启动默认坐标在配置恢复前覆盖 settings.json。
    /// </summary>
    private bool _persistWindowBounds;
    private string _currentAddress = string.Empty;
    private string _statusMessage = LocalizationService.Get("Status.InitBrowser", "正在初始化浏览器...");
    private StatusLevel _lastStatusLevel = StatusLevel.Info;
    private ControlWindow? _controlWindow;
    // 配置保存防抖改用 DispatcherTimer，避免每次 UI 事件 new CTS + Cancel + Dispose 的分配开销
    private DispatcherTimer? _settingsSaveTimer;
    // SPA 连续切集时取消前序未完成的 RecordHistoryForCurrentSourceAsync 延迟任务
    private CancellationTokenSource? _recordHistoryCts;

    // 浮窗模式下热键临时隐藏：再按一次恢复；切回浏览模式时自动恢复显示
    private bool _hiddenByHotkey;

    // 鼠标移出窗口检测：光标离开后向页面补发鼠标事件，避免播放器控件停在移出前的位置常驻
    private DispatcherTimer? _mouseLeaveWatchTimer;
    private bool _mouseOutsideNotified;

    // 最大化状态：透明无边框窗口不能用 WindowState.Maximized（会覆盖任务栏），
    // 这里用手工记录工作区矩形并保存还原前的边界。
    private bool _isMaximized;
    private Rect _savedBounds;

    /// <summary>
    /// 浏览器光标强制可见的描述符与回调。退出时需要显式解绑，避免 WebView2 与窗口之间的事件订阅泄漏。
    /// </summary>
    private System.ComponentModel.DependencyPropertyDescriptor? _cursorDescriptor;
    private EventHandler? _cursorForceArrowHandler;
    private WebView2CompositionControl? _cursorGuardBrowser;

    // 标题栏：浏览模式常驻；浮窗模式自动隐藏（顶部感应唤出）。
    // 显示时窗口向下 +30，不挤占 WebView 内容高度。
    // 内容区高度是尺寸真源：标题栏显隐只做 Height = content ± 30 的绝对赋值，
    // 禁止用 ActualHeight 累加减，否则 DPI/布局取整会每次漂移 1px。
    private bool _isTitleBarVisible = true;
    private bool _adjustingTitleBarBounds;
    private double _contentAreaHeight = 370;
    private DispatcherTimer? _titleBarHideTimer;
    private DispatcherTimer? _modeToastTimer;
    private bool _isApplyingZoom;

    // 主窗移动/缩放时，控制窗尺寸显示与跟随位置的 UI 防抖
    private DispatcherTimer? _windowBoundsUiDebounceTimer;

    // WebView2 下载事件可能高频触发。这里只保留每个任务的最新状态，统一按 100ms 刷新 UI。
    // 用 ConcurrentDictionary 让非 UI 线程的 BytesReceivedChanged 直接写入，避免每事件 Dispatcher.InvokeAsync 闭包堆积。
    private readonly ConcurrentDictionary<CoreWebView2DownloadOperation, DownloadItem> _downloadItemsByOperation = new();
    private readonly ConcurrentDictionary<DownloadItem, CoreWebView2DownloadOperation> _pendingDownloadProgress = new();
    // 反向字典：DownloadItem → operation，供 CancelDownload O(1) 查找（替代 FirstOrDefault 线性扫描）
    private readonly ConcurrentDictionary<DownloadItem, CoreWebView2DownloadOperation> _operationsByItem = new();
    // 0=无待处理 dispatch，1=已入队 dispatch。配合 ConcurrentDictionary 实现"仅入队一次启动 timer"
    private int _isProgressDispatchPending;
    private DispatcherTimer? _downloadProgressTimer;
    private PendingDownloadRetry? _pendingDownloadRetry;
    private DispatcherTimer? _pendingDownloadRetryWatchdog;
    private const int DownloadRetryPendingSeconds = 30;

    public event EventHandler<BrowserStateChangedEventArgs>? BrowserStateChanged;
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
        _downloadsService = new DownloadsService(Path.Combine(dataRoot, "downloads.json"));
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
        StateChanged += MainWindow_OnStateChanged;
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
        set => TrySetToggleModeHotkey(value, _settings.ToggleModeModifiers);
    }

    public ModifierKeys ToggleModeModifiers
    {
        get => _settings.ToggleModeModifiers;
        set => TrySetToggleModeHotkey(_settings.ToggleModeKey, value);
    }

    public Key TogglePlaybackKey
    {
        get => _settings.TogglePlaybackKey;
        set => TrySetTogglePlaybackHotkey(value, _settings.TogglePlaybackModifiers);
    }

    public ModifierKeys TogglePlaybackModifiers
    {
        get => _settings.TogglePlaybackModifiers;
        set => TrySetTogglePlaybackHotkey(_settings.TogglePlaybackKey, value);
    }

    public Key ToggleHideKey
    {
        get => _settings.ToggleHideKey;
        set => TrySetToggleHideHotkey(value, _settings.ToggleHideModifiers);
    }

    public ModifierKeys ToggleHideModifiers
    {
        get => _settings.ToggleHideModifiers;
        set => TrySetToggleHideHotkey(_settings.ToggleHideKey, value);
    }

    public Key SeekBackwardKey
    {
        get => _settings.SeekBackwardKey;
        set => TrySetSeekBackwardHotkey(value, _settings.SeekBackwardModifiers);
    }

    public ModifierKeys SeekBackwardModifiers
    {
        get => _settings.SeekBackwardModifiers;
        set => TrySetSeekBackwardHotkey(_settings.SeekBackwardKey, value);
    }

    public Key SeekForwardKey
    {
        get => _settings.SeekForwardKey;
        set => TrySetSeekForwardHotkey(value, _settings.SeekForwardModifiers);
    }

    public ModifierKeys SeekForwardModifiers
    {
        get => _settings.SeekForwardModifiers;
        set => TrySetSeekForwardHotkey(_settings.SeekForwardKey, value);
    }

    /// <summary>热键槽位标识，用于候选组合与其它槽位的冲突检查。</summary>
    private enum HotkeySlot
    {
        Mode,
        Playback,
        Hide,
        SeekBackward,
        SeekForward,
    }

    /// <summary>候选 (Key, 修饰键) 是否已被其它热键槽位占用（仅 UI 线程读写 settings）。</summary>
    private bool IsHotkeyOccupied(Key key, ModifierKeys modifiers, HotkeySlot self)
    {
        bool Occupied(HotkeySlot slot, Key slotKey, ModifierKeys slotMods) =>
            slot != self && slotKey == key && slotMods == modifiers;

        return Occupied(HotkeySlot.Mode, _settings.ToggleModeKey, _settings.ToggleModeModifiers)
            || Occupied(HotkeySlot.Playback, _settings.TogglePlaybackKey, _settings.TogglePlaybackModifiers)
            || Occupied(HotkeySlot.Hide, _settings.ToggleHideKey, _settings.ToggleHideModifiers)
            || Occupied(HotkeySlot.SeekBackward, _settings.SeekBackwardKey, _settings.SeekBackwardModifiers)
            || Occupied(HotkeySlot.SeekForward, _settings.SeekForwardKey, _settings.SeekForwardModifiers);
    }

    public bool TrySetToggleModeHotkey(Key key, ModifierKeys modifiers)
    {
        if (key == _settings.ToggleModeKey && modifiers == _settings.ToggleModeModifiers)
        {
            return true;
        }

        if (IsHotkeyOccupied(key, modifiers, HotkeySlot.Mode) ||
            !_keyboardHookService.TrySetToggleModeHotkey(KeyInterop.VirtualKeyFromKey(key), modifiers))
        {
            return false;
        }

        _settings.ToggleModeKey = key;
        _settings.ToggleModeModifiers = modifiers;
        QueueSettingsSave();
        OnHotkeysChanged();
        return true;
    }

    public bool TrySetTogglePlaybackHotkey(Key key, ModifierKeys modifiers)
    {
        if (key == _settings.TogglePlaybackKey && modifiers == _settings.TogglePlaybackModifiers)
        {
            return true;
        }

        if (IsHotkeyOccupied(key, modifiers, HotkeySlot.Playback) ||
            !_keyboardHookService.TrySetTogglePlaybackHotkey(KeyInterop.VirtualKeyFromKey(key), modifiers))
        {
            return false;
        }

        _settings.TogglePlaybackKey = key;
        _settings.TogglePlaybackModifiers = modifiers;
        QueueSettingsSave();
        OnHotkeysChanged();
        return true;
    }

    public bool TrySetToggleHideHotkey(Key key, ModifierKeys modifiers)
    {
        if (key == _settings.ToggleHideKey && modifiers == _settings.ToggleHideModifiers)
        {
            return true;
        }

        if (IsHotkeyOccupied(key, modifiers, HotkeySlot.Hide) ||
            !_keyboardHookService.TrySetToggleHideHotkey(KeyInterop.VirtualKeyFromKey(key), modifiers))
        {
            return false;
        }

        _settings.ToggleHideKey = key;
        _settings.ToggleHideModifiers = modifiers;
        QueueSettingsSave();
        OnHotkeysChanged();
        return true;
    }

    public bool TrySetSeekBackwardHotkey(Key key, ModifierKeys modifiers)
    {
        if (key == _settings.SeekBackwardKey && modifiers == _settings.SeekBackwardModifiers)
        {
            return true;
        }

        if (IsHotkeyOccupied(key, modifiers, HotkeySlot.SeekBackward) ||
            !_keyboardHookService.TrySetSeekBackwardHotkey(KeyInterop.VirtualKeyFromKey(key), modifiers))
        {
            return false;
        }

        _settings.SeekBackwardKey = key;
        _settings.SeekBackwardModifiers = modifiers;
        QueueSettingsSave();
        OnHotkeysChanged();
        return true;
    }

    public bool TrySetSeekForwardHotkey(Key key, ModifierKeys modifiers)
    {
        if (key == _settings.SeekForwardKey && modifiers == _settings.SeekForwardModifiers)
        {
            return true;
        }

        if (IsHotkeyOccupied(key, modifiers, HotkeySlot.SeekForward) ||
            !_keyboardHookService.TrySetSeekForwardHotkey(KeyInterop.VirtualKeyFromKey(key), modifiers))
        {
            return false;
        }

        _settings.SeekForwardKey = key;
        _settings.SeekForwardModifiers = modifiers;
        QueueSettingsSave();
        OnHotkeysChanged();
        return true;
    }

    private string FormatToggleModeHotkey() =>
        HotkeyFormatter.Format(_settings.ToggleModeKey, _settings.ToggleModeModifiers);

    private string FormatTogglePlaybackHotkey() =>
        HotkeyFormatter.Format(_settings.TogglePlaybackKey, _settings.TogglePlaybackModifiers);

    private string FormatToggleHideHotkey() =>
        HotkeyFormatter.Format(_settings.ToggleHideKey, _settings.ToggleHideModifiers);

    private void OnHotkeysChanged()
    {
        UpdateModeToggleButton();
        NotifyBrowserState(BrowserStateChangeKind.Mode);
    }

    public double ZoomFactor
    {
        get
        {
            // WebView2 初始化前访问 ZoomFactor 会抛异常；控制窗 VM 构造时就会读一次。
            try
            {
                return BrowserView.CoreWebView2 is null ? _settings.ZoomFactor : BrowserView.ZoomFactor;
            }
            catch
            {
                return _settings.ZoomFactor;
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

            if (_isShuttingDown)
            {
                return;
            }

            // 浏览器进程拉起是启动最慢的一环：先并行预创建环境，
            // 与下面的数据服务加载、控制窗构造同时进行
            StartEnvironmentPreload();

            // 启动时 settings 已 Sanitize 无冲突；原子写入避免分步 setter 中间态
            _keyboardHookService.TrySetToggleModeHotkey(
                KeyInterop.VirtualKeyFromKey(_settings.ToggleModeKey),
                _settings.ToggleModeModifiers);
            _keyboardHookService.TrySetTogglePlaybackHotkey(
                KeyInterop.VirtualKeyFromKey(_settings.TogglePlaybackKey),
                _settings.TogglePlaybackModifiers);
            _keyboardHookService.TrySetToggleHideHotkey(
                KeyInterop.VirtualKeyFromKey(_settings.ToggleHideKey),
                _settings.ToggleHideModifiers);
            _keyboardHookService.TrySetSeekBackwardHotkey(
                KeyInterop.VirtualKeyFromKey(_settings.SeekBackwardKey),
                _settings.SeekBackwardModifiers);
            _keyboardHookService.TrySetSeekForwardHotkey(
                KeyInterop.VirtualKeyFromKey(_settings.SeekForwardKey),
                _settings.SeekForwardModifiers);
            _keyboardHookService.IsGamingMode = _settings.WindowMode == WindowMode.Fixed;
            _keyboardHookService.HotkeyScope = _settings.HotkeyScope;
            UpdateWindowTitle();

            // 异步初始化历史/收藏/下载服务（构造函数不再同步读盘）。
            // 并行加载，不阻塞 UI 线程；完成后再构造控制窗（它会读取这些数据）。
            // 三个任务同时启动，但逐个独立兜底：单个服务读盘失败只按空数据继续，
            // 不像 Task.WhenAll 那样任一异常拖垮整个启动流程。
            var initTasks = new[]
            {
                _historyService.InitializeAsync(),
                _favoritesService.InitializeAsync(),
                _downloadsService.InitializeAsync(),
            };
            foreach (var initTask in initTasks)
            {
                try
                {
                    await initTask.ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    FileLogger.LogException(ex, "Initialize data service");
                }
            }

            _controlWindow = new ControlWindow(this);
            UpdateControlWindowVisibility();
            NotifyBrowserState(BrowserStateChangeKind.All);

            // 运行时检查/安装移入 GetOrCreateBrowserEnvironmentAsync：
            // 常见路径下预启动环境已就绪，这里直接消费
            await InitializeBrowserAsync();
            if (_isShuttingDown)
            {
                return;
            }

            _keyboardHookService.KPressed += KeyboardHookService_OnKPressed;
            _keyboardHookService.ModeTogglePressed += KeyboardHookService_OnModeTogglePressed;
            _keyboardHookService.HideTogglePressed += KeyboardHookService_OnHideTogglePressed;
            _keyboardHookService.SeekBackwardPressed += KeyboardHookService_OnSeekBackwardPressed;
            _keyboardHookService.SeekForwardPressed += KeyboardHookService_OnSeekForwardPressed;
            StartKeyboardHook();
            EnsureMouseLeaveWatchStarted();

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

    private void App_OnActivated(object? sender, EventArgs e)
    {
        _keyboardHookService.IsAppActive = true;
        UpdateWebViewMemoryTargetLevel();
    }

    private void App_OnDeactivated(object? sender, EventArgs e)
    {
        _keyboardHookService.IsAppActive = false;
        UpdateWebViewMemoryTargetLevel();
    }

    private void MainWindow_OnStateChanged(object? sender, EventArgs e)
    {
        UpdateWebViewMemoryTargetLevel();

        // 任务栏图标点击 / Alt-Tab 恢复：从热键隐藏（最小化）回到可见状态时结束隐藏态。
        // 程序化恢复（RestoreFromHotkeyHide）先清标志，不会走到这里二次处理。
        if (_hiddenByHotkey && WindowState != WindowState.Minimized)
        {
            _hiddenByHotkey = false;
            _keyboardHookService.AlwaysAllowHideToggle = false;
            UpdateControlWindowVisibility();
        }
    }

    /// <summary>
    /// 浮窗叠游戏、应用失焦或窗口最小化时请求 WebView2 使用低内存目标；
    /// 浏览模式且前台可见时恢复 Normal，减轻与游戏争用内存。
    /// </summary>
    private void UpdateWebViewMemoryTargetLevel(CoreWebView2? core = null)
    {
        try
        {
            core ??= BrowserView.CoreWebView2;
        }
        catch
        {
            return;
        }

        if (core is null)
        {
            return;
        }

        var preferLow =
            _settings.WindowMode == WindowMode.Fixed ||
            WindowState == WindowState.Minimized ||
            !_keyboardHookService.IsAppActive;

        var target = preferLow
            ? CoreWebView2MemoryUsageTargetLevel.Low
            : CoreWebView2MemoryUsageTargetLevel.Normal;

        try
        {
            if (core.MemoryUsageTargetLevel != target)
            {
                core.MemoryUsageTargetLevel = target;
            }
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Set WebView2 MemoryUsageTargetLevel");
        }
    }

    /// <summary>
    /// 并行预创建 WebView2 环境（拉起浏览器进程）。运行时缺失或创建失败时返回 null，
    /// 正式初始化走原路径（含运行时安装交互）。异常全部吞掉，预启动只是优化。
    /// </summary>
    private void StartEnvironmentPreload()
    {
        if (_environmentPreloadTask is not null || _isShuttingDown)
        {
            return;
        }

        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GenshinBrowser", "WebViewProfile");
        _webViewUserDataFolder = userDataFolder;
        _environmentPreloadTask = Task.Run(async () =>
        {
            try
            {
                if (!IsWebView2RuntimeInstalled())
                {
                    return null;
                }

                return await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            }
            catch
            {
                return null;
            }
        });
    }

    /// <summary>
    /// 取可用的 WebView2 环境：优先消费启动预加载（仅首次初始化使用一次，
    /// 浏览器进程崩溃后的重建路径必须重新创建，旧环境对象随进程失效）；
    /// 无预加载或预加载失败时按原路径检查运行时并创建。
    /// </summary>
    private async Task<CoreWebView2Environment> GetOrCreateBrowserEnvironmentAsync()
    {
        var preload = _environmentPreloadTask;
        if (preload is not null)
        {
            _environmentPreloadTask = null;
            if (await preload.ConfigureAwait(true) is { } preloaded)
            {
                return preloaded;
            }
        }

        await EnsureWebView2RuntimeAsync();
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GenshinBrowser", "WebViewProfile");
        _webViewUserDataFolder = userDataFolder;
        return await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
    }

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
            var msg = LocalizationService.Format(
                "Status.WebView2MissingInstaller",
                LocalizationService.Get("Status.WebView2ManualHint"));
            throw new InvalidOperationException(msg);
        }

        _statusMessage = LocalizationService.Get("Status.InstallingWebView2", "正在安装 WebView2 Runtime...");
        NotifyBrowserState(BrowserStateChangeKind.Status);
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = bootstrapperPath,
            Arguments = "/silent",
            UseShellExecute = true
        }) ?? throw new InvalidOperationException(LocalizationService.Get("Status.WebView2InstallerStartFailed"));

        await process.WaitForExitAsync();

        if (!IsWebView2RuntimeInstalled())
            throw new InvalidOperationException(LocalizationService.Get("Status.WebView2InstallFailed", "WebView2 Runtime 自动安装失败，请手动安装。"));
    }

    private async Task<bool> InitializeBrowserAsync(string? requestedStartUrl = null)
    {
        if (_isShuttingDown)
        {
            return false;
        }

        var browser = BrowserView;
        try
        {
            var environment = await GetOrCreateBrowserEnvironmentAsync();
            await browser.EnsureCoreWebView2Async(environment);
            if (_isShuttingDown || !ReferenceEquals(browser, BrowserView))
            {
                return false;
            }

            var core = browser.CoreWebView2
                       ?? throw new InvalidOperationException(LocalizationService.Get("Status.WebView2CoreUnavailable"));
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreDevToolsEnabled = true;
            // 浏览模式保留 Ctrl+F/F5/Ctrl+R 等浏览器快捷键；
            // 浮窗模式禁用，避免与游戏输入和全局热键冲突。
            core.Settings.AreBrowserAcceleratorKeysEnabled = ShouldEnableBrowserAcceleratorKeys(_settings.WindowMode);
            // CompositionControl + 透明浮窗必须用真正的 Transparent，非 0 alpha 会导致白屏/合成失败。
            // 光标问题用窗口近透明背景解决，切勿把 DefaultBackgroundColor 改成 Alpha>0。
            browser.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            ApplyWindowOpacity(_settings.WindowOpacity);
            ApplyStoredZoom(browser);
            UpdateWebViewMemoryTargetLevel(core);

            // 页面初始化脚本：透明背景 + 取消播放器 cursor:none
            await core.AddScriptToExecuteOnDocumentCreatedAsync(PageBootstrapScript);
            if (_isShuttingDown || !ReferenceEquals(browser, BrowserView))
            {
                return false;
            }

            AttachBrowserEvents(browser, core);
            AttachCursorGuard(browser);

            var startUrl = NavigationTarget.GetStartupUrl(requestedStartUrl ?? _settings.LastUrl);
            _currentAddress = startUrl;
            _browserReady = true;
            SetNavigating(true);
            core.Navigate(startUrl);
            // 按当前窗口模式应用标题栏（浏览常驻 / 浮窗隐藏）
            ApplyTitleBarForCurrentMode(forceHideInFixed: true);
            UpdateWindowTitle();
            SetStatusMessage(
                LocalizationService.Get("Status.BrowserReady"),
                StatusLevel.Success,
                BrowserStateChangeKind.Navigation | BrowserStateChangeKind.Mode | BrowserStateChangeKind.Appearance);
            // 自动缓存检查延后到首屏 NavigationCompleted，见 TryScheduleAutoCacheCheck
            return true;
        }
        catch (Exception ex)
        {
            DetachBrowserEvents(browser);
            DetachCursorGuard(browser);
            FileLogger.LogException(ex, "Initialize browser");
            if (!ReferenceEquals(browser, BrowserView) || _isShuttingDown)
            {
                return false;
            }

            _browserReady = false;
            SetNavigating(false);
            SetStatusMessage(LocalizationService.Format("Status.InitFailed", ex.Message), StatusLevel.Error);
            return false;
        }
    }

    private void AttachBrowserEvents(WebView2CompositionControl browser, CoreWebView2 core)
    {
        browser.NavigationStarting += BrowserView_OnNavigationStarting;
        browser.NavigationCompleted += BrowserView_OnNavigationCompleted;
        browser.ZoomFactorChanged += BrowserView_OnZoomFactorChanged;
        core.DocumentTitleChanged += BrowserView_OnDocumentTitleChanged;
        core.SourceChanged += BrowserView_OnSourceChanged;
        core.NewWindowRequested += BrowserView_OnNewWindowRequested;
        core.HistoryChanged += BrowserView_OnHistoryChanged;
        core.DownloadStarting += BrowserView_OnDownloadStarting;
        core.ProcessFailed += BrowserView_OnProcessFailed;
        _browserEventControl = browser;
        _browserEventCore = core;
    }

    private bool IsCurrentBrowserEventSender(object? sender)
    {
        return ReferenceEquals(_browserEventControl, BrowserView) &&
               (ReferenceEquals(sender, _browserEventControl) || ReferenceEquals(sender, _browserEventCore));
    }

    private void DetachBrowserEvents(WebView2CompositionControl browser)
    {
        try
        {
            browser.NavigationStarting -= BrowserView_OnNavigationStarting;
            browser.NavigationCompleted -= BrowserView_OnNavigationCompleted;
            browser.ZoomFactorChanged -= BrowserView_OnZoomFactorChanged;
        }
        catch
        {
            // 已释放的控件不再需要解绑 WPF 事件。
        }

        var isTrackedBrowser = ReferenceEquals(browser, _browserEventControl);
        var core = isTrackedBrowser ? _browserEventCore : null;
        if (core is null)
        {
            try { core = browser.CoreWebView2; }
            catch { /* 浏览器进程退出后可能无法再读取 CoreWebView2。 */ }
        }

        if (core is not null)
        {
            try
            {
                core.DocumentTitleChanged -= BrowserView_OnDocumentTitleChanged;
                core.SourceChanged -= BrowserView_OnSourceChanged;
                core.NewWindowRequested -= BrowserView_OnNewWindowRequested;
                core.HistoryChanged -= BrowserView_OnHistoryChanged;
                core.DownloadStarting -= BrowserView_OnDownloadStarting;
                core.ProcessFailed -= BrowserView_OnProcessFailed;
            }
            catch
            {
                // COM 进程已退出时解绑可能失败；控件随后会被释放。
            }
        }

        if (isTrackedBrowser)
        {
            _browserEventControl = null;
            _browserEventCore = null;
        }
    }

    private void AttachCursorGuard(WebView2CompositionControl browser)
    {
        browser.Cursor = System.Windows.Input.Cursors.Arrow;
        _cursorDescriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
            FrameworkElement.CursorProperty, typeof(FrameworkElement));
        _cursorForceArrowHandler = (_, _) =>
        {
            if (browser.Cursor is null || ReferenceEquals(browser.Cursor, System.Windows.Input.Cursors.None))
            {
                browser.Cursor = System.Windows.Input.Cursors.Arrow;
            }
        };
        _cursorGuardBrowser = browser;
        _cursorDescriptor?.AddValueChanged(browser, _cursorForceArrowHandler);
    }

    private void DetachCursorGuard(WebView2CompositionControl browser)
    {
        if (!ReferenceEquals(browser, _cursorGuardBrowser))
        {
            return;
        }

        if (_cursorDescriptor is not null && _cursorForceArrowHandler is not null)
        {
            try
            {
                _cursorDescriptor.RemoveValueChanged(browser, _cursorForceArrowHandler);
            }
            catch
            {
                // 浏览器进程异常退出时控件可能已处于释放状态。
            }
        }

        _cursorDescriptor = null;
        _cursorForceArrowHandler = null;
        _cursorGuardBrowser = null;
    }

    private void BrowserView_OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (!IsCurrentBrowserEventSender(sender))
        {
            return;
        }

        FileLogger.LogDebug(
            $"WebView2 process failed: Kind={e.ProcessFailedKind}, Reason={e.Reason}, ExitCode={e.ExitCode}");

        if (_isShuttingDown)
        {
            return;
        }

        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                _ = Dispatcher.InvokeAsync(() => _ = RecreateBrowserAsync());
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
                _ = Dispatcher.InvokeAsync(RecoverRendererProcess);
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                _ = Dispatcher.InvokeAsync(HandleRendererUnresponsive);
                break;
        }
    }

    private void RecoverRendererProcess()
    {
        if (_isShuttingDown || BrowserView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            SetStatusMessage(LocalizationService.Get("Status.RendererRecovering", "页面渲染进程异常，正在重新加载..."), StatusLevel.Warning);
            BrowserView.Reload();
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Recover WebView2 renderer");
            _ = RecreateBrowserAsync();
        }
    }

    private void HandleRendererUnresponsive()
    {
        var now = DateTime.UtcNow;
        _rendererUnresponsiveCount = now - _lastRendererUnresponsiveUtc <= TimeSpan.FromSeconds(10)
            ? _rendererUnresponsiveCount + 1
            : 1;
        _lastRendererUnresponsiveUtc = now;

        if (_rendererUnresponsiveCount < 2 || BrowserView.CoreWebView2 is null)
        {
            return;
        }

        _rendererUnresponsiveCount = 0;
        try
        {
            SetStatusMessage(LocalizationService.Get("Status.RendererUnresponsive", "页面长时间无响应，正在重新加载..."), StatusLevel.Warning);
            BrowserView.CoreWebView2.Stop();
            BrowserView.Reload();
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Recover unresponsive WebView2 renderer");
            _ = RecreateBrowserAsync();
        }
    }

    private async Task RecreateBrowserAsync()
    {
        if (_browserRecoveryInProgress || _isShuttingDown)
        {
            return;
        }

        _browserRecoveryInProgress = true;
        var recoveryUrl = EntryText.TryNormalizeHttpUrl(_currentAddress, out var normalizedUrl)
            ? normalizedUrl
            : AppConfig.Browser.DefaultUrl;

        try
        {
            SetStatusMessage(LocalizationService.Get("Status.BrowserRecovering", "浏览器进程异常，正在恢复..."), StatusLevel.Warning);
            _browserReady = false;
            SetNavigating(false);
            ClearPendingDownloadRetry();
            DetachAllDownloadOperations(markInterrupted: true);

            var oldBrowser = BrowserView;
            DetachBrowserEvents(oldBrowser);
            DetachCursorGuard(oldBrowser);
            BrowserHost.Children.Remove(oldBrowser);
            try
            {
                oldBrowser.Dispose();
            }
            catch (Exception ex)
            {
                FileLogger.LogException(ex, "Dispose failed WebView2 during recovery");
            }

            var replacement = CreateBrowserControl();
            BrowserView = replacement;
            BrowserHost.Children.Add(replacement);

            await Task.Delay(300).ConfigureAwait(true);
            if (_isShuttingDown)
            {
                return;
            }

            if (await InitializeBrowserAsync(recoveryUrl).ConfigureAwait(true))
            {
                SetStatusMessage(LocalizationService.Get("Status.BrowserRecovered", "浏览器已恢复。"), StatusLevel.Success);
            }
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Recreate WebView2");
            SetStatusMessage(LocalizationService.Format("Status.BrowserRecoveryFailed", ex.Message), StatusLevel.Error);
        }
        finally
        {
            _browserRecoveryInProgress = false;
        }
    }

    private WebView2CompositionControl CreateBrowserControl()
    {
        var browser = new WebView2CompositionControl
        {
            Cursor = System.Windows.Input.Cursors.Arrow,
            ForceCursor = true,
            DefaultBackgroundColor = System.Drawing.Color.Transparent,
        };
        ((FrameworkElement)browser).Opacity = Math.Clamp(_settings.WindowOpacity, 0.1, 1.0);
        return browser;
    }

    /// <summary>
    /// 异步检查 WebView2 可回收数据目录大小，超过阈值时静默自动清理浏览数据。
    /// 在后台线程计算，避免阻塞 UI。
    /// 24 小时内已检查过则跳过，避免每次启动都递归枚举 WebViewProfile 目录造成 IO 压力。
    /// 自动清理不弹状态提示，避免打扰正在浏览的用户。
    /// </summary>
    private async Task CheckWebView2CacheSizeAsync(string userDataFolder, CancellationToken cancellationToken)
    {
        // 频率限制：上次检查时间戳在 _settings 中，24 小时内跳过
        var nowUtc = DateTime.UtcNow;
        if ((nowUtc - _settings.LastWebView2CacheCheckUtc) < TimeSpan.FromHours(24))
        {
            return;
        }

        long? sizeBytes;
        try
        {
            sizeBytes = await WebViewDataSizeCalculator.CalculateAsync(userDataFolder, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (sizeBytes is null || cancellationToken.IsCancellationRequested || _isShuttingDown)
        {
            return;
        }

        if (sizeBytes.Value > AppConfig.Data.WebView2CacheThresholdBytes)
        {
            // 超阈值静默自动清理：不写状态栏，不 Toast
            await ClearBrowsingDataAsync(silent: true);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _settings.LastWebView2CacheCheckUtc = nowUtc;
        QueueSettingsSave();
    }

    /// <summary>
    /// 清理 WebView2 浏览数据：磁盘缓存、DOM 存储与 Service Worker。
    /// 自动填充和已保存密码始终保留。
    /// 保留 Cookie（登录态）、浏览历史、下载记录；应用内收藏夹/历史不在 WebView2 数据内，不受影响。
    /// </summary>
    /// <param name="silent">为 true 时不写状态栏、不刷新控制窗（用于启动自动清理）。</param>
    public async Task<bool> ClearBrowsingDataAsync(bool silent = false)
    {
        CoreWebView2? core;
        try
        {
            core = BrowserView.CoreWebView2;
        }
        catch
        {
            core = null;
        }

        if (core is null)
        {
            if (!silent)
            {
                SetStatusMessage(LocalizationService.Get("Status.BrowserNotReady"), StatusLevel.Warning);
            }
            return false;
        }

        try
        {
            if (!silent)
            {
                SetStatusMessage(LocalizationService.Get("Status.ClearingBrowsingData"), StatusLevel.Info);
            }

            // 清「可再生」数据；保留 Cookie / 浏览历史 / 下载记录 / 自动填充 / 已保存密码
            // 注意：1.0.4022.49 版本枚举不含 NetworkCache，DiskCache 已覆盖磁盘缓存
            // AllDomStorage 覆盖 LocalStorage / IndexedDB / CacheStorage 等站点本地数据
            var kinds =
                CoreWebView2BrowsingDataKinds.DiskCache |
                CoreWebView2BrowsingDataKinds.AllDomStorage |
                CoreWebView2BrowsingDataKinds.ServiceWorkers;

            await core.Profile.ClearBrowsingDataAsync(kinds);

            if (!_isShuttingDown)
            {
                _settings.LastWebView2CacheCheckUtc = DateTime.UtcNow;
                QueueSettingsSave();
            }

            if (!silent)
            {
                SetStatusMessage(LocalizationService.Get("Status.BrowsingDataCleared"), StatusLevel.Success);
            }

            return true;
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, silent ? "Clear browsing data (auto)" : "Clear browsing data");
            if (!silent)
            {
                SetStatusMessage(LocalizationService.Format("Status.ClearBrowsingDataFailed", ex.Message), StatusLevel.Error);
            }

            return false;
        }
    }

    private void BrowserView_OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!IsCurrentBrowserEventSender(sender))
        {
            return;
        }

        if (e.IsRedirected)
        {
            return;
        }

        SetNavigating(true);
    }

    private async void BrowserView_OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!IsCurrentBrowserEventSender(sender) || !_browserReady || BrowserView.CoreWebView2 is null)
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
                // PageBootstrapScript 已通过 AddScriptToExecuteOnDocumentCreatedAsync 注册，
                // 会自动在每个新文档执行；脚本内部用 id 去重，无需在此重复执行。

                CompleteNavigation(
                    LocalizationService.Format(
                        "Status.PageLoaded",
                        FormatTogglePlaybackHotkey(),
                        FormatToggleModeHotkey()),
                    StatusLevel.Success,
                    BrowserStateChangeKind.History);
            }
            else
            {
                CompleteNavigation(
                    LocalizationService.Format("Status.LoadFailed", e.WebErrorStatus),
                    StatusLevel.Error);
            }

            // 成功或失败都调度一次：首屏失败时仍应有机会腾空间，避免坏缓存卡住后续启动
            TryScheduleAutoCacheCheck();
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Navigation completed handling");
            CompleteNavigation(
                LocalizationService.Format("Status.RecordStateFailed", ex.Message),
                StatusLevel.Error);
            TryScheduleAutoCacheCheck();
        }
    }

    /// <summary>
    /// 首屏导航结束后再异步检查/清理 WebView2 缓存，避免与首屏加载抢磁盘 IO。
    /// 仅调度一次；24 小时节流与阈值判断在 CheckWebView2CacheSizeAsync 内。
    /// </summary>
    private void TryScheduleAutoCacheCheck()
    {
        if (_autoCacheCheckScheduled || string.IsNullOrEmpty(_webViewUserDataFolder))
        {
            return;
        }

        _autoCacheCheckScheduled = true;
        _cacheCheckCts = new CancellationTokenSource();
        _cacheCheckTask = CheckWebView2CacheSizeAsync(_webViewUserDataFolder, _cacheCheckCts.Token);
    }

    private async Task StopAutoCacheCheckAsync()
    {
        var cts = _cacheCheckCts;
        var task = _cacheCheckTask;
        _cacheCheckCts = null;
        _cacheCheckTask = null;

        cts?.Cancel();
        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
            catch (Exception ex)
            {
                FileLogger.LogException(ex, "Stop automatic WebView2 cache check");
            }
        }

        cts?.Dispose();
    }

    private void BrowserView_OnDocumentTitleChanged(object? sender, object e)
    {
        if (!IsCurrentBrowserEventSender(sender))
        {
            return;
        }

        UpdateWindowTitle();
    }

    private void BrowserView_OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        if (!IsCurrentBrowserEventSender(sender))
        {
            return;
        }

        // 当页面 URL 改变时（包括用户点击链接、重定向、B 站 SPA 切集等），立即更新地址栏与 LastUrl
        // 关闭流程开始后忽略：此时服务可能已 Dispose，晚到的 QueueSettingsSave 会打到已释放的保存闸。
        if (!_browserReady || BrowserView.CoreWebView2 is null || _isShuttingDown)
        {
            return;
        }

        try
        {
            if (CaptureCurrentUrlToSettings())
            {
                QueueSettingsSave();
                NotifyBrowserState(BrowserStateChangeKind.Navigation);
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

            var addressChanged = !string.Equals(_currentAddress, newUrl, StringComparison.Ordinal);
            var canPersist = EntryText.TryNormalizeHttpUrl(newUrl, out var persistedUrl);
            var persistedUrlChanged = canPersist &&
                                      !string.Equals(_settings.LastUrl, persistedUrl, StringComparison.Ordinal);
            if (!addressChanged && !persistedUrlChanged)
            {
                return false;
            }

            _currentAddress = newUrl;
            if (persistedUrlChanged)
            {
                _settings.LastUrl = persistedUrl;
            }
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

        // 取消前序未完成的记录任务：SPA 连续切集时避免堆积多个 Task.Delay(400) 延迟任务
        _recordHistoryCts?.Cancel();
        _recordHistoryCts?.Dispose();
        _recordHistoryCts = new CancellationTokenSource();
        var token = _recordHistoryCts.Token;

        try
        {
            var currentUrl = BrowserView.CoreWebView2.Source;
            if (string.IsNullOrWhiteSpace(currentUrl))
            {
                return;
            }

            // 给 SPA 一点时间更新 document.title
            await Task.Delay(400, token).ConfigureAwait(true);
            if (_isShuttingDown || BrowserView.CoreWebView2 is null || token.IsCancellationRequested)
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
                NotifyBrowserState(BrowserStateChangeKind.History);
            }
        }
        catch (OperationCanceledException)
        {
            // 被新的 SourceChanged 取消，预期行为
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Record history for source change");
        }
    }

    private void BrowserView_OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (!IsCurrentBrowserEventSender(sender))
        {
            return;
        }

        // 拦截「在新窗口打开」的链接，统一在当前浏览器内导航，避免弹出无控制窗口的裸 WebView2
        if (string.IsNullOrEmpty(e.Uri))
        {
            return;
        }

        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && NavigationTarget.IsHttpOrHttps(uri))
        {
            e.Handled = true;
            BrowserView.CoreWebView2.Navigate(e.Uri);
        }
    }

    private void BrowserView_OnHistoryChanged(object? sender, object e)
    {
        if (!IsCurrentBrowserEventSender(sender))
        {
            return;
        }

        // WebView 前进/后退栈变化，不涉及应用内 history.json
        NotifyBrowserState(BrowserStateChangeKind.Navigation);
    }

    private void MainWindow_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 精确匹配 Ctrl 单个修饰键：HasFlag 会把 Ctrl+Shift+= / Ctrl+Alt+= 也当成缩放
        // 并吞掉，导致这些组合无法到达页面。仅当修饰键恰好是 Ctrl 时才拦截。
        if (Keyboard.Modifiers != ModifierKeys.Control)
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
        if (!IsCurrentBrowserEventSender(sender))
        {
            return;
        }

        // WebView2 事件可能从后台线程触发（与 BytesReceivedChanged/StateChanged 一致）。
        // 这里只捕获 COM operation 引用与结果路径字符串，避免在非 UI 线程改 UI 绑定状态；
        // 事件参数本身不跨线程使用。
        if (!Dispatcher.CheckAccess())
        {
            var operation = e.DownloadOperation;
            var resultFilePath = e.ResultFilePath;
            _ = Dispatcher.InvokeAsync(() => HandleDownloadStarting(operation, resultFilePath));
            return;
        }

        HandleDownloadStarting(e.DownloadOperation, e.ResultFilePath);
    }

    private void HandleDownloadStarting(CoreWebView2DownloadOperation? operation, string? resultFilePath)
    {
        DownloadItem? item = null;
        try
        {
            if (operation is null)
            {
                throw new InvalidOperationException("DownloadStarting event carried no download operation.");
            }

            var filePath = resultFilePath ?? string.Empty;
            var fileName = !string.IsNullOrEmpty(filePath) ? Path.GetFileName(filePath) : LocalizationService.Get("Downloads.DefaultFileName", "下载文件");

            var sourceUri = operation.Uri ?? string.Empty;
            var totalBytes = operation.TotalBytesToReceive > 0 ? (long)operation.TotalBytesToReceive : 0;
            var receivedBytes = (long)operation.BytesReceived;
            var retryItem = TakePendingDownloadRetry(sourceUri);
            if (retryItem is not null)
            {
                item = retryItem;
                _downloadsService.Restart(
                    item,
                    operation,
                    sourceUri,
                    filePath,
                    totalBytes,
                    receivedBytes);
            }
            else
            {
                item = new DownloadItem
                {
                    FileName = fileName,
                    FilePath = filePath,
                    SourceUri = sourceUri,
                    TotalBytes = totalBytes,
                    ReceivedBytes = receivedBytes,
                    StartedAtUtc = DateTime.UtcNow,
                };
                _downloadsService.Track(item, operation);
            }

            _downloadItemsByOperation[operation] = item;
            _operationsByItem[item] = operation;
            operation.BytesReceivedChanged += DownloadOperation_OnBytesReceivedChanged;
            operation.StateChanged += DownloadOperation_OnStateChanged;

            SetStatusMessage(LocalizationService.Format("Status.DownloadStarted", item.FileName), StatusLevel.Info);
            DownloadsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            if (operation is not null)
            {
                DetachDownloadOperation(operation);
            }

            if (item is not null)
            {
                _pendingDownloadProgress.TryRemove(item, out _);
                _operationsByItem.TryRemove(item, out _);
                var isVisible = Downloads.Contains(item);
                _downloadsService.MarkInterrupted(item);
                if (isVisible)
                {
                    DownloadsChanged?.Invoke(this, EventArgs.Empty);
                }
            }

            FileLogger.LogException(ex, "DownloadStarting");
        }
    }

    private void DownloadOperation_OnBytesReceivedChanged(object? sender, object e)
    {
        if (sender is not CoreWebView2DownloadOperation operation)
        {
            return;
        }

        if (_isShuttingDown)
        {
            return;
        }

        // 非 UI 线程：直接写 ConcurrentDictionary，仅入队一次 dispatch 启动 timer。
        // 避免每事件都 Dispatcher.InvokeAsync 闭包入队（快下载时每秒数百事件 → 队列堆积）。
        if (!_downloadItemsByOperation.TryGetValue(operation, out var item))
        {
            return;
        }

        _pendingDownloadProgress[item] = operation;

        // 仅当没有待处理 dispatch 时才入队启动 timer
        if (Interlocked.CompareExchange(ref _isProgressDispatchPending, 1, 0) == 0)
        {
            _ = Dispatcher.InvokeAsync(EnsureDownloadProgressTimerStarted);
        }
    }

    private void EnsureDownloadProgressTimerStarted()
    {
        Volatile.Write(ref _isProgressDispatchPending, 0);
        _downloadProgressTimer ??= CreateDownloadProgressTimer();
        if (!_downloadProgressTimer.IsEnabled)
        {
            _downloadProgressTimer.Start();
        }
    }

    private void BrowserView_OnZoomFactorChanged(object? sender, object e)
    {
        if (_isApplyingZoom || !IsCurrentBrowserEventSender(sender) || _isShuttingDown)
        {
            return;
        }

        try
        {
            var zoom = Math.Clamp(BrowserView.ZoomFactor, 0.25, 5.0);
            StoreZoomFactor(zoom);
            ZoomChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Capture WebView2 zoom factor");
        }
    }

    private DispatcherTimer CreateDownloadProgressTimer()
    {
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        timer.Tick += DownloadProgressTimer_OnTick;
        return timer;
    }

    private void DownloadProgressTimer_OnTick(object? sender, EventArgs e)
    {
        _downloadProgressTimer?.Stop();
        if (_isShuttingDown || _pendingDownloadProgress.Count == 0)
        {
            return;
        }

        foreach (var pair in _pendingDownloadProgress)
        {
            try
            {
                ApplyDownloadProgress(pair.Key, pair.Value);
            }
            catch (Exception ex)
            {
                FileLogger.LogException(ex, "Update download progress");
            }
        }
        _pendingDownloadProgress.Clear();
        DownloadsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void ApplyDownloadProgress(DownloadItem item, CoreWebView2DownloadOperation operation)
    {
        var resultFilePath = operation.ResultFilePath;
        if (!string.IsNullOrWhiteSpace(resultFilePath) &&
            !string.Equals(item.FilePath, resultFilePath, StringComparison.Ordinal))
        {
            item.FilePath = resultFilePath;
            var fileName = Path.GetFileName(resultFilePath);
            if (!string.IsNullOrEmpty(fileName))
            {
                item.FileName = fileName;
            }
        }

        item.ReceivedBytes = (long)operation.BytesReceived;
        if (operation.TotalBytesToReceive > 0)
        {
            item.TotalBytes = (long)operation.TotalBytesToReceive;
        }
    }

    private void DownloadOperation_OnStateChanged(object? sender, object e)
    {
        if (sender is not CoreWebView2DownloadOperation operation)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => HandleDownloadStateChanged(operation));
            return;
        }

        HandleDownloadStateChanged(operation);
    }

    private void HandleDownloadStateChanged(CoreWebView2DownloadOperation operation)
    {
        if (!_downloadItemsByOperation.TryGetValue(operation, out var item))
        {
            return;
        }

        try
        {
            ApplyDownloadProgress(item, operation);
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Read final download state");
            _downloadsService.MarkInterrupted(item);
            _pendingDownloadProgress.TryRemove(item, out _);
            DetachDownloadOperation(operation);
            DownloadsChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        _pendingDownloadProgress.TryRemove(item, out _);

        switch (operation.State)
        {
            case CoreWebView2DownloadState.Completed:
                _downloadsService.MarkCompleted(item);
                SetStatusMessage(LocalizationService.Format("Status.DownloadCompleted", item.FileName), StatusLevel.Success);
                DetachDownloadOperation(operation);
                break;
            case CoreWebView2DownloadState.Interrupted:
                if (item.State == DownloadState.Canceled ||
                    operation.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled)
                {
                    var wasAlreadyCanceled = item.State == DownloadState.Canceled;
                    _downloadsService.MarkCanceled(item);
                    if (!wasAlreadyCanceled)
                    {
                        SetStatusMessage(LocalizationService.Format("Status.DownloadCanceled", item.FileName), StatusLevel.Success);
                    }
                }
                else
                {
                    _downloadsService.MarkInterrupted(item);
                    SetStatusMessage(LocalizationService.Format("Status.DownloadInterrupted", item.FileName), StatusLevel.Warning);
                }
                DetachDownloadOperation(operation);
                break;
        }

        DownloadsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DetachDownloadOperation(CoreWebView2DownloadOperation operation)
    {
        try { operation.BytesReceivedChanged -= DownloadOperation_OnBytesReceivedChanged; }
        catch { /* 浏览器进程退出后 operation 可能已失效。 */ }

        try { operation.StateChanged -= DownloadOperation_OnStateChanged; }
        catch { /* 浏览器进程退出后 operation 可能已失效。 */ }

        if (_downloadItemsByOperation.TryRemove(operation, out var item))
        {
            _operationsByItem.TryRemove(item, out _);
        }
    }

    private void DetachAllDownloadOperations(bool markInterrupted = false)
    {
        _downloadProgressTimer?.Stop();
        if (_downloadProgressTimer is not null)
        {
            _downloadProgressTimer.Tick -= DownloadProgressTimer_OnTick;
            _downloadProgressTimer = null;
        }

        _pendingDownloadProgress.Clear();
        _operationsByItem.Clear();
        var changed = false;
        foreach (var pair in _downloadItemsByOperation.ToArray())
        {
            if (markInterrupted && pair.Value.State == DownloadState.InProgress)
            {
                _downloadsService.MarkInterrupted(pair.Value);
                changed = true;
            }

            DetachDownloadOperation(pair.Key);
        }

        if (changed)
        {
            DownloadsChanged?.Invoke(this, EventArgs.Empty);
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

    private void KeyboardHookService_OnHideTogglePressed(object? sender, EventArgs e)
    {
        if (_isShuttingDown)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(ToggleFloatingHidden);
    }

    private void KeyboardHookService_OnSeekBackwardPressed(object? sender, EventArgs e)
    {
        if (_isShuttingDown)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() => _ = SeekVideoAsync(-5));
    }

    private void KeyboardHookService_OnSeekForwardPressed(object? sender, EventArgs e)
    {
        if (_isShuttingDown)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() => _ = SeekVideoAsync(5));
    }

    /// <summary>
    /// 临时隐藏/恢复显示窗口（热键 F7 默认），浏览与浮窗模式均可用。
    /// 用最小化而非 Hide()：任务栏图标保留，点击图标即可恢复（与再按 F7 等效），
    /// Alt-Tab 也能找回；隐藏时控制窗一并收起，视频继续播放（保留声音）。
    /// 隐藏期间隐藏键是唯一热键恢复入口：钩子层 AlwaysAllowHideToggle 使其不受生效范围限制。
    /// </summary>
    public void ToggleFloatingHidden()
    {
        if (_isShuttingDown)
        {
            return;
        }

        if (_hiddenByHotkey)
        {
            RestoreFromHotkeyHide();
            SetStatusMessage(LocalizationService.Get("Status.WindowShown"), StatusLevel.Success);
        }
        else
        {
            _hiddenByHotkey = true;
            _keyboardHookService.AlwaysAllowHideToggle = true;
            try
            {
                _controlWindow?.Hide();
            }
            catch (Exception ex)
            {
                FileLogger.LogException(ex, "Hide control window");
            }

            WindowState = WindowState.Minimized;
            SetStatusMessage(
                LocalizationService.Format("Status.WindowHidden", FormatToggleHideHotkey()),
                StatusLevel.Info);
        }
    }

    /// <summary>
    /// 结束热键隐藏态并恢复窗口。SW_SHOWNOACTIVATE 从最小化恢复但不抢前台焦点
    /// （游戏中按 F7 恢复浮窗不切走游戏焦点）；失败兜底走 WPF 恢复（会激活）。
    /// </summary>
    private void RestoreFromHotkeyHide()
    {
        _hiddenByHotkey = false;
        _keyboardHookService.AlwaysAllowHideToggle = false;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !ShowWindow(hwnd, SwShowNoActivate))
        {
            WindowState = WindowState.Normal;
        }

        UpdateControlWindowVisibility();
    }

    /// <summary>视频快进/倒退（秒）。找到当前可见的视频元素并钳制在 [0, duration] 内跳转。</summary>
    public async Task SeekVideoAsync(double deltaSeconds)
    {
        if (!_browserReady || BrowserView.CoreWebView2 is null)
        {
            SetStatusMessage(LocalizationService.Get("Status.BrowserNotReady"), StatusLevel.Warning);
            return;
        }

        var script = BuildSeekScript(deltaSeconds);
        try
        {
            var result = await BrowserView.CoreWebView2.ExecuteScriptAsync(script);
            var secondsText = Math.Abs(deltaSeconds).ToString("0");
            switch (result)
            {
                case "\"ok\"":
                    SetStatusMessage(
                        LocalizationService.Format(
                            deltaSeconds < 0 ? "Status.SeekBackward" : "Status.SeekForward",
                            secondsText),
                        StatusLevel.Success);
                    break;
                case "\"no-video\"":
                    SetStatusMessage(LocalizationService.Get("Status.NoVideo"), StatusLevel.Warning);
                    break;
                case "\"at-edge\"":
                    SetStatusMessage(LocalizationService.Get("Status.SeekAtEdge"), StatusLevel.Info);
                    break;
                default:
                    // live 流（duration=Infinity）等无时长的视频：命令已发出但不跳转
                    SetStatusMessage(LocalizationService.Get("Status.PlaybackCommandSent"), StatusLevel.Info);
                    break;
            }
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Seek video");
            SetStatusMessage(LocalizationService.Format("Status.PlaybackFailed", ex.Message), StatusLevel.Error);
        }
    }

    private static string BuildSeekScript(double deltaSeconds)
    {
        const string template = """
(() => {
  const videos = Array.from(document.querySelectorAll('video'));
  const video = videos.find(v => {
    const rect = v.getBoundingClientRect();
    return rect.width > 0 && rect.height > 0;
  }) || videos[0];
  if (!video) return 'no-video';
  const duration = video.duration;
  if (!isFinite(duration)) return 'no-duration';
  const target = Math.max(0, Math.min(duration, video.currentTime + DELTA));
  if (Math.abs(target - video.currentTime) < 0.001) return 'at-edge';
  video.currentTime = target;
  return 'ok';
})();
""";
        return template.Replace("DELTA", deltaSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 低频轮询光标是否已离开窗口（WebView 捕获输入时 WPF 的 MouseLeave 不可靠，
    /// 用 GetCursorPos + 窗口物理矩形判断）。离开后向页面补发一次鼠标事件，
    /// 让播放器感知「鼠标已不在」并收起控制栏；回到窗口内后重置标志。
    /// </summary>
    private void EnsureMouseLeaveWatchStarted()
    {
        if (_isShuttingDown)
        {
            return;
        }

        _mouseLeaveWatchTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _mouseLeaveWatchTimer.Tick -= MouseLeaveWatchTimer_OnTick;
        _mouseLeaveWatchTimer.Tick += MouseLeaveWatchTimer_OnTick;
        if (!_mouseLeaveWatchTimer.IsEnabled)
        {
            _mouseLeaveWatchTimer.Start();
        }
    }

    private void MouseLeaveWatchTimer_OnTick(object? sender, EventArgs e)
    {
        if (_isShuttingDown || !_browserReady || _hiddenByHotkey || WindowState == WindowState.Minimized)
        {
            _mouseOutsideNotified = false;
            return;
        }

        if (IsCursorOverWindow())
        {
            _mouseOutsideNotified = false;
            return;
        }

        if (!_mouseOutsideNotified)
        {
            _mouseOutsideNotified = true;
            _ = DispatchMouseLeftPageAsync();
        }
    }

    private bool IsCursorOverWindow()
    {
        try
        {
            if (!GetCursorPos(out var cursor))
            {
                return true;
            }

            var origin = PointToScreen(new Point(0, 0));
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget is null)
            {
                return true;
            }

            var transform = source.CompositionTarget.TransformToDevice;
            var width = ActualWidth * transform.M11;
            var height = ActualHeight * transform.M22;
            const double tolerance = 2;
            return cursor.X >= origin.X - tolerance && cursor.X <= origin.X + width + tolerance &&
                   cursor.Y >= origin.Y - tolerance && cursor.Y <= origin.Y + height + tolerance;
        }
        catch
        {
            // 句柄尚未创建/已销毁等：按「在窗口内」处理，避免误发事件
            return true;
        }
    }

    private async Task DispatchMouseLeftPageAsync()
    {
        CoreWebView2? core;
        try
        {
            core = BrowserView.CoreWebView2;
        }
        catch
        {
            return;
        }

        if (core is null)
        {
            return;
        }

        try
        {
            await core.ExecuteScriptAsync(MouseLeftPageScript);
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Notify mouse left page");
        }
    }

    /// <summary>
    /// 注入页面：把页面感知的鼠标位置挪到左上角（远离底部控制栏），并对常见
    /// 播放器容器补发 mouseleave/mouseout，使其收起控制栏、恢复闲置自动隐藏。
    /// </summary>
    private const string MouseLeftPageScript = """
(() => {
  try {
    document.dispatchEvent(new MouseEvent('mousemove', {
      bubbles: true, cancelable: true, view: window, clientX: 2, clientY: 2
    }));
    const selectors = '.bpx-player-container,.bpx-player-video-wrap,.bilibili-player-video-wrap,' +
                      '.bilibili-player-video-perch,.bilibili-player-area,video';
    for (const el of document.querySelectorAll(selectors)) {
      el.dispatchEvent(new MouseEvent('mouseleave', { bubbles: false, cancelable: true, view: window }));
      el.dispatchEvent(new MouseEvent('mouseout', { bubbles: true, cancelable: true, view: window }));
    }
  } catch (_) {}
})();
""";

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Win32Point lpPoint);

    private const int SwShowNoActivate = 4;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Point
    {
        public int X;
        public int Y;
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
    const p = video.play();
    // play() 返回 Promise：等待其结算，自动播放被拦截时返回 play-blocked，
    // 避免未处理的 Promise rejection 污染页面控制台，也避免误报为已播放。
    if (p && typeof p.then === 'function') {
      return p.then(() => 'play').catch(() => 'play-blocked');
    }
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
                case "\"play-blocked\"":
                    SetStatusMessage(LocalizationService.Get("Status.PlaybackBlocked"), StatusLevel.Warning);
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

        // 热键隐藏状态下切模式：先结束隐藏态恢复窗口（回浏览模式允许激活）
        if (_hiddenByHotkey)
        {
            _hiddenByHotkey = false;
            _keyboardHookService.AlwaysAllowHideToggle = false;
            WindowState = WindowState.Normal;
        }

        // 切换前先记下当前位置/尺寸，浏览/浮窗共用同一组边界
        SaveWindowBounds();

        _settings.WindowMode = mode;

        _windowModeService.ApplyMode(_settings.WindowMode);
        _keyboardHookService.IsGamingMode = _settings.WindowMode == WindowMode.Fixed;
        UpdateBrowserAcceleratorKeys();
        // 浮窗叠游戏 / 失焦 / 最小化时降低 WebView 内存目标，前台浏览再恢复
        UpdateWebViewMemoryTargetLevel();
        // 浏览：标题栏常驻；浮窗：自动隐藏（向下延长策略不改内容区高度）
        ApplyTitleBarForCurrentMode(forceHideInFixed: true);
        UpdateModeToggleButton();

        var enteringFloating = _settings.WindowMode == WindowMode.Fixed;
        // 首次进入浮窗：较长引导；之后仅短 toast + 描边闪
        if (enteringFloating && !_settings.HasSeenFloatingModeHint)
        {
            _settings.HasSeenFloatingModeHint = true;
            ShowMainModeToast(
                LocalizationService.Format(
                    "Mode.FirstFloatingHint",
                    FormatToggleModeHotkey()),
                TimeSpan.FromSeconds(3.2));
            SetStatusMessage(
                LocalizationService.Format("Mode.FixedOn", FormatToggleModeHotkey()),
                StatusLevel.Success);
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
                    ? LocalizationService.Format("Mode.FixedOn", FormatToggleModeHotkey())
                    : LocalizationService.Get("Mode.FreeOn"),
                StatusLevel.Info);
        }

        FlashModeBorder(enteringFloating);
        UpdateControlWindowVisibility();
        NotifyBrowserState(BrowserStateChangeKind.Mode | BrowserStateChangeKind.Navigation | BrowserStateChangeKind.Appearance);
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
            SetStatusMessage(
                LocalizationService.Get("Status.FavoriteAdded"),
                StatusLevel.Success,
                BrowserStateChangeKind.Favorites | BrowserStateChangeKind.Navigation);
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
            SetStatusMessage(
                LocalizationService.Get("Status.FavoriteRemoved"),
                StatusLevel.Success,
                BrowserStateChangeKind.Favorites | BrowserStateChangeKind.Navigation);
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
            SetStatusMessage(
                LocalizationService.Get("Status.HistoryRemoved"),
                StatusLevel.Success,
                BrowserStateChangeKind.History);
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
        _settings.HasControlWindowPosition = true;
        _settings.ControlWindowWidth = width;
        _settings.ControlWindowHeight = height;
        if (!_isShuttingDown)
        {
            QueueSettingsSave();
        }
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
        if (_settings.HasControlWindowPosition)
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

        var target = NavigationTarget.Build(input);
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
            SetStatusMessage(
                LocalizationService.Format("Status.Opening", target),
                StatusLevel.Info,
                BrowserStateChangeKind.Navigation);
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
        RestoreDefaultHotkeys();
        SetZoom(1.0);
        QueueSettingsSave();
    }

    /// <summary>
    /// 恢复默认热键：模式=F8、播放=K、隐藏=F7、倒退=;、快进='。
    /// 先把非默认热键泊到 F9–F12 的空闲位（不能是任何默认值或其它热键当前值），
    /// 避免槽位间交叉占用导致目标默认值写不进去。
    /// </summary>
    private void RestoreDefaultHotkeys()
    {
        var slots = new (HotkeySlot Slot, Func<Key> GetKey, Func<ModifierKeys> GetMods, Func<Key, ModifierKeys, bool> Set)[]
        {
            (HotkeySlot.Mode, () => _settings.ToggleModeKey, () => _settings.ToggleModeModifiers,
                (k, m) => TrySetToggleModeHotkey(k, m)),
            (HotkeySlot.Playback, () => _settings.TogglePlaybackKey, () => _settings.TogglePlaybackModifiers,
                (k, m) => TrySetTogglePlaybackHotkey(k, m)),
            (HotkeySlot.Hide, () => _settings.ToggleHideKey, () => _settings.ToggleHideModifiers,
                (k, m) => TrySetToggleHideHotkey(k, m)),
            (HotkeySlot.SeekBackward, () => _settings.SeekBackwardKey, () => _settings.SeekBackwardModifiers,
                (k, m) => TrySetSeekBackwardHotkey(k, m)),
            (HotkeySlot.SeekForward, () => _settings.SeekForwardKey, () => _settings.SeekForwardModifiers,
                (k, m) => TrySetSeekForwardHotkey(k, m)),
        };
        var defaults = new Dictionary<HotkeySlot, (Key Key, ModifierKeys Mods)>
        {
            [HotkeySlot.Mode] = (Key.F8, ModifierKeys.None),
            [HotkeySlot.Playback] = (Key.K, ModifierKeys.None),
            [HotkeySlot.Hide] = (Key.F7, ModifierKeys.None),
            [HotkeySlot.SeekBackward] = (Key.OemSemicolon, ModifierKeys.None),
            [HotkeySlot.SeekForward] = (Key.OemQuotes, ModifierKeys.None),
        };

        if (slots.All(slot => slot.GetKey() == defaults[slot.Slot].Key && slot.GetMods() == defaults[slot.Slot].Mods))
        {
            return;
        }

        ReadOnlySpan<Key> parkingKeys = [Key.F9, Key.F10, Key.F11, Key.F12];
        var defaultKeys = defaults.Values.Select(pair => pair.Key).ToHashSet();
        foreach (var slot in slots)
        {
            var (defaultKey, defaultMods) = defaults[slot.Slot];
            if (slot.GetKey() == defaultKey && slot.GetMods() == defaultMods)
            {
                continue;
            }

            // 泊位须避开：默认值集合、以及其它槽位当前已占的键
            foreach (var park in parkingKeys)
            {
                if (defaultKeys.Contains(park) ||
                    slots.Any(other => other.Slot != slot.Slot && other.GetKey() == park))
                {
                    continue;
                }

                if (slot.Set(park, ModifierKeys.None))
                {
                    break;
                }
            }
        }

        // 逐个写默认：泊位已腾出目标组合，正常应全部成功
        foreach (var slot in slots)
        {
            var (defaultKey, defaultMods) = defaults[slot.Slot];
            slot.Set(defaultKey, defaultMods);
        }
    }

    /// <summary>全局热键生效范围（黑名单外 / 全部应用 / 关闭），对所有内置热键统一生效。</summary>
    public HotkeyScope HotkeyScope
    {
        get => _settings.HotkeyScope;
        set
        {
            if (!Enum.IsDefined(value) || _settings.HotkeyScope == value)
            {
                return;
            }

            _settings.HotkeyScope = value;
            _keyboardHookService.HotkeyScope = value;
            QueueSettingsSave();
            NotifyBrowserState(BrowserStateChangeKind.Mode);
        }
    }

    public string ThemeMode
    {
        get => ThemeService.Normalize(_settings.ThemeMode);        set
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
                UpdateModeToggleButton();
                return;
            }

            _settings.Language = lang;
            LocalizationService.Apply(lang);
            UpdateModeToggleButton();
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
        NotifyBrowserState(BrowserStateChangeKind.Appearance);

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
        NotifyBrowserState(BrowserStateChangeKind.Appearance);
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
            _pendingDownloadProgress.TryRemove(item, out _);
            // 用反向字典 O(1) 查找，替代 FirstOrDefault O(n) 线性扫描
            if (_operationsByItem.TryGetValue(item, out var operation))
            {
                DetachDownloadOperation(operation);
            }

            SetStatusMessage(LocalizationService.Format("Status.DownloadCanceled", item.FileName), StatusLevel.Success);
            DownloadsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RetryDownload(DownloadItem item)
    {
        if (_isShuttingDown || !item.CanRetry ||
            !EntryText.TryValidateHttpUrl(item.SourceUri, out var retryUri))
        {
            SetStatusMessage(LocalizationService.Get("Status.DownloadRetryUnavailable"), StatusLevel.Warning);
            return;
        }

        CoreWebView2? core;
        try
        {
            core = BrowserView.CoreWebView2;
        }
        catch
        {
            core = null;
        }

        if (core is null)
        {
            SetStatusMessage(LocalizationService.Get("Status.BrowserNotReady"), StatusLevel.Warning);
            return;
        }

        try
        {
            // 不在当前文档 Navigate：优先隐藏 iframe 触发下载，保留攻略/视频页。
            // pending 仅在 URI 匹配或过期时消费；脚本失败时回退宿主级 Navigate。
            BeginPendingDownloadRetry(item, retryUri);
            _ = TriggerDownloadRetryAsync(core, retryUri, item.FileName);
        }
        catch (Exception ex)
        {
            ClearPendingDownloadRetry();
            FileLogger.LogException(ex, "Retry download");
            SetStatusMessage(
                LocalizationService.Format("Status.DownloadRetryFailed", ex.Message),
                StatusLevel.Error);
        }
    }

    private void BeginPendingDownloadRetry(DownloadItem item, string retryUri)
    {
        _pendingDownloadRetry = new PendingDownloadRetry(
            item,
            retryUri,
            DateTime.UtcNow.AddSeconds(DownloadRetryPendingSeconds));
        SchedulePendingDownloadRetryWatchdog();
    }

    private void ClearPendingDownloadRetry()
    {
        _pendingDownloadRetry = null;
        CancelPendingDownloadRetryWatchdog();
    }

    private void SchedulePendingDownloadRetryWatchdog()
    {
        CancelPendingDownloadRetryWatchdog();
        _pendingDownloadRetryWatchdog = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(DownloadRetryPendingSeconds),
        };
        _pendingDownloadRetryWatchdog.Tick += PendingDownloadRetryWatchdog_OnTick;
        _pendingDownloadRetryWatchdog.Start();
    }

    private void CancelPendingDownloadRetryWatchdog()
    {
        if (_pendingDownloadRetryWatchdog is null)
        {
            return;
        }

        _pendingDownloadRetryWatchdog.Stop();
        _pendingDownloadRetryWatchdog.Tick -= PendingDownloadRetryWatchdog_OnTick;
        _pendingDownloadRetryWatchdog = null;
    }

    private void PendingDownloadRetryWatchdog_OnTick(object? sender, EventArgs e)
    {
        CancelPendingDownloadRetryWatchdog();
        if (_pendingDownloadRetry is null || _isShuttingDown)
        {
            return;
        }

        // 超时仍未匹配到 DownloadStarting：常见于 CSP 拦 iframe、非附件响应、或 URI 与 SourceUri 完全无关
        var fileName = _pendingDownloadRetry.Item.FileName;
        ClearPendingDownloadRetry();
        SetStatusMessage(
            LocalizationService.Format("Status.DownloadRetryTimedOut", fileName),
            StatusLevel.Warning);
    }

    private async Task TriggerDownloadRetryAsync(CoreWebView2 core, string retryUri, string fileName)
    {
        try
        {
            // 优先 iframe：不挤掉当前页。用 JSON 字符串嵌入避免手工转义漏洞。
            var uriLiteral = System.Text.Json.JsonSerializer.Serialize(retryUri);
            var script =
                "(function(){" +
                "try{" +
                "if(!document||!document.documentElement)return 'no-dom';" +
                "var f=document.createElement('iframe');" +
                "f.style.cssText='position:fixed;width:0;height:0;border:0;left:-9999px;top:-9999px;';" +
                "f.src=" + uriLiteral + ";" +
                "document.documentElement.appendChild(f);" +
                "setTimeout(function(){try{f.remove();}catch(e){}},60000);" +
                "return 'ok';" +
                "}catch(e){return 'error';}" +
                "})();";

            var result = await core.ExecuteScriptAsync(script).ConfigureAwait(true);
            if (_isShuttingDown)
            {
                return;
            }

            // ExecuteScriptAsync 返回 JSON 字符串，成功时约 "\"ok\""
            if (result is not null && result.Contains("ok", StringComparison.Ordinal))
            {
                SetStatusMessage(
                    LocalizationService.Format("Status.DownloadRetrying", fileName),
                    StatusLevel.Info);
                return;
            }

            // 无 DOM / 脚本失败：回退宿主级 Navigate（会离开当前页，但能绕过 CSP/无 document）
            FileLogger.LogDebug($"Retry download iframe failed (result={result}); falling back to Navigate");
            if (!IsPendingDownloadRetryActiveFor(retryUri))
            {
                return;
            }

            SetNavigating(true);
            core.Navigate(retryUri);
            SetStatusMessage(
                LocalizationService.Format("Status.DownloadRetryNavigateFallback", fileName),
                StatusLevel.Warning);
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Retry download via iframe");
            if (_isShuttingDown)
            {
                return;
            }

            // 异常路径同样尝试宿主 Navigate 回退
            try
            {
                if (!IsPendingDownloadRetryActiveFor(retryUri))
                {
                    return;
                }

                SetNavigating(true);
                core.Navigate(retryUri);
                SetStatusMessage(
                    LocalizationService.Format("Status.DownloadRetryNavigateFallback", fileName),
                    StatusLevel.Warning);
            }
            catch (Exception navEx)
            {
                ClearPendingDownloadRetry();
                FileLogger.LogException(navEx, "Retry download Navigate fallback");
                SetNavigating(false);
                SetStatusMessage(
                    LocalizationService.Format("Status.DownloadRetryFailed", navEx.Message),
                    StatusLevel.Error);
            }
        }
    }

    private bool IsPendingDownloadRetryActiveFor(string retryUri)
    {
        var pending = _pendingDownloadRetry;
        return pending is not null &&
               pending.ExpiresAtUtc >= DateTime.UtcNow &&
               string.Equals(pending.SourceUri, retryUri, StringComparison.Ordinal);
    }

    public void OpenDownloadFile(DownloadItem item)
    {
        if (!_downloadsService.OpenFile(item))
        {
            SetStatusMessage(LocalizationService.Get("Status.CannotOpenFile"), StatusLevel.Warning);
        }
    }

    public void OpenDownloadFolder(DownloadItem item)
    {
        if (!_downloadsService.OpenFolder(item))
        {
            SetStatusMessage(LocalizationService.Get("Status.CannotOpenFolder"), StatusLevel.Warning);
        }
    }

    public void ClearFinishedDownloads()
    {
        _downloadsService.ClearFinished();
        DownloadsChanged?.Invoke(this, EventArgs.Empty);
    }

    private DownloadItem? TakePendingDownloadRetry(string sourceUri)
    {
        var pending = _pendingDownloadRetry;
        if (pending is null)
        {
            return null;
        }

        // 过期或条目不可用：清掉，避免悬挂
        if (pending.ExpiresAtUtc < DateTime.UtcNow ||
            !Downloads.Contains(pending.Item) ||
            !pending.Item.CanRetry)
        {
            ClearPendingDownloadRetry();
            return null;
        }

        // 不匹配则保留 pending（keep-until-match）：CDN 重定向 / 无关下载不应吃掉重试窗口
        if (!DownloadUrisMatch(pending.SourceUri, sourceUri))
        {
            return null;
        }

        ClearPendingDownloadRetry();
        return pending.Item;
    }

    /// <summary>
    /// 重试 URI 匹配：host 大小写不敏感；path 忽略默认端口与末尾斜杠；query 按键值集合比较（顺序无关）。
    /// token 大小写仍敏感，避免把不同签名当成同一下载。
    /// </summary>
    internal static bool DownloadUrisMatch(string expected, string actual)
    {
        if (!Uri.TryCreate(expected, UriKind.Absolute, out var expectedUri) ||
            !Uri.TryCreate(actual, UriKind.Absolute, out var actualUri))
        {
            return false;
        }

        if (!string.Equals(expectedUri.Scheme, actualUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expectedUri.Host, actualUri.Host, StringComparison.OrdinalIgnoreCase) ||
            expectedUri.Port != actualUri.Port)
        {
            return false;
        }

        var expectedPath = expectedUri.AbsolutePath.TrimEnd('/');
        var actualPath = actualUri.AbsolutePath.TrimEnd('/');
        if (!string.Equals(expectedPath, actualPath, StringComparison.Ordinal))
        {
            return false;
        }

        return QuerySetsEqual(expectedUri.Query, actualUri.Query);
    }

    private static bool QuerySetsEqual(string expectedQuery, string actualQuery)
    {
        static Dictionary<string, string> Parse(string query)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(query))
            {
                return map;
            }

            var q = query[0] == '?' ? query[1..] : query;
            foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq < 0)
                {
                    map[Uri.UnescapeDataString(part)] = string.Empty;
                }
                else
                {
                    var key = Uri.UnescapeDataString(part[..eq]);
                    var value = Uri.UnescapeDataString(part[(eq + 1)..]);
                    map[key] = value;
                }
            }

            return map;
        }

        var a = Parse(expectedQuery);
        var b = Parse(actualQuery);
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var pair in a)
        {
            if (!b.TryGetValue(pair.Key, out var other) ||
                !string.Equals(pair.Value, other, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void ModeToggleButton_OnClick(object sender, RoutedEventArgs e) => ToggleWindowMode();

    private void MinButton_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxButton_OnClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    /// <summary>
    /// 同步标题栏模式按钮图标与动作型 tooltip：浮窗=锁，浏览=解锁。
    /// 快捷键文案跟当前设置走，避免改键后仍显示默认 F8。
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
            var hotkey = FormatToggleModeHotkey();
            // 动作型 tooltip：写清点下去会进入哪一侧
            ModeToggleButton.ToolTip = _settings.WindowMode == WindowMode.Fixed
                ? LocalizationService.Format("Mode.SwitchToBrowsingTooltip", hotkey)
                : LocalizationService.Format("Mode.SwitchToFloatingTooltip", hotkey);
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

        if (_modeToastTimer is null)
        {
            _modeToastTimer = new DispatcherTimer();
            _modeToastTimer.Tick += ModeToastTimer_OnTick;
        }
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
    /// 模式切换时主窗边缘短暂描边闪（浏览蓝 / 浮窗橙，色值来自主题 token）。
    /// </summary>
    private void FlashModeBorder(bool floating)
    {
        if (ModeFlashBorder is null)
        {
            return;
        }

        var key = floating ? "ModeFloatingAccent" : "ModeBrowsingAccent";
        ModeFlashBorder.BorderBrush = TryFindResource(key) as System.Windows.Media.Brush
            ?? new System.Windows.Media.SolidColorBrush(
                floating
                    ? System.Windows.Media.Color.FromRgb(0xF0, 0xA0, 0x20)
                    : System.Windows.Media.Color.FromRgb(0x58, 0xA6, 0xFF));

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
            var work = WindowBoundsHelper.GetWorkArea(this);
            // 先置标志再赋值：Left/Top/Width/Height 赋值会同步触发 LocationChanged/SizeChanged，
            // 守卫若未生效会把最大化边界写入 settings，关闭时丢失还原前的窗口位置。
            _isMaximized = true;
            Left = work.Left;
            Top = work.Top;
            Width = work.Width;
            Height = work.Height;
            MaxIcon.Text = "\uE73F";
        }
        else
        {
            // 赋值期间 _isMaximized 仍为 true：还原后的边界不落盘，等用户随后的移动/缩放再写
            Left = _savedBounds.Left;
            Top = _savedBounds.Top;
            Width = _savedBounds.Width;
            Height = _savedBounds.Height;
            _isMaximized = false;
            MaxIcon.Text = "\uE740";
        }

        // 赋值期间的位置/尺寸事件已被 _isMaximized 守卫拦截，
        // 控制窗跟随与尺寸显示在这里显式补一次（防抖）
        QueueControlWindowBoundsUiRefresh();
    }

    private void RestoreFromMaximizeOnDrag(MouseButtonEventArgs e)
    {
        // 拖动最大化窗口标题栏时还原窗口尺寸，并让窗口跟随鼠标
        var work = WindowBoundsHelper.GetWorkArea(this);
        var ratio = e.GetPosition(this).X / ActualWidth;
        ToggleMaximize();
        // GetPosition(null) 返回的是事件触发时刻鼠标相对「最大化窗口」原点的客户区坐标：
        // MouseDevice 缓存最后一次鼠标移动时的客户区坐标，窗口移动后仅按当前窗口位置
        // 做 client→screen→client 往返，返回值不随窗口移动变化。而自定义最大化的原点
        // 恰为 work.Left/work.Top（见 ToggleMaximize），故鼠标屏幕坐标 = work 原点 + pos。
        var pos = e.GetPosition(null);
        Left = work.Left + pos.X - _savedBounds.Width * ratio;
        Top = work.Top + pos.Y - SystemParameters.CaptionHeight / 2.0;
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
        if (_closeCleanupStarted)
        {
            return;
        }

        _closeCleanupStarted = true;

        try
        {
            await CleanupAndSaveAsync();
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Unhandled main window cleanup failure");
        }
        finally
        {
            // 重新调用 Close，此时 _isRealClose 为 true，将直接返回并退出
            _isRealClose = true;
            Close();
        }
    }

    private async Task CleanupAndSaveAsync()
    {
        _isShuttingDown = true;

        await StopAutoCacheCheckAsync();

        // 立即停止键盘钩子
        try
        {
            _keyboardHookService.Dispose();
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Dispose keyboard hook");
        }

        // 取消前台状态跟踪
        StateChanged -= MainWindow_OnStateChanged;
        if (Application.Current is not null)
        {
            Application.Current.Activated -= App_OnActivated;
            Application.Current.Deactivated -= App_OnDeactivated;
        }

        // 取消待处理的保存操作与历史记录延迟任务
        if (_settingsSaveTimer is not null)
        {
            _settingsSaveTimer.Stop();
            _settingsSaveTimer.Tick -= SettingsSaveTimer_OnTick;
            _settingsSaveTimer = null;
        }

        _recordHistoryCts?.Cancel();
        _recordHistoryCts?.Dispose();
        _recordHistoryCts = null;

        if (_mouseLeaveWatchTimer is not null)
        {
            _mouseLeaveWatchTimer.Stop();
            _mouseLeaveWatchTimer.Tick -= MouseLeaveWatchTimer_OnTick;
            _mouseLeaveWatchTimer = null;
        }

        _keyboardHookService.KPressed -= KeyboardHookService_OnKPressed;
        _keyboardHookService.ModeTogglePressed -= KeyboardHookService_OnModeTogglePressed;
        _keyboardHookService.HideTogglePressed -= KeyboardHookService_OnHideTogglePressed;
        _keyboardHookService.SeekBackwardPressed -= KeyboardHookService_OnSeekBackwardPressed;
        _keyboardHookService.SeekForwardPressed -= KeyboardHookService_OnSeekForwardPressed;

        if (_windowBoundsUiDebounceTimer is not null)
        {
            _windowBoundsUiDebounceTimer.Stop();
            _windowBoundsUiDebounceTimer.Tick -= WindowBoundsUiDebounceTimer_OnTick;
            _windowBoundsUiDebounceTimer = null;
        }

        if (_titleBarHideTimer is not null)
        {
            _titleBarHideTimer.Stop();
            _titleBarHideTimer.Tick -= TitleBarHideTimer_OnTick;
            _titleBarHideTimer = null;
        }

        if (_modeToastTimer is not null)
        {
            _modeToastTimer.Stop();
            _modeToastTimer.Tick -= ModeToastTimer_OnTick;
            _modeToastTimer = null;
        }

        DetachAllDownloadOperations(markInterrupted: true);
        ClearPendingDownloadRetry();

        // 取消事件订阅
        LocationChanged -= MainWindow_OnLocationOrSizeChanged;
        SizeChanged -= MainWindow_OnLocationOrSizeChanged;

        // 立即关闭控制窗口（不等待保存）
        if (_controlWindow is not null)
        {
            _controlWindow.SaveWindowBounds();
            _controlWindow.AllowClose = true;
            _controlWindow.Close();
            _controlWindow = null;
        }

        // 保存窗口位置到内存（最大化时保留还原前的边界）
        SaveWindowBounds();

        // 退出前强制用当前页面 URL 刷新 LastUrl，避免 SPA 导航 / 防抖取消导致恢复到旧地址
        CaptureCurrentUrlToSettings();
        CaptureCurrentZoomToSettings();

        try
        {
            DetachBrowserEvents(BrowserView);
            DetachCursorGuard(BrowserView);
            BrowserHost.Children.Remove(BrowserView);
            BrowserView.Dispose();
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Dispose WebView2");
        }

        try
        {
            await _settingsService.SaveAsync(_settings);
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Save settings before close");
        }

        try
        {
            await _historyService.FlushAsync();
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Flush history before close");
        }

        try
        {
            await _favoritesService.FlushAsync();
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Flush favorites before close");
        }

        try
        {
            await _downloadsService.FlushAsync();
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Flush downloads before close");
        }

        try { _settingsService.Dispose(); }
        catch (Exception ex) { FileLogger.LogException(ex, "Dispose settings service"); }

        try { _historyService.Dispose(); }
        catch (Exception ex) { FileLogger.LogException(ex, "Dispose history service"); }

        try { _favoritesService.Dispose(); }
        catch (Exception ex) { FileLogger.LogException(ex, "Dispose favorites service"); }

        try { _downloadsService.Dispose(); }
        catch (Exception ex) { FileLogger.LogException(ex, "Dispose downloads service"); }

        // 最后等待后台日志队列排空，避免进程退出丢日志。
        await FileLogger.FlushAsync();
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

        if (_windowBoundsUiDebounceTimer is null)
        {
            _windowBoundsUiDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(AppConfig.Ui.WindowBoundsUiDebounceMs),
            };
            _windowBoundsUiDebounceTimer.Tick += WindowBoundsUiDebounceTimer_OnTick;
        }
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
        if (WindowBoundsHelper.HasHandle(this))
        {
            WindowBoundsHelper.ClampToWorkArea(this);
        }
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
        if (_isShuttingDown)
        {
            return;
        }

        if (!_keyboardHookService.Start(out var errorCode))
        {
            // Dispose 后 Start 失败是关闭竞态的预期结果，不弹错误
            if (errorCode == KeyboardHookService.ObjectDisposedErrorCode)
            {
                return;
            }

            SetStatusMessage(LocalizationService.Format("Status.HotkeyInstallFailed", errorCode), StatusLevel.Error);
        }
    }

    /// <summary>
    /// 录制全局快捷键期间暂停内置热键，避免 PreviewKeyDown 挡不住 WH_KEYBOARD_LL。
    /// </summary>
    public void SetHotkeyRecordingActive(bool active)
    {
        _keyboardHookService.SuspendBuiltInHotkeys = active;
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
        // 用 DispatcherTimer 防抖，避免每次 UI 事件 new CTS + Cancel + Dispose 的分配开销。
        // 拖拽/缩放/透明度/导航等 16+ 处调用，拖拽时 30-60 次/秒。
        _settingsSaveTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(AppConfig.Ui.SettingsSaveDebounceMs),
        };
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Tick -= SettingsSaveTimer_OnTick;
        _settingsSaveTimer.Tick += SettingsSaveTimer_OnTick;
        _settingsSaveTimer.Start();
    }

    private async void SettingsSaveTimer_OnTick(object? sender, EventArgs e)
    {
        _settingsSaveTimer?.Stop();
        await SaveSettingsAsync().ConfigureAwait(true);
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

    /// <summary>
    /// 按切片通知控制窗刷新。浮窗模式下控制窗已 Hide 时仍推送，便于再次显示时数据一致；
    /// ViewModel 侧按 Kind 跳过列表同步。
    /// </summary>
    private void NotifyBrowserState(BrowserStateChangeKind kind)
    {
        if (kind == BrowserStateChangeKind.None || _isShuttingDown)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            // 非 UI 线程：异步投递，避免阻塞 WebView2 / 钩子回调
            _ = Dispatcher.InvokeAsync(() => NotifyBrowserState(kind));
            return;
        }

        BrowserStateChanged?.Invoke(this, new BrowserStateChangedEventArgs(kind));
    }

    private void SetStatusMessage(string message, StatusLevel level = StatusLevel.Info, BrowserStateChangeKind extra = BrowserStateChangeKind.None)
    {
        _statusMessage = message;
        _lastStatusLevel = level;
        // 默认仅刷状态/Toast；调用方可合并 Navigation/History 等切片，避免连续两次刷新
        NotifyBrowserState(BrowserStateChangeKind.Status | extra);
    }

    /// <summary>
    /// 导航结束统一收口：清加载态 + 状态文案 + Navigation（及可选 History 等）一次合并通知。
    /// 禁止在成功路径裸写 <c>_isNavigating</c>，避免日后漏带 Navigation 切片导致加载条卡住。
    /// </summary>
    private void CompleteNavigation(string message, StatusLevel level, BrowserStateChangeKind extra = BrowserStateChangeKind.None)
    {
        _isNavigating = false;
        SetStatusMessage(message, level, BrowserStateChangeKind.Navigation | extra);
    }

    private void SetNavigating(bool isNavigating)
    {
        if (_isNavigating == isNavigating)
        {
            return;
        }

        _isNavigating = isNavigating;
        NotifyBrowserState(BrowserStateChangeKind.Navigation);
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
        if (_titleBarHideTimer is null)
        {
            _titleBarHideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _titleBarHideTimer.Tick += TitleBarHideTimer_OnTick;
        }
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
        // 不再写 LogDebug：滑块拖动会高频触发，原本会让单日日志膨胀。
    }

    internal static bool ShouldEnableBrowserAcceleratorKeys(WindowMode mode) => mode == WindowMode.Free;

    private void UpdateBrowserAcceleratorKeys(CoreWebView2? core = null)
    {
        try
        {
            core ??= BrowserView.CoreWebView2;
            if (core is not null)
            {
                core.Settings.AreBrowserAcceleratorKeysEnabled = ShouldEnableBrowserAcceleratorKeys(_settings.WindowMode);
            }
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Update browser accelerator keys");
        }
    }

    private void ApplyStoredZoom(WebView2CompositionControl browser)
    {
        var zoom = Math.Clamp(_settings.ZoomFactor, 0.25, 5.0);
        _settings.ZoomFactor = zoom;
        _isApplyingZoom = true;
        try
        {
            browser.ZoomFactor = zoom;
        }
        finally
        {
            _isApplyingZoom = false;
        }
    }

    private void StoreZoomFactor(double factor)
    {
        var clamped = Math.Clamp(factor, 0.25, 5.0);
        if (Math.Abs(_settings.ZoomFactor - clamped) <= 0.0001)
        {
            return;
        }

        _settings.ZoomFactor = clamped;
        QueueSettingsSave();
    }

    private void CaptureCurrentZoomToSettings()
    {
        try
        {
            if (BrowserView.CoreWebView2 is not null)
            {
                _settings.ZoomFactor = Math.Clamp(BrowserView.ZoomFactor, 0.25, 5.0);
            }
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Capture zoom before close");
        }
    }

    private void SetZoom(double factor)
    {
        var clamped = Math.Clamp(factor, 0.25, 5.0);
        var browserChanged = false;
        try
        {
            if (BrowserView.CoreWebView2 is not null && Math.Abs(BrowserView.ZoomFactor - clamped) > 0.0001)
            {
                _isApplyingZoom = true;
                try
                {
                    BrowserView.ZoomFactor = clamped;
                    browserChanged = true;
                }
                finally
                {
                    _isApplyingZoom = false;
                }
            }
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Set WebView2 zoom factor");
        }

        var storedChanged = Math.Abs(_settings.ZoomFactor - clamped) > 0.0001;
        StoreZoomFactor(clamped);
        if (browserChanged || storedChanged)
        {
            ZoomChanged?.Invoke(this, EventArgs.Empty);
        }
        // ZoomChanged 已更新百分比；状态走 Status 切片即可
        SetStatusMessage(LocalizationService.Format("Status.Zoom", Math.Round(clamped * 100)), StatusLevel.Info);
    }

    private void ZoomBy(double delta)
    {
        // 勿裸读 BrowserView.ZoomFactor：WebView 未就绪时会抛；与 ZoomFactor getter 一致回退到 settings。
        double current;
        try
        {
            current = BrowserView.CoreWebView2 is null ? _settings.ZoomFactor : BrowserView.ZoomFactor;
        }
        catch
        {
            current = _settings.ZoomFactor;
        }

        SetZoom(current + delta);
    }

    private sealed record PendingDownloadRetry(
        DownloadItem Item,
        string SourceUri,
        DateTime ExpiresAtUtc);
}
