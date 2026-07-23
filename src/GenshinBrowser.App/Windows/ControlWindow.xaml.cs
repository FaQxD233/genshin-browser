using GenshinBrowser.Browser;
using GenshinBrowser.Constants;
using GenshinBrowser.Models;
using GenshinBrowser.ViewModels;
using GenshinBrowser.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using UiDispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace GenshinBrowser.Windows;

public sealed partial class ControlWindow : Window
{
    private readonly BrowserSession session;
    private readonly ControlWindowViewModel viewModel;
    private readonly UiDispatcherQueueTimer boundsChangedTimer;
    private IWindowPlacementService? placementService;
    private bool followBrowser;
    private bool applyingPlacement;
    private WindowBounds? lastProgrammaticBounds;

    internal event Action<bool>? ActivationChanged;

    internal ControlWindow(BrowserSession session, ControlWindowViewModel viewModel)
    {
        this.session = session;
        this.viewModel = viewModel;
        followBrowser = !session.HasControlWindowPosition;
        InitializeComponent();
        RootGrid.DataContext = viewModel;

        AppWindow.Title = "Genshin Browser 控制面板";
        AppWindow.Resize(WindowDpi.ScaleSize(560, 640, WindowHandle));
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

        boundsChangedTimer = DispatcherQueue.CreateTimer();
        boundsChangedTimer.Interval = TimeSpan.FromMilliseconds(180);
        boundsChangedTimer.Tick += BoundsChangedTimer_OnTick;

        viewModel.PropertyChanged += ViewModel_OnPropertyChanged;
        session.BrowserStateChanged += Session_OnBrowserStateChanged;
        session.ThemeChanged += Session_OnThemeChanged;
        session.LanguageChanged += Session_OnLanguageChanged;
        AppWindow.Changed += AppWindow_OnChanged;
        Activated += ControlWindow_OnActivated;
        Closed += ControlWindow_OnClosed;
        SyncAddressBar(force: true);
        ApplyTheme();
        ApplyLanguage();
    }

    public bool AllowClose { get; set; }

    internal nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    internal void AttachPlacement(IWindowPlacementService service)
    {
        placementService = service;
    }

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

    internal void RestoreOrPlaceNearBrowser()
    {
        if (placementService is null)
        {
            return;
        }

        WindowBounds saved = session.GetControlWindowBounds();
        if (session.HasControlWindowPosition && saved.IsValid)
        {
            ApplyProgrammaticPlacement(saved);
            return;
        }

        PlaceNearBrowser(saved.Width, saved.Height);
    }

    private void PlaceNearBrowser(double width, double height)
    {
        if (placementService is null)
        {
            return;
        }

        WindowBounds browserBounds = session.GetBrowserWindowBounds();
        ApplyProgrammaticPlacement(new WindowBounds(
            browserBounds.Left + browserBounds.Width + 12,
            browserBounds.Top,
            width,
            height));
        WindowBounds placed = placementService.Capture();
        if (placed.Left < browserBounds.Left + browserBounds.Width &&
            placed.Left + placed.Width > browserBounds.Left)
        {
            ApplyProgrammaticPlacement(new WindowBounds(
                browserBounds.Left - width - 12,
                browserBounds.Top,
                width,
                height));
        }
    }

    private void ApplyProgrammaticPlacement(WindowBounds bounds)
    {
        if (placementService is null)
        {
            return;
        }

        applyingPlacement = true;
        try
        {
            placementService.Restore(bounds);
            lastProgrammaticBounds = placementService.Capture();
        }
        finally
        {
            applyingPlacement = false;
        }
    }

    internal void ApplyInitialModeVisibility()
    {
        if (session.CurrentMode == WindowMode.Free)
        {
            ShowAndActivate();
        }
        else
        {
            AppWindow.Hide();
        }
    }

    internal void CaptureBoundsForShutdown() => CaptureBounds();

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ControlWindowViewModel.CurrentAddress))
        {
            SyncAddressBar();
        }
    }

    private void Session_OnBrowserStateChanged(object? sender, BrowserStateChangedEventArgs e)
    {
        if (e.Kind.HasFlag(BrowserStateChangeKind.Mode))
        {
            if (session.CurrentMode == WindowMode.Fixed)
            {
                HideControlWindow();
            }
            else
            {
                if (followBrowser)
                {
                    WindowBounds current = placementService?.Capture() ?? default;
                    PlaceNearBrowser(current.Width, current.Height);
                }
                ShowAndActivate();
            }
        }

        if (e.Kind.HasFlag(BrowserStateChangeKind.WindowBounds) &&
            followBrowser &&
            session.CurrentMode == WindowMode.Free)
        {
            WindowBounds current = placementService?.Capture() ?? default;
            PlaceNearBrowser(current.Width, current.Height);
        }
    }

    private void SyncAddressBar(bool force = false)
    {
        if (!force && AddressBox.FocusState != FocusState.Unfocused)
        {
            return;
        }

        AddressBox.Text = viewModel.CurrentAddress;
    }

    private void NavigateFromAddressBox()
    {
        if (viewModel.NavigateFromAddressBarCommand.CanExecute(AddressBox.Text))
        {
            viewModel.NavigateFromAddressBarCommand.Execute(AddressBox.Text);
        }
    }

    private void AddressBox_OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            NavigateFromAddressBox();
            e.Handled = true;
        }
    }

    private void AddressBox_OnLostFocus(object sender, RoutedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() => SyncAddressBar(force: true));
    }

    private void NavigateButton_Click(object sender, RoutedEventArgs e) => NavigateFromAddressBox();

    private void WindowSizeBox_OnLostFocus(object sender, RoutedEventArgs e)
    {
        viewModel.ApplyWindowSizeCommand.Execute(null);
    }

    private void WindowSizeBox_OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            viewModel.ApplyWindowSizeCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OpacityBox_OnLostFocus(object sender, RoutedEventArgs e)
    {
        viewModel.ApplyOpacityCommand.Execute(null);
    }

    private void OpacityBox_OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            viewModel.ApplyOpacityCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void ZoomBox_OnLostFocus(object sender, RoutedEventArgs e)
    {
        viewModel.ApplyZoomCommand.Execute(null);
    }

    private void ZoomBox_OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            viewModel.ApplyZoomCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void RootGrid_OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!viewModel.IsRecordingAnyKey)
        {
            if (e.Key == VirtualKey.L && GetCurrentModifiers() == HotkeyModifiers.Control)
            {
                AddressBox.Focus(FocusState.Keyboard);
                AddressBox.SelectAll();
                e.Handled = true;
            }
            return;
        }

        HotkeyModifiers modifiers = GetCurrentModifiers();
        int virtualKey = (int)e.OriginalKey;
        if (IsModifierKey(virtualKey))
        {
            viewModel.UpdateRecordingModifiers(modifiers);
        }
        else
        {
            viewModel.FinishRecordingKey(virtualKey, modifiers);
        }
        e.Handled = true;
    }

    private void RootGrid_OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (viewModel.IsRecordingAnyKey)
        {
            viewModel.UpdateRecordingModifiers(GetCurrentModifiers());
            e.Handled = true;
        }
    }

    private void FavoritesList_OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        OpenSelectedItem(FavoritesList);
    }

    private void HistoryList_OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        OpenSelectedItem(HistoryList);
    }

    private void FavoritesList_OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        HandleListKey(FavoritesList, e, removeFavorite: true);
    }

    private void HistoryList_OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        HandleListKey(HistoryList, e, removeFavorite: false);
    }

    private void HandleListKey(ListView list, KeyRoutedEventArgs e, bool removeFavorite)
    {
        if (e.Key == VirtualKey.Enter)
        {
            OpenSelectedItem(list);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Delete && list.SelectedItem is ControlItemViewModel item)
        {
            if (removeFavorite) viewModel.RemoveFavoriteCommand.Execute(item);
            else viewModel.RemoveHistoryCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void OpenSelectedItem(ListView list)
    {
        if (list.SelectedItem is ControlItemViewModel item)
        {
            viewModel.OpenItemCommand.Execute(item);
        }
    }

    private void OpenControlItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: ControlItemViewModel item })
        {
            viewModel.OpenItemCommand.Execute(item);
        }
    }

    private void CopyControlItemUrl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: ControlItemViewModel item })
        {
            return;
        }

        DataPackage package = new();
        package.SetText(item.Url);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    private void RemoveFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: ControlItemViewModel item })
        {
            viewModel.RemoveFavoriteCommand.Execute(item);
        }
    }

    private void RemoveHistory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: ControlItemViewModel item })
        {
            viewModel.RemoveHistoryCommand.Execute(item);
        }
    }

    private void OpenDownloadFile_Click(object sender, RoutedEventArgs e)
    {
        ExecuteDownloadCommand(sender, viewModel.OpenDownloadFileCommand);
    }

    private void OpenDownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        ExecuteDownloadCommand(sender, viewModel.OpenDownloadFolderCommand);
    }

    private void RetryDownload_Click(object sender, RoutedEventArgs e)
    {
        ExecuteDownloadCommand(sender, viewModel.RetryDownloadCommand);
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e)
    {
        ExecuteDownloadCommand(sender, viewModel.CancelDownloadCommand);
    }

    private static void ExecuteDownloadCommand(object sender, RelayCommand command)
    {
        if (sender is Button { Tag: DownloadItem item } && command.CanExecute(item))
        {
            command.Execute(item);
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Minimize();
        }
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
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

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        HideControlWindow();
    }

    private void HideControlWindow()
    {
        viewModel.CancelHotkeyRecording();
        CaptureBounds();
        ActivationChanged?.Invoke(false);
        AppWindow.Hide();
    }

    private void AppWindow_OnChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        UpdateMinimumSize();
        UpdateTitleBarInset();
        UpdateMaximizeVisual();
        bool programmaticChange = applyingPlacement || IsAtLastProgrammaticBounds();
        bool userBoundsChange = (args.DidPositionChange || args.DidSizeChange) && !programmaticChange;
        if (userBoundsChange)
        {
            followBrowser = false;
        }

        if (userBoundsChange)
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

        int minimumWidth = WindowDpi.ScaleLength(400, WindowHandle);
        int minimumHeight = WindowDpi.ScaleLength(440, WindowHandle);
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

    private bool IsAtLastProgrammaticBounds()
    {
        if (placementService is null || lastProgrammaticBounds is not { } expected)
        {
            return false;
        }

        WindowBounds current = placementService.Capture();
        const double tolerance = 1.0;
        return Math.Abs(current.Left - expected.Left) <= tolerance &&
               Math.Abs(current.Top - expected.Top) <= tolerance &&
               Math.Abs(current.Width - expected.Width) <= tolerance &&
               Math.Abs(current.Height - expected.Height) <= tolerance;
    }

    private void BoundsChangedTimer_OnTick(UiDispatcherQueueTimer sender, object args)
    {
        boundsChangedTimer.Stop();
        CaptureBounds();
    }

    private void CaptureBounds()
    {
        if (!followBrowser && placementService is { CanPersistCurrentBounds: true })
        {
            session.CaptureControlWindowBounds(placementService.Capture());
        }
    }

    private void ControlWindow_OnActivated(object sender, WindowActivatedEventArgs args)
    {
        bool active = args.WindowActivationState != WindowActivationState.Deactivated;
        ActivationChanged?.Invoke(active);
        if (!active)
        {
            viewModel.CancelHotkeyRecording();
        }
    }

    private void Session_OnThemeChanged(object? sender, EventArgs e)
    {
        ApplyTheme();
    }

    private void Session_OnLanguageChanged(object? sender, EventArgs e)
    {
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        AppWindow.Title = session.GetText("Control.Title", "Genshin Browser 控制面板");
    }

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = session.ThemeMode switch
        {
            UiPreferences.Themes.Dark => ElementTheme.Dark,
            UiPreferences.Themes.Light => ElementTheme.Light,
            _ => ElementTheme.Default,
        };
    }

    private void ControlWindow_OnClosed(object sender, WindowEventArgs args)
    {
        if (!AllowClose)
        {
            args.Handled = true;
            HideControlWindow();
            return;
        }

        ActivationChanged?.Invoke(false);
        boundsChangedTimer.Stop();
        boundsChangedTimer.Tick -= BoundsChangedTimer_OnTick;
        viewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
        session.BrowserStateChanged -= Session_OnBrowserStateChanged;
        session.ThemeChanged -= Session_OnThemeChanged;
        session.LanguageChanged -= Session_OnLanguageChanged;
        AppWindow.Changed -= AppWindow_OnChanged;
        Activated -= ControlWindow_OnActivated;
        Closed -= ControlWindow_OnClosed;
        viewModel.Dispose();
        ActivationChanged = null;
    }

    private static bool IsModifierKey(int virtualKey)
    {
        return virtualKey is
            0x10 or 0x11 or 0x12 or
            0x5B or 0x5C or
            0xA0 or 0xA1 or
            0xA2 or 0xA3 or
            0xA4 or 0xA5;
    }

    private static HotkeyModifiers GetCurrentModifiers()
    {
        HotkeyModifiers modifiers = HotkeyModifiers.None;
        if (IsPressed(0x11)) modifiers |= HotkeyModifiers.Control;
        if (IsPressed(0x12)) modifiers |= HotkeyModifiers.Alt;
        if (IsPressed(0x10)) modifiers |= HotkeyModifiers.Shift;
        if (IsPressed(0x5B) || IsPressed(0x5C)) modifiers |= HotkeyModifiers.Windows;
        return modifiers;
    }

    private static bool IsPressed(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
