using GenshinBrowser.Browser;
using GenshinBrowser.Constants;
using GenshinBrowser.Models;
using GenshinBrowser.Services;
using GenshinBrowser.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Runtime.InteropServices;
using Windows.UI;

namespace GenshinBrowser.Windows;

public sealed partial class BrowserWindow : Window, IWebViewHost
{
    private readonly DispatcherQueueTimer titleBarHideTimer;
    private readonly DispatcherQueueTimer boundsChangedTimer;
    private readonly DispatcherQueueTimer modeToastTimer;
    private WebView2 activeWebView;
    private TaskCompletionSource webViewLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private BrowserSession? session;
    private bool pointerOverTitleBar;

    internal event Action<bool>? ActivationChanged;

    internal event EventHandler? CloseRequested;

    internal bool AllowClose { get; set; }

    public BrowserWindow()
    {
        InitializeComponent();
        activeWebView = (WebView2)BrowserSurface.Children[0];
        AttachWebViewLoadedSignal(activeWebView);

        AppWindow.Title = "Genshin Browser";
        AppWindow.Resize(WindowDpi.ScaleSize(658, 370, WindowHandle));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
        }
        UpdateMinimumSize();

        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        UpdateTitleBarInset();

        titleBarHideTimer = DispatcherQueue.CreateTimer();
        titleBarHideTimer.Interval = TimeSpan.FromSeconds(1.5);
        titleBarHideTimer.Tick += TitleBarHideTimer_OnTick;

        boundsChangedTimer = DispatcherQueue.CreateTimer();
        boundsChangedTimer.Interval = TimeSpan.FromMilliseconds(150);
        boundsChangedTimer.Tick += BoundsChangedTimer_OnTick;

        modeToastTimer = DispatcherQueue.CreateTimer();
        modeToastTimer.Interval = TimeSpan.FromSeconds(1.8);
        modeToastTimer.Tick += ModeToastTimer_OnTick;

        BrowserRoot.ActualThemeChanged += BrowserRoot_OnActualThemeChanged;
        AppWindow.Changed += AppWindow_OnChanged;
        Activated += BrowserWindow_OnActivated;
        Closed += BrowserWindow_OnClosed;
    }

    WebView2 IWebViewHost.CurrentWebView => activeWebView;

    internal nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    internal void ShowAndActivate()
    {
        if (!AppWindow.IsVisible)
        {
            AppWindow.Show();
        }
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }
        Activate();
    }

    internal void AttachSession(BrowserSession browserSession)
    {
        session = browserSession;
        session.BrowserStateChanged += Session_OnBrowserStateChanged;
        session.ThemeChanged += Session_OnThemeChanged;
        session.LanguageChanged += Session_OnLanguageChanged;
        ApplyTheme();
        ApplyWebViewBackground();
        UpdateWebViewAutomationName();
        ApplySessionState(BrowserStateChangeKind.All & ~BrowserStateChangeKind.Mode);
        ApplyWindowModeVisual(session.CurrentMode, showToast: false);
    }

    WebView2 IWebViewHost.ReplaceWebView()
    {
        BrowserSurface.Children.Clear();
        webViewLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        activeWebView = new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            DefaultBackgroundColor = GetWebViewBackgroundColor(),
        };
        AutomationProperties.SetName(
            activeWebView,
            session?.GetText("Browser.ContentName", "浏览器内容") ?? "浏览器内容");
        AttachWebViewLoadedSignal(activeWebView);
        BrowserSurface.Children.Add(activeWebView);
        return activeWebView;
    }

    Task IWebViewHost.WaitForCurrentWebViewLoadedAsync(CancellationToken cancellationToken) =>
        webViewLoaded.Task.WaitAsync(cancellationToken);

    private void AttachWebViewLoadedSignal(WebView2 view)
    {
        TaskCompletionSource loadedSignal = webViewLoaded;
        RoutedEventHandler? handler = null;
        handler = (sender, args) =>
        {
            view.Loaded -= handler;
            loadedSignal.TrySetResult();
        };
        view.Loaded += handler;
    }

    private void Session_OnBrowserStateChanged(object? sender, BrowserStateChangedEventArgs e)
    {
        ApplySessionState(e.Kind);
    }

    private void Session_OnThemeChanged(object? sender, EventArgs e)
    {
        ApplyTheme();
        ApplyWebViewBackground();
    }

    private void Session_OnLanguageChanged(object? sender, EventArgs e)
    {
        UpdateDocumentTitle();
        UpdateWebViewAutomationName();
        UpdateModeTogglePresentation(session?.CurrentMode ?? WindowMode.Free);
    }

    private void UpdateWebViewAutomationName()
    {
        AutomationProperties.SetName(
            activeWebView,
            session?.GetText("Browser.ContentName", "浏览器内容") ?? "浏览器内容");
    }

    private void ApplyTheme()
    {
        if (session is null)
        {
            return;
        }

        BrowserRoot.RequestedTheme = session.ThemeMode switch
        {
            UiPreferences.Themes.Dark => ElementTheme.Dark,
            UiPreferences.Themes.Light => ElementTheme.Light,
            _ => ElementTheme.Default,
        };
    }

    private void ApplyWebViewBackground()
    {
        activeWebView.DefaultBackgroundColor = GetWebViewBackgroundColor();
    }

    private Color GetWebViewBackgroundColor()
    {
        // Match WPF dark canvas (#0F1419) so the first paint doesn't flash pure black.
        return BrowserRoot.ActualTheme == ElementTheme.Light
            ? Color.FromArgb(255, 255, 255, 255)
            : Color.FromArgb(255, 0x0F, 0x14, 0x19);
    }

    private void BrowserRoot_OnActualThemeChanged(FrameworkElement sender, object args)
    {
        ApplyWebViewBackground();
    }

    private void ApplySessionState(BrowserStateChangeKind kind)
    {
        if (session is null)
        {
            return;
        }

        if (kind.HasFlag(BrowserStateChangeKind.Navigation) || kind.HasFlag(BrowserStateChangeKind.Title))
        {
            UpdateDocumentTitle();
        }

        if (kind.HasFlag(BrowserStateChangeKind.Mode))
        {
            ApplyWindowModeVisual(session.CurrentMode, showToast: true);
        }

        if (kind.HasFlag(BrowserStateChangeKind.Hotkeys))
        {
            UpdateModeTogglePresentation(session.CurrentMode);
        }
    }

    private void UpdateDocumentTitle()
    {
        string applicationTitle = session?.GetText("App.Title", "Genshin Browser") ?? "Genshin Browser";
        string documentTitle = session?.DocumentTitle ?? string.Empty;
        DocumentTitleText.Text = string.IsNullOrWhiteSpace(documentTitle) ? applicationTitle : documentTitle;
        AppWindow.Title = string.IsNullOrWhiteSpace(documentTitle)
            ? applicationTitle
            : $"{documentTitle} - {applicationTitle}";
    }

    private void ApplyWindowModeVisual(WindowMode mode, bool showToast)
    {
        bool floating = mode == WindowMode.Fixed;
        UpdateModeTogglePresentation(mode);
        MaximizeButton.IsEnabled = !floating;
        if (floating)
        {
            HideTitleBar();
        }
        else
        {
            ShowTitleBar();
        }

        if (showToast)
        {
            string message = session?.ModeToastMessage ?? string.Empty;
            if (string.IsNullOrWhiteSpace(message))
            {
                message = floating
                    ? session?.GetText("Mode.ToastFloating", "浮窗") ?? "浮窗"
                    : session?.GetText("Mode.ToastBrowsing", "浏览") ?? "浏览";
            }
            ShowModeToast(message, session?.ModeToastDuration ?? TimeSpan.FromSeconds(1.1));
        }
    }

    private void UpdateModeTogglePresentation(WindowMode mode)
    {
        bool floating = mode == WindowMode.Fixed;
        ModeToggleIcon.Glyph = floating ? "\uE785" : "\uE718";
        if (session is null)
        {
            return;
        }

        string hotkey = HotkeyFormatter.Format(session.ToggleModeHotkey);
        string tooltip = floating
            ? session.FormatText(
                "Mode.SwitchToBrowsingTooltip",
                "返回浏览（{0}）",
                hotkey)
            : session.FormatText(
                "Mode.SwitchToFloatingTooltip",
                "切换到浮窗（{0}）",
                hotkey);
        ToolTipService.SetToolTip(ModeToggleButton, tooltip);
        AutomationProperties.SetName(ModeToggleButton, tooltip);
    }

    private void ShowTitleBar()
    {
        titleBarHideTimer.Stop();
        TitleBarRevealStrip.IsHitTestVisible = false;
        SetTitleBar(TitleBarDragRegion);
        TitleBarRow.Height = new GridLength(32);
        TitleBarArea.Visibility = Visibility.Visible;
        if (session?.CurrentMode == WindowMode.Fixed)
        {
            BrowserSurface.SetValue(Grid.RowProperty, 0);
            BrowserSurface.SetValue(Grid.RowSpanProperty, 2);
        }
        else
        {
            BrowserSurface.SetValue(Grid.RowProperty, 1);
            BrowserSurface.SetValue(Grid.RowSpanProperty, 1);
        }
    }

    private void HideTitleBar()
    {
        if (session?.CurrentMode != WindowMode.Fixed)
        {
            return;
        }

        titleBarHideTimer.Stop();
        TitleBarRevealStrip.IsHitTestVisible = true;
        // Keep a small, hit-testable strip as the native drag region while the
        // floating title bar is hidden. This preserves dragging without making
        // the WebView surface draggable.
        SetTitleBar(TitleBarRevealStrip);
        TitleBarArea.Visibility = Visibility.Collapsed;
        TitleBarRow.Height = new GridLength(0);
        BrowserSurface.SetValue(Grid.RowProperty, 0);
        BrowserSurface.SetValue(Grid.RowSpanProperty, 2);
    }

    private void ScheduleTitleBarHide()
    {
        if (session?.CurrentMode != WindowMode.Fixed)
        {
            return;
        }

        titleBarHideTimer.Stop();
        titleBarHideTimer.Start();
    }

    private void TitleBarRevealStrip_OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (session?.CurrentMode == WindowMode.Fixed)
        {
            titleBarHideTimer.Stop();
            ShowTitleBar();
        }
    }

    private void TitleBarDragRegion_OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        pointerOverTitleBar = true;
        titleBarHideTimer.Stop();
    }

    private void TitleBarDragRegion_OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        pointerOverTitleBar = false;
        ScheduleTitleBarHide();
    }

    private void TitleBarHideTimer_OnTick(DispatcherQueueTimer sender, object args)
    {
        titleBarHideTimer.Stop();
        if (pointerOverTitleBar)
        {
            return;
        }

        HideTitleBar();
    }

    private void ShowModeToast(string message, TimeSpan duration)
    {
        ModeToastText.Text = message;
        ModeToast.Visibility = Visibility.Visible;
        modeToastTimer.Stop();
        modeToastTimer.Interval = duration;
        modeToastTimer.Start();
    }

    private void ModeToastTimer_OnTick(DispatcherQueueTimer sender, object args)
    {
        modeToastTimer.Stop();
        ModeToast.Visibility = Visibility.Collapsed;
    }

    private void AppWindow_OnChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        UpdateMinimumSize();
        UpdateTitleBarInset();
        UpdateMaximizeVisual();
        if (args.DidPositionChange || args.DidSizeChange)
        {
            boundsChangedTimer.Stop();
            boundsChangedTimer.Start();
        }
    }

    private void UpdateMaximizeVisual()
    {
        bool maximized = AppWindow.Presenter is OverlappedPresenter
        {
            State: OverlappedPresenterState.Maximized,
        };
        MaximizeIcon.Glyph = maximized ? "\uE923" : "\uE922";
    }

    private void UpdateMinimumSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        int minimumWidth = WindowDpi.ScaleLength(640, WindowHandle);
        int minimumHeight = WindowDpi.ScaleLength(
            session?.CurrentMode == WindowMode.Fixed ? 360 : 400,
            WindowHandle);
        if (presenter.PreferredMinimumWidth != minimumWidth)
        {
            presenter.PreferredMinimumWidth = minimumWidth;
        }
        if (presenter.PreferredMinimumHeight != minimumHeight)
        {
            presenter.PreferredMinimumHeight = minimumHeight;
        }
    }

    private void UpdateTitleBarInset()
    {
        CaptionButtonInsetColumn.Width = new GridLength(AppWindow.TitleBar.RightInset);
    }

    private void BoundsChangedTimer_OnTick(DispatcherQueueTimer sender, object args)
    {
        boundsChangedTimer.Stop();
        session?.CaptureWindowBounds();
    }

    private void BrowserWindow_OnActivated(object sender, WindowActivatedEventArgs args)
    {
        ActivationChanged?.Invoke(args.WindowActivationState != WindowActivationState.Deactivated);
    }

    private void BrowserRoot_OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (session is null || !IsControlPressed())
        {
            return;
        }

        int virtualKey = (int)e.OriginalKey;
        switch (virtualKey)
        {
            case 0xBB: // VK_OEM_PLUS
            case 0x6B: // VK_ADD
                session.ZoomFactor = Math.Clamp(session.ZoomFactor + 0.1, 0.25, 5.0);
                e.Handled = true;
                break;
            case 0xBD: // VK_OEM_MINUS
            case 0x6D: // VK_SUBTRACT
                session.ZoomFactor = Math.Clamp(session.ZoomFactor - 0.1, 0.25, 5.0);
                e.Handled = true;
                break;
            case 0x30: // VK_0
            case 0x60: // VK_NUMPAD0
                session.ZoomFactor = 1.0;
                e.Handled = true;
                break;
        }
    }

    private void ModeToggleButton_Click(object sender, RoutedEventArgs e) => session?.ToggleWindowMode();

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Minimize();
        }
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (session?.CurrentMode == WindowMode.Fixed || AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        if (presenter.State == OverlappedPresenterState.Maximized)
        {
            presenter.Restore();
        }
        else
        {
            presenter.Maximize();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void BrowserWindow_OnClosed(object sender, WindowEventArgs args)
    {
        if (!AllowClose)
        {
            args.Handled = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        ActivationChanged?.Invoke(false);
        titleBarHideTimer.Stop();
        boundsChangedTimer.Stop();
        modeToastTimer.Stop();
        titleBarHideTimer.Tick -= TitleBarHideTimer_OnTick;
        boundsChangedTimer.Tick -= BoundsChangedTimer_OnTick;
        modeToastTimer.Tick -= ModeToastTimer_OnTick;
        BrowserRoot.ActualThemeChanged -= BrowserRoot_OnActualThemeChanged;
        AppWindow.Changed -= AppWindow_OnChanged;
        Activated -= BrowserWindow_OnActivated;
        Closed -= BrowserWindow_OnClosed;
        if (session is not null)
        {
            session.BrowserStateChanged -= Session_OnBrowserStateChanged;
            session.ThemeChanged -= Session_OnThemeChanged;
            session.LanguageChanged -= Session_OnLanguageChanged;
            session = null;
        }
        ActivationChanged = null;
        CloseRequested = null;
    }

    private static bool IsControlPressed() => (GetAsyncKeyState(0x11) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
