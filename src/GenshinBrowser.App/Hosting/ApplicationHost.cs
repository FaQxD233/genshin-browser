using GenshinBrowser.Browser;
using GenshinBrowser.Localization;
using GenshinBrowser.Models;
using GenshinBrowser.Services;
using GenshinBrowser.Threading;
using GenshinBrowser.ViewModels;
using GenshinBrowser.Windowing;
using GenshinBrowser.Windows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace GenshinBrowser.Hosting;

internal sealed class ApplicationHost
{
    private readonly Action exitApplication;
    private BrowserWindow? browserWindow;
    private ControlWindow? controlWindow;
    private BrowserSession? browserSession;
    private bool browserWindowActive;
    private bool controlWindowActive;
    private bool shuttingDown;

    public ApplicationHost(Action exitApplication)
    {
        this.exitApplication = exitApplication;
    }

    public void Activate()
    {
        if (shuttingDown)
        {
            return;
        }

        browserWindow?.ShowAndActivate();
        if (browserSession?.CurrentMode == WindowMode.Free)
        {
            controlWindow?.ShowAndActivate();
        }
    }

    public void Start()
    {
        if (browserWindow is not null)
        {
            Activate();
            return;
        }

        FileLogger.PurgeOldLogs();
        ApplicationDataPaths paths = new();
        DataFileMaintenance.PurgeStaleTempFiles(paths.Root);
        SettingsService settingsService = new(paths.Settings);
        var settings = settingsService.Load();
        HistoryService historyService = new(paths.History);
        FavoritesService favoritesService = new(paths.Favorites);
        DownloadsService downloadsService = new(paths.Downloads);
        KeyboardHookService keyboardHookService = new();
        DispatcherQueue dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("WinUI dispatcher queue is unavailable.");
        DispatcherQueueUiDispatcher dispatcher = new(dispatcherQueue);
        DispatcherQueueTimerFactory timerFactory = new(dispatcherQueue);
        WinUiTextResourceService text = new();
        LocalizationBinding.Provider = text;
        WebViewEnvironmentProvider environmentProvider = new(paths.WebViewProfile, text);

        browserWindow = new BrowserWindow();
        AppWindowModeService modeService = new(browserWindow.AppWindow, browserWindow.WindowHandle);
        AppWindowPlacementService placementService = new(browserWindow.AppWindow, browserWindow.WindowHandle);
        WindowTransparencyService transparencyService = new(browserWindow.WindowHandle);
        browserSession = new BrowserSession(
            browserWindow,
            browserWindow.WindowHandle,
            environmentProvider,
            settingsService,
            settings,
            historyService,
            favoritesService,
            downloadsService,
            keyboardHookService,
            modeService,
            placementService,
            transparencyService,
            dispatcher,
            timerFactory,
            text);
        browserWindow.AttachSession(browserSession);

        ControlWindowViewModel viewModel = new(browserSession, dispatcher, timerFactory, text);
        controlWindow = new ControlWindow(browserSession, viewModel);
        AppWindowPlacementService controlPlacement = new(controlWindow.AppWindow, controlWindow.WindowHandle);
        controlWindow.AttachPlacement(controlPlacement);
        if (!WindowOwnerService.TrySetOwner(
                controlWindow.WindowHandle,
                browserWindow.WindowHandle,
                out int ownerError))
        {
            FileLogger.LogDebug($"Unable to set ControlWindow owner. Win32 error: {ownerError}");
        }

        browserWindow.ActivationChanged += BrowserWindow_OnActivationChanged;
        browserWindow.CloseRequested += BrowserWindow_OnCloseRequested;
        controlWindow.ActivationChanged += ControlWindow_OnActivationChanged;
        controlWindow.Closed += ControlWindow_OnClosed;
        browserWindow.ShowAndActivate();
        controlWindow.RestoreOrPlaceNearBrowser();
        controlWindow.ApplyInitialModeVisibility();
        _ = InitializeBrowserAsync(browserSession);
    }

    private static async Task InitializeBrowserAsync(BrowserSession session)
    {
        try
        {
            await session.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Start WinUI browser session");
        }
    }

    private async void BrowserWindow_OnCloseRequested(object? sender, EventArgs e)
    {
        if (shuttingDown)
        {
            return;
        }

        shuttingDown = true;
        BrowserWindow? windowToClose = browserWindow;
        if (windowToClose is not null)
        {
            windowToClose.ActivationChanged -= BrowserWindow_OnActivationChanged;
            windowToClose.CloseRequested -= BrowserWindow_OnCloseRequested;
        }
        browserWindowActive = false;

        ControlWindow? controlToClose = controlWindow;
        controlWindow = null;
        if (controlToClose is not null)
        {
            controlToClose.ActivationChanged -= ControlWindow_OnActivationChanged;
            controlToClose.Closed -= ControlWindow_OnClosed;
            controlWindowActive = false;
            try
            {
                controlToClose.CaptureBoundsForShutdown();
                controlToClose.AllowClose = true;
                controlToClose.Close();
            }
            catch (Exception ex)
            {
                FileLogger.LogException(ex, "Close WinUI control window");
            }
        }
        UpdateApplicationActivation();

        if (windowToClose?.AppWindow.IsVisible == true)
        {
            windowToClose.AppWindow.Hide();
        }

        BrowserSession? sessionToDispose = browserSession;
        browserSession = null;
        if (sessionToDispose is not null)
        {
            try
            {
                await sessionToDispose.DisposeAsync();
            }
            catch (Exception ex)
            {
                FileLogger.LogException(ex, "Shut down WinUI browser session");
            }
        }

        LocalizationBinding.Provider = null;
        if (windowToClose is not null)
        {
            try
            {
                windowToClose.AllowClose = true;
                windowToClose.Close();
            }
            catch (Exception ex)
            {
                FileLogger.LogException(ex, "Close WinUI browser window");
            }
        }
        browserWindow = null;
        exitApplication();
    }

    private void BrowserWindow_OnActivationChanged(bool active)
    {
        browserWindowActive = active;
        UpdateApplicationActivation();
    }

    private void ControlWindow_OnActivationChanged(bool active)
    {
        controlWindowActive = active;
        UpdateApplicationActivation();
    }

    private void UpdateApplicationActivation()
    {
        browserSession?.SetAppActive(browserWindowActive || controlWindowActive);
    }

    private void ControlWindow_OnClosed(object sender, WindowEventArgs args)
    {
        if (args.Handled)
        {
            return;
        }

        if (controlWindow is not null)
        {
            controlWindow.ActivationChanged -= ControlWindow_OnActivationChanged;
            controlWindow.Closed -= ControlWindow_OnClosed;
            controlWindow = null;
        }
        controlWindowActive = false;
        UpdateApplicationActivation();
    }
}
