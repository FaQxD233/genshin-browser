using GenshinBrowser.Constants;
using GenshinBrowser.Localization;
using GenshinBrowser.Interop;
using GenshinBrowser.Models;
using GenshinBrowser.Services;
using GenshinBrowser.Threading;
using GenshinBrowser.Utils;
using GenshinBrowser.Windowing;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System.Globalization;

namespace GenshinBrowser.Browser;

internal sealed partial class BrowserSession : IBrowserSession, IAsyncDisposable
{
    private const string CursorBootstrapScript = """
        (() => {
          try {
            if (document.getElementById('gb-cursor-bootstrap-style')) return;
            const style = document.createElement('style');
            style.id = 'gb-cursor-bootstrap-style';
            style.textContent = [
              'video,video *{cursor:default !important;}',
              '.bpx-player-container,.bpx-player-video-wrap,.bpx-player-video-area,',
              '.bpx-player-video-perch,.bilibili-player,.bilibili-player-area,',
              '.bilibili-player-video-wrap,.bilibili-player-video-perch{cursor:default !important;}'
            ].join('');
            (document.documentElement || document.head || document.body).appendChild(style);
          } catch (_) {}
        })();
        """;
    private readonly IWebViewHost webViewHost;
    private readonly nint ownerWindowHandle;
    private readonly WebViewEnvironmentProvider environmentProvider;
    private readonly SettingsService settingsService;
    private readonly HistoryService historyService;
    private readonly FavoritesService favoritesService;
    private readonly DownloadsService downloadsService;
    private readonly KeyboardHookService keyboardHookService;
    private readonly IWindowModeService windowModeService;
    private readonly IWindowPlacementService windowPlacementService;
    private readonly IWindowTransparencyService windowTransparencyService;
    private readonly IUiDispatcher dispatcher;
    private readonly IUiTimerFactory timerFactory;
    private readonly ITextResourceService text;
    private readonly AppSettings settings;
    private readonly CancellationTokenSource lifetimeCts = new();
    private WebView2 webView;
    private CoreWebView2? core;
    private CancellationTokenSource? settingsSaveCts;
    private CancellationTokenSource? historyCaptureCts;
    private CancellationTokenSource? cacheCheckCts;
    private Task settingsSaveTask = Task.CompletedTask;
    private Task historyCaptureTask = Task.CompletedTask;
    private Task? cacheCheckTask;
    private string currentAddress = string.Empty;
    private string statusMessage = "正在初始化浏览器...";
    private StatusLevel lastStatusLevel = StatusLevel.Info;
    private bool isNavigating;
    private bool initialized;
    private bool shuttingDown;
    private bool browserRecoveryInProgress;
    private string? zoomBootstrapScriptId;
    private Task zoomApplyTask = Task.CompletedTask;
    private int rendererUnresponsiveCount;
    private DateTime lastRendererUnresponsiveUtc;

    internal string ModeToastMessage { get; private set; } = string.Empty;

    internal TimeSpan ModeToastDuration { get; private set; } = TimeSpan.FromSeconds(1.1);

    public BrowserSession(
        IWebViewHost webViewHost,
        nint ownerWindowHandle,
        WebViewEnvironmentProvider environmentProvider,
        SettingsService settingsService,
        AppSettings settings,
        HistoryService historyService,
        FavoritesService favoritesService,
        DownloadsService downloadsService,
        KeyboardHookService keyboardHookService,
        IWindowModeService windowModeService,
        IWindowPlacementService windowPlacementService,
        IWindowTransparencyService windowTransparencyService,
        IUiDispatcher dispatcher,
        IUiTimerFactory timerFactory,
        ITextResourceService text)
    {
        this.webViewHost = webViewHost;
        this.ownerWindowHandle = ownerWindowHandle;
        this.environmentProvider = environmentProvider;
        this.settingsService = settingsService;
        this.settings = settings;
        this.historyService = historyService;
        this.favoritesService = favoritesService;
        this.downloadsService = downloadsService;
        this.keyboardHookService = keyboardHookService;
        this.windowModeService = windowModeService;
        this.windowPlacementService = windowPlacementService;
        this.windowTransparencyService = windowTransparencyService;
        this.dispatcher = dispatcher;
        this.timerFactory = timerFactory;
        this.text = text;
        webView = webViewHost.CurrentWebView;

        windowPlacementService.Restore(new WindowBounds(
            settings.WindowLeft,
            settings.WindowTop,
            settings.WindowWidth,
            BrowserWindowMetrics.ToOuterHeight(settings.WindowHeight, settings.WindowMode)));
        windowModeService.Apply(settings.WindowMode);
        windowTransparencyService.Apply(settings.WindowOpacity);
        text.Apply(settings.Language);
        statusMessage = text.Get("Status.InitBrowser", "正在初始化浏览器...");

        keyboardHookService.KPressed += KeyboardHookOnPlaybackPressed;
        keyboardHookService.ModeTogglePressed += KeyboardHookOnModeTogglePressed;
    }

    public event EventHandler<BrowserStateChangedEventArgs>? BrowserStateChanged;

    public event EventHandler? DownloadsChanged;

    public event EventHandler? ThemeChanged;

    public event EventHandler? LanguageChanged;

    public WindowMode CurrentMode => settings.WindowMode;

    public double WindowOpacity
    {
        get => settings.WindowOpacity;
        set
        {
            double clamped = double.IsFinite(value) ? Math.Clamp(value, 0.1, 1.0) : 1.0;
            try
            {
                bool shouldBeLayered = clamped < 1.0;
                if (settings.WindowOpacity == clamped &&
                    windowTransparencyService.Opacity == clamped &&
                    windowTransparencyService.IsLayered == shouldBeLayered)
                {
                    return;
                }

                windowTransparencyService.Apply(clamped);
            }
            catch (Exception ex)
            {
                FileLogger.LogException(ex, "Apply browser window opacity");
                SetStatus(
                    text.Format("Status.OpacityFailed", "设置窗口不透明度失败：{0}", ex.Message),
                    StatusLevel.Error);
                Notify(BrowserStateChangeKind.Opacity);
                return;
            }

            bool settingChanged = settings.WindowOpacity != clamped;
            settings.WindowOpacity = clamped;
            if (settingChanged)
            {
                QueueSettingsSave();
            }
            Notify(BrowserStateChangeKind.Opacity);
        }
    }

    public HotkeyGesture ToggleModeHotkey => settings.ToggleModeHotkey;

    public HotkeyGesture TogglePlaybackHotkey => settings.TogglePlaybackHotkey;

    public string CurrentAddress => currentAddress;

    public string DocumentTitle => ReadDocumentTitle();

    public string StatusMessage => statusMessage;

    public StatusLevel LastStatusLevel => lastStatusLevel;

    public bool CanGoBack => core?.CanGoBack ?? false;

    public bool CanGoForward => core?.CanGoForward ?? false;

    public bool IsNavigating => isNavigating;

    public double ZoomFactor
    {
        get => settings.ZoomFactor;
        set => SetZoom(value);
    }

    public string ThemeMode
    {
        get => NormalizeTheme(settings.ThemeMode);
        set
        {
            string normalized = NormalizeTheme(value);
            if (string.Equals(settings.ThemeMode, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            settings.ThemeMode = normalized;
            QueueSettingsSave();
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string UiLanguage
    {
        get => NormalizeLanguage(settings.Language);
        set
        {
            string normalized = NormalizeLanguage(value);
            if (string.Equals(settings.Language, normalized, StringComparison.OrdinalIgnoreCase))
            {
                text.Apply(normalized);
                return;
            }

            settings.Language = normalized;
            text.Apply(normalized);
            QueueSettingsSave();
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public double BrowserWindowWidth
    {
        get => windowPlacementService.Capture().Width;
    }

    public double BrowserWindowHeight
    {
        get => BrowserWindowMetrics.ToContentHeight(windowPlacementService.Capture().Height, settings.WindowMode);
    }

    public IReadOnlyList<DownloadItem> Downloads => downloadsService.Downloads;

    public IReadOnlyList<HistoryEntry> HistoryEntries => historyService.GetEntries();

    public IReadOnlyList<FavoriteEntry> FavoriteEntries => favoritesService.GetEntries();

    public async Task InitializeAsync()
    {
        if (initialized || shuttingDown)
        {
            return;
        }

        initialized = true;
        SetStatus(text.Get("Status.InitBrowser", "正在初始化浏览器..."), StatusLevel.Info);

        keyboardHookService.TrySetToggleModeHotkey(settings.ToggleModeKey, settings.ToggleModeModifiers);
        keyboardHookService.TrySetTogglePlaybackHotkey(settings.TogglePlaybackKey, settings.TogglePlaybackModifiers);
        keyboardHookService.IsGamingMode = settings.WindowMode == WindowMode.Fixed;
        if (!keyboardHookService.Start(out int hookError))
        {
            SetStatus(text.Format(
                "Status.HotkeyInstallFailed",
                "全局热键安装失败，Win32 错误码: {0}。控制面板按钮仍可使用。",
                hookError), StatusLevel.Warning);
        }

        await InitializeWebViewAsync(NavigationTarget.GetStartupUrl(settings.LastUrl)).ConfigureAwait(true);
    }

    public bool TrySetToggleModeHotkey(HotkeyGesture gesture)
    {
        if (!gesture.IsValid || gesture == settings.TogglePlaybackHotkey)
        {
            return false;
        }

        if (!keyboardHookService.TrySetToggleModeHotkey(gesture.VirtualKey, gesture.Modifiers))
        {
            return false;
        }

        settings.ToggleModeKey = gesture.VirtualKey;
        settings.ToggleModeModifiers = gesture.Modifiers;
        QueueSettingsSave();
        Notify(BrowserStateChangeKind.Hotkeys);
        return true;
    }

    public bool TrySetTogglePlaybackHotkey(HotkeyGesture gesture)
    {
        if (!gesture.IsValid || gesture == settings.ToggleModeHotkey)
        {
            return false;
        }

        if (!keyboardHookService.TrySetTogglePlaybackHotkey(gesture.VirtualKey, gesture.Modifiers))
        {
            return false;
        }

        settings.TogglePlaybackKey = gesture.VirtualKey;
        settings.TogglePlaybackModifiers = gesture.Modifiers;
        QueueSettingsSave();
        Notify(BrowserStateChangeKind.Hotkeys);
        return true;
    }

    public bool IsFavorite(string url) => favoritesService.Contains(url);

    public void GoBack()
    {
        try
        {
            if (core?.CanGoBack == true)
            {
                core.GoBack();
            }
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Navigate back");
        }
    }

    public void GoForward()
    {
        try
        {
            if (core?.CanGoForward == true)
            {
                core.GoForward();
            }
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Navigate forward");
        }
    }

    public void ReloadPage()
    {
        if (core is null)
        {
            SetStatus(text.Get("Status.BrowserNotReady", "浏览器尚未就绪"), StatusLevel.Warning);
            return;
        }

        try
        {
            core.Reload();
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Reload page");
            SetStatus(text.Format("Status.ReloadFailed", "刷新失败：{0}", ex.Message), StatusLevel.Error);
        }
    }

    public void NavigateTo(string? input)
    {
        if (core is null)
        {
            SetStatus(text.Get("Status.BrowserNotReady", "浏览器尚未就绪"), StatusLevel.Warning);
            return;
        }

        string? target = NavigationTarget.Build(input);
        if (target is null)
        {
            SetStatus(text.Get("Status.OnlyHttp", "仅支持 HTTP/HTTPS 地址"), StatusLevel.Warning);
            return;
        }

        string previousAddress = currentAddress;
        try
        {
            SetNavigating(true);
            currentAddress = target;
            core.Navigate(target);
            SetStatus(
                text.Format("Status.Opening", "正在打开 {0}", target),
                StatusLevel.Info,
                BrowserStateChangeKind.Navigation);
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Navigate to address");
            currentAddress = previousAddress;
            SetNavigating(false);
            SetStatus(
                text.Format("Status.OpenFailed", "打开失败：{0}", ex.Message),
                StatusLevel.Error,
                BrowserStateChangeKind.Navigation);
        }
    }

    public void ToggleWindowMode()
    {
        SetWindowMode(settings.WindowMode == WindowMode.Fixed ? WindowMode.Free : WindowMode.Fixed);
    }

    public void SetWindowMode(WindowMode mode)
    {
        if (settings.WindowMode == mode)
        {
            return;
        }

        CaptureWindowBounds();
        settings.WindowMode = mode;
        windowModeService.Apply(mode);
        windowPlacementService.Resize(
            settings.WindowWidth,
            BrowserWindowMetrics.ToOuterHeight(settings.WindowHeight, mode));
        keyboardHookService.IsGamingMode = mode == WindowMode.Fixed;
        if (core is not null)
        {
            try
            {
                core.Settings.AreBrowserAcceleratorKeysEnabled = BrowserModeRules.ShouldEnableAcceleratorKeys(mode);
            }
            catch (Exception ex)
            {
                FileLogger.LogException(ex, "Update browser accelerator keys");
            }
        }

        string hotkey = HotkeyFormatter.Format(settings.ToggleModeHotkey);
        bool enteringFloating = mode == WindowMode.Fixed;
        string message;
        if (enteringFloating && !settings.HasSeenFloatingModeHint)
        {
            settings.HasSeenFloatingModeHint = true;
            ModeToastMessage = text.Format(
                "Mode.FirstFloatingHint",
                "已进入浮窗：置顶并隐藏控制台。按 {0} 返回浏览；移到窗口顶部可显示标题栏。",
                hotkey);
            ModeToastDuration = TimeSpan.FromSeconds(3.2);
            message = text.Format(
                "Mode.FixedOn",
                "已进入浮窗：置顶，控制台已隐藏。按 {0} 返回浏览。",
                hotkey);
        }
        else if (enteringFloating)
        {
            ModeToastMessage = text.Get("Mode.ToastFloating", "浮窗");
            ModeToastDuration = TimeSpan.FromSeconds(1.1);
            message = text.Format(
                "Mode.FixedOn",
                "已进入浮窗：置顶，控制台已隐藏。按 {0} 返回浏览。",
                hotkey);
        }
        else
        {
            ModeToastMessage = text.Get("Mode.ToastBrowsing", "浏览");
            ModeToastDuration = TimeSpan.FromSeconds(1.1);
            message = text.Get("Mode.FreeOn", "浏览中：可拖动窗口，控制台已打开。");
        }

        QueueSettingsSave();
        SetStatus(message, StatusLevel.Success, BrowserStateChangeKind.Mode | BrowserStateChangeKind.WindowSize);
    }

    public async Task ToggleVideoPlaybackAsync()
    {
        if (core is null)
        {
            SetStatus(text.Get("Status.BrowserNotReady", "浏览器尚未就绪"), StatusLevel.Warning);
            return;
        }

        const string script = """
            (() => {
              const videos = Array.from(document.querySelectorAll('video'));
              const video = videos.find(v => {
                const rect = v.getBoundingClientRect();
                return rect.width > 0 && rect.height > 0;
              }) || videos[0];
              if (!video) return 'no-video';
              if (video.paused) {
                video.play();
                return 'play';
              }
              video.pause();
              return 'pause';
            })();
            """;

        try
        {
            string result = await core.ExecuteScriptAsync(script);
            if (shuttingDown)
            {
                return;
            }
            switch (result)
            {
                case "\"play\"":
                    SetStatus(text.Get("Status.Played", "已播放"), StatusLevel.Success);
                    break;
                case "\"pause\"":
                    SetStatus(text.Get("Status.Paused", "已暂停"), StatusLevel.Success);
                    break;
                case "\"no-video\"":
                    SetStatus(text.Get("Status.NoVideo", "当前页面没有可播放的视频"), StatusLevel.Warning);
                    break;
                default:
                    SetStatus(text.Get("Status.PlaybackCommandSent", "播放命令已发送"), StatusLevel.Info);
                    break;
            }
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Toggle video playback");
            SetStatus(text.Format("Status.PlaybackFailed", "播放控制失败：{0}", ex.Message), StatusLevel.Error);
        }
    }

    public async Task AddCurrentPageToFavoritesAsync()
    {
        if (string.IsNullOrWhiteSpace(currentAddress))
        {
            return;
        }

        try
        {
            await favoritesService.AddOrUpdateAsync(currentAddress, GetDocumentTitle()).ConfigureAwait(true);
            if (shuttingDown)
            {
                return;
            }
            SetStatus(
                text.Get("Status.FavoriteAdded", "已添加收藏"),
                StatusLevel.Success,
                BrowserStateChangeKind.Favorites | BrowserStateChangeKind.Navigation);
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Add favorite");
            SetStatus(text.Format("Status.FavoriteAddFailed", "添加收藏失败：{0}", ex.Message), StatusLevel.Error);
        }
    }

    public async Task RemoveFavoriteAsync(string url)
    {
        try
        {
            await favoritesService.RemoveAsync(url).ConfigureAwait(true);
            if (shuttingDown)
            {
                return;
            }
            SetStatus(
                text.Get("Status.FavoriteRemoved", "已移除收藏"),
                StatusLevel.Success,
                BrowserStateChangeKind.Favorites | BrowserStateChangeKind.Navigation);
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Remove favorite");
            SetStatus(text.Format("Status.FavoriteRemoveFailed", "移除收藏失败：{0}", ex.Message), StatusLevel.Error);
        }
    }

    public async Task RemoveHistoryEntryAsync(string url)
    {
        try
        {
            await historyService.RemoveAsync(url).ConfigureAwait(true);
            if (shuttingDown)
            {
                return;
            }
            SetStatus(text.Get("Status.HistoryRemoved", "已移除浏览记录"), StatusLevel.Success, BrowserStateChangeKind.History);
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Remove history entry");
            SetStatus(text.Format("Status.HistoryRemoveFailed", "移除浏览记录失败：{0}", ex.Message), StatusLevel.Error);
        }
    }

    public void MoveBrowserToCorner(WindowCorner corner)
    {
        windowPlacementService.MoveToCorner(corner);
        CaptureWindowBounds();
        string message = corner switch
        {
            WindowCorner.TopLeft => text.Get("Toast.MovedTopLeft", "已移动到左上角"),
            WindowCorner.TopRight => text.Get("Toast.MovedTopRight", "已移动到右上角"),
            WindowCorner.BottomLeft => text.Get("Toast.MovedBottomLeft", "已移动到左下角"),
            _ => text.Get("Toast.MovedBottomRight", "已移动到右下角"),
        };
        SetStatus(message, StatusLevel.Success);
    }

    public void RestoreDefaultSettings()
    {
        WindowOpacity = 1.0;
        ZoomFactor = 1.0;
        RestoreDefaultHotkeys();
    }

    public void SetHotkeyRecordingActive(bool active)
    {
        keyboardHookService.SuspendBuiltInHotkeys = active;
    }

    public void SetAppActive(bool active)
    {
        keyboardHookService.IsAppActive = active;
    }

    public void CaptureWindowBounds()
    {
        if (shuttingDown)
        {
            return;
        }

        Notify(BrowserStateChangeKind.WindowBounds);
        if (!windowPlacementService.CanPersistCurrentBounds)
        {
            return;
        }

        WindowBounds bounds = windowPlacementService.Capture();
        if (!bounds.IsValid)
        {
            return;
        }

        settings.WindowLeft = bounds.Left;
        settings.WindowTop = bounds.Top;
        settings.WindowWidth = bounds.Width;
        settings.WindowHeight = BrowserWindowMetrics.ToContentHeight(bounds.Height, settings.WindowMode);
        QueueSettingsSave();
        Notify(BrowserStateChangeKind.WindowSize);
    }

    public WindowBounds GetControlWindowBounds()
    {
        return new WindowBounds(
            settings.ControlWindowLeft,
            settings.ControlWindowTop,
            settings.ControlWindowWidth,
            settings.ControlWindowHeight);
    }

    public bool HasControlWindowPosition => settings.HasControlWindowPosition;

    public WindowBounds GetBrowserWindowBounds() => windowPlacementService.Capture();

    public string GetText(string key, string fallback) => text.Get(key, fallback);

    public string FormatText(string key, string fallback, params object[] args) =>
        text.Format(key, fallback, args);

    public void CaptureControlWindowBounds(WindowBounds bounds)
    {
        if (!bounds.IsValid || shuttingDown)
        {
            return;
        }

        settings.ControlWindowLeft = bounds.Left;
        settings.ControlWindowTop = bounds.Top;
        settings.ControlWindowWidth = bounds.Width;
        settings.ControlWindowHeight = bounds.Height;
        settings.HasControlWindowPosition = true;
        QueueSettingsSave();
    }

    public async ValueTask DisposeAsync()
    {
        if (shuttingDown)
        {
            return;
        }

        shuttingDown = true;
        lifetimeCts.Cancel();
        keyboardHookService.KPressed -= KeyboardHookOnPlaybackPressed;
        keyboardHookService.ModeTogglePressed -= KeyboardHookOnModeTogglePressed;
        keyboardHookService.Dispose();

        settingsSaveCts?.Cancel();
        settingsSaveCts?.Dispose();
        settingsSaveCts = null;
        await settingsSaveTask.ConfigureAwait(true);
        historyCaptureCts?.Cancel();
        historyCaptureCts?.Dispose();
        historyCaptureCts = null;
        await historyCaptureTask.ConfigureAwait(true);
        cacheCheckCts?.Cancel();
        if (cacheCheckTask is not null)
        {
            try
            {
                await cacheCheckTask.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
        }
        cacheCheckCts?.Dispose();
        cacheCheckCts = null;
        cacheCheckTask = null;
        await zoomApplyTask.ConfigureAwait(true);
        CapturePageStateForShutdown();
        CapturePendingDownloadProgress();
        DetachDownloadOperations(markInterrupted: true);
        try
        {
            DetachWebViewEvents();
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Detach WinUI WebView2 events during shutdown");
        }
        CaptureWindowBoundsForShutdown();
        try
        {
            webView.Close();
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Close WinUI WebView2 during shutdown");
        }
        await RunShutdownStepAsync(
            () => settingsService.SaveAsync(settings),
            "Save settings before WinUI shutdown").ConfigureAwait(false);
        await RunShutdownStepAsync(
            historyService.FlushAsync,
            "Flush history before WinUI shutdown").ConfigureAwait(false);
        await RunShutdownStepAsync(
            favoritesService.FlushAsync,
            "Flush favorites before WinUI shutdown").ConfigureAwait(false);
        await RunShutdownStepAsync(
            downloadsService.FlushAsync,
            "Flush downloads before WinUI shutdown").ConfigureAwait(false);

        DisposeShutdownService(settingsService, "Dispose settings service");
        DisposeShutdownService(historyService, "Dispose history service");
        DisposeShutdownService(favoritesService, "Dispose favorites service");
        DisposeShutdownService(downloadsService, "Dispose downloads service");
        lifetimeCts.Dispose();
    }

    private async Task<bool> InitializeWebViewAsync(string startUrl)
    {
        if (shuttingDown)
        {
            return false;
        }

        try
        {
            if (!WebViewEnvironmentProvider.IsRuntimeInstalled())
            {
                SetStatus(
                    text.Get("Status.InstallingWebView2", "正在安装 WebView2 Runtime..."),
                    StatusLevel.Info);
            }

            CoreWebView2Environment environment = await environmentProvider.GetAsync().ConfigureAwait(true);
            if (shuttingDown)
            {
                return false;
            }
            await webViewHost.WaitForCurrentWebViewLoadedAsync(lifetimeCts.Token).ConfigureAwait(true);
            if (shuttingDown)
            {
                return false;
            }
            await webView.EnsureCoreWebView2Async(environment);
            if (shuttingDown)
            {
                return false;
            }

            core = webView.CoreWebView2 ?? throw new InvalidOperationException("WebView2 Core 初始化失败。");
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.AreBrowserAcceleratorKeysEnabled = BrowserModeRules.ShouldEnableAcceleratorKeys(settings.WindowMode);
            core.Settings.IsZoomControlEnabled = false;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(CursorBootstrapScript);
            if (shuttingDown)
            {
                return false;
            }
            zoomBootstrapScriptId = null;
            await InstallZoomScriptAsync(settings.ZoomFactor, applyToCurrentDocument: false);
            if (shuttingDown)
            {
                return false;
            }
            AttachWebViewEvents();

            currentAddress = startUrl;
            SetNavigating(true);
            core.Navigate(startUrl);
            SetStatus(
                text.Get("Status.BrowserReady", "浏览器已就绪"),
                StatusLevel.Success,
                BrowserStateChangeKind.Navigation | BrowserStateChangeKind.Mode);
            return true;
        }
        catch (Exception ex)
        {
            if (shuttingDown)
            {
                return false;
            }

            try
            {
                DetachWebViewEvents();
            }
            catch (Exception detachException)
            {
                FileLogger.LogException(detachException, "Detach partially initialized WinUI WebView2");
            }
            core = null;
            FileLogger.LogException(ex, "Initialize WinUI WebView2");
            SetNavigating(false);
            string message = text.Format("Status.InitFailed", "浏览器初始化失败：{0}", ex.Message);
            SetStatus(message, StatusLevel.Error);
            Win32MessageBox.ShowError(
                message,
                text.Get("Status.ErrorTitle", "Genshin Browser 错误"),
                ownerWindowHandle);
            return false;
        }
    }

    public void ResizeBrowserWindow(double width, double height)
    {
        windowPlacementService.Resize(
            Math.Clamp(width, 640, 10_000),
            BrowserWindowMetrics.ToOuterHeight(Math.Clamp(height, 360, 10_000), settings.WindowMode));
        CaptureWindowBounds();
    }

    private void SetZoom(double value)
    {
        double clamped = Math.Clamp(value, 0.25, 5.0);
        if (Math.Abs(settings.ZoomFactor - clamped) <= 0.0001)
        {
            return;
        }

        settings.ZoomFactor = clamped;
        QueueSettingsSave();
        Task previousApply = zoomApplyTask;
        zoomApplyTask = ApplyZoomQueuedAsync(previousApply, clamped);
        Notify(BrowserStateChangeKind.Zoom);
    }

    private async Task ApplyZoomQueuedAsync(Task previousApply, double factor)
    {
        try
        {
            await previousApply.ConfigureAwait(true);
            if (shuttingDown || Math.Abs(settings.ZoomFactor - factor) > 0.0001)
            {
                return;
            }

            await InstallZoomScriptAsync(factor, applyToCurrentDocument: true);
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Apply WinUI WebView2 page zoom");
            SetStatus(
                text.Format("Status.ZoomFailed", "设置页面缩放失败：{0}", ex.Message),
                StatusLevel.Error);
        }
    }

    private async Task InstallZoomScriptAsync(double factor, bool applyToCurrentDocument)
    {
        CoreWebView2 activeCore = core
            ?? throw new InvalidOperationException("WebView2 Core is unavailable while applying zoom.");
        if (zoomBootstrapScriptId is not null)
        {
            activeCore.RemoveScriptToExecuteOnDocumentCreated(zoomBootstrapScriptId);
            zoomBootstrapScriptId = null;
        }

        string script = CreateZoomScript(factor);
        string scriptId = await activeCore.AddScriptToExecuteOnDocumentCreatedAsync(script);
        if (shuttingDown || !ReferenceEquals(core, activeCore))
        {
            activeCore.RemoveScriptToExecuteOnDocumentCreated(scriptId);
            return;
        }

        zoomBootstrapScriptId = scriptId;
        if (applyToCurrentDocument)
        {
            await activeCore.ExecuteScriptAsync(script);
        }
    }

    private static string CreateZoomScript(double factor)
    {
        string value = factor.ToString("0.####", CultureInfo.InvariantCulture);
        return $$"""
            (() => {
              const apply = () => {
                if (document.documentElement) {
                  document.documentElement.style.zoom = '{{value}}';
                }
              };
              apply();
              if (!document.documentElement) {
                document.addEventListener('DOMContentLoaded', apply, { once: true });
              }
            })();
            """;
    }

    private void RestoreDefaultHotkeys()
    {
        HotkeyGesture parking = FindParkingHotkey();
        if (!TrySetTogglePlaybackHotkey(parking))
        {
            return;
        }
        if (!TrySetToggleModeHotkey(new HotkeyGesture(VirtualKeyCodes.F8, HotkeyModifiers.None)))
        {
            return;
        }
        TrySetTogglePlaybackHotkey(new HotkeyGesture(VirtualKeyCodes.K, HotkeyModifiers.None));
    }

    private HotkeyGesture FindParkingHotkey()
    {
        for (int virtualKey = 0x87; virtualKey >= 0x70; virtualKey--)
        {
            if (virtualKey == VirtualKeyCodes.F8)
            {
                continue;
            }

            HotkeyGesture candidate = new(virtualKey, HotkeyModifiers.None);
            if (candidate != settings.ToggleModeHotkey)
            {
                return candidate;
            }
        }

        return new HotkeyGesture(VirtualKeyCodes.F9, HotkeyModifiers.Control);
    }

    private void KeyboardHookOnPlaybackPressed(object? sender, EventArgs e)
    {
        if (shuttingDown)
        {
            return;
        }

        dispatcher.TryEnqueue(() =>
        {
            if (!shuttingDown)
            {
                _ = ToggleVideoPlaybackAsync();
            }
        });
    }

    private void KeyboardHookOnModeTogglePressed(object? sender, EventArgs e)
    {
        if (shuttingDown)
        {
            return;
        }

        dispatcher.TryEnqueue(() =>
        {
            if (!shuttingDown)
            {
                ToggleWindowMode();
            }
        });
    }

    private void QueueSettingsSave()
    {
        if (shuttingDown)
        {
            return;
        }

        settingsSaveCts?.Cancel();
        settingsSaveCts?.Dispose();
        settingsSaveCts = new CancellationTokenSource();
        Task previousSave = settingsSaveTask;
        settingsSaveTask = SaveSettingsDebouncedAsync(previousSave, settingsSaveCts.Token);
    }

    private async Task SaveSettingsDebouncedAsync(Task previousSave, CancellationToken cancellationToken)
    {
        try
        {
            await previousSave.ConfigureAwait(false);
            await Task.Delay(AppConfig.Ui.SettingsSaveDebounceMs, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await dispatcher.InvokeAsync(() =>
                shuttingDown ? Task.CompletedTask : settingsService.SaveAsync(settings)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Save settings");
        }
    }

    private void SetStatus(
        string message,
        StatusLevel level,
        BrowserStateChangeKind extra = BrowserStateChangeKind.None)
    {
        if (shuttingDown)
        {
            return;
        }

        statusMessage = message;
        lastStatusLevel = level;
        Notify(BrowserStateChangeKind.Status | extra);
    }

    private void SetNavigating(bool value)
    {
        if (isNavigating == value)
        {
            return;
        }

        isNavigating = value;
        Notify(BrowserStateChangeKind.Navigation);
    }

    private void Notify(BrowserStateChangeKind kind)
    {
        if (shuttingDown)
        {
            return;
        }

        BrowserStateChanged?.Invoke(this, new BrowserStateChangedEventArgs(kind));
    }

    private string GetDocumentTitle()
    {
        string title = ReadDocumentTitle();
        return string.IsNullOrWhiteSpace(title) ? currentAddress : title;
    }

    private string ReadDocumentTitle()
    {
        try
        {
            return core?.DocumentTitle ?? string.Empty;
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Read WinUI WebView2 document title");
            return string.Empty;
        }
    }

    private void CaptureWindowBoundsForShutdown()
    {
        if (!windowPlacementService.CanPersistCurrentBounds)
        {
            return;
        }

        WindowBounds bounds = windowPlacementService.Capture();
        if (!bounds.IsValid)
        {
            return;
        }

        settings.WindowLeft = bounds.Left;
        settings.WindowTop = bounds.Top;
        settings.WindowWidth = bounds.Width;
        settings.WindowHeight = BrowserWindowMetrics.ToContentHeight(bounds.Height, settings.WindowMode);
    }

    private void CapturePageStateForShutdown()
    {
        try
        {
            CaptureCurrentAddress();
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Capture current address before WinUI shutdown");
        }

    }

    private static async Task RunShutdownStepAsync(Func<Task> step, string context)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, context);
        }
    }

    private static void DisposeShutdownService(IDisposable service, string context)
    {
        try
        {
            service.Dispose();
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, context);
        }
    }

    private static string NormalizeTheme(string? value)
    {
        return value?.Trim().ToUpperInvariant() switch
        {
            "LIGHT" => UiPreferences.Themes.Light,
            "SYSTEM" => UiPreferences.Themes.System,
            _ => UiPreferences.Themes.Dark,
        };
    }

    private static string NormalizeLanguage(string? value)
    {
        return string.Equals(value, UiPreferences.Languages.English, StringComparison.OrdinalIgnoreCase)
            ? UiPreferences.Languages.English
            : UiPreferences.Languages.Chinese;
    }
}
