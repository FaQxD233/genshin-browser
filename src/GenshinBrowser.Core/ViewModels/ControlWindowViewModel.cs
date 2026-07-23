using GenshinBrowser.Browser;
using GenshinBrowser.Constants;
using GenshinBrowser.Localization;
using GenshinBrowser.Models;
using GenshinBrowser.Services;
using GenshinBrowser.Threading;
using System.Collections.ObjectModel;
using System.Globalization;

namespace GenshinBrowser.ViewModels;

public sealed class ControlWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IBrowserSession browser;
    private readonly IUiDispatcher dispatcher;
    private readonly ITextResourceService text;
    private readonly IUiTimer toastTimer;
    private readonly List<ControlItemViewModel> allHistoryItems = [];
    private readonly List<ControlItemViewModel> allFavoriteItems = [];
    private string statusMessage = "正在初始化浏览器...";
    private string currentAddress = string.Empty;
    private string favoriteToggleText = "收藏当前页";
    private string favoriteToggleIcon = "\uE735";
    private bool isCurrentFavorite;
    private string searchText = string.Empty;
    private bool isFavoritesTab = true;
    private string toastMessage = string.Empty;
    private StatusLevel toastSeverity = StatusLevel.Info;
    private bool isToastVisible;
    private bool canGoBack;
    private bool canGoForward;
    private bool isNavigating;
    private bool isSettingsExpanded;
    private bool isDownloadsExpanded;
    private string downloadsBadgeText = string.Empty;
    private string browserWindowWidthText = string.Empty;
    private string browserWindowHeightText = string.Empty;
    private string opacityPercentText = string.Empty;
    private string zoomPercentText = string.Empty;
    private bool isRecordingToggleModeKey;
    private bool isRecordingTogglePlaybackKey;
    private HotkeyModifiers currentRecordingModifiers;
    private bool disposed;

    public ControlWindowViewModel(
        IBrowserSession browser,
        IUiDispatcher dispatcher,
        IUiTimerFactory timerFactory,
        ITextResourceService text)
    {
        this.browser = browser;
        this.dispatcher = dispatcher;
        this.text = text;

        SetBrowsingModeCommand = new RelayCommand(() => SetWindowMode(WindowMode.Free));
        SetFloatingModeCommand = new RelayCommand(() => SetWindowMode(WindowMode.Fixed));
        TogglePlaybackCommand = new AsyncRelayCommand(browser.ToggleVideoPlaybackAsync);
        ReloadCommand = new RelayCommand(browser.ReloadPage);
        NavigateFromAddressBarCommand = new RelayCommand(NavigateFromAddressBar);
        ToggleFavoriteCommand = new AsyncRelayCommand(ToggleFavoriteAsync);
        OpenItemCommand = new RelayCommand(NavigateToItem);
        RemoveFavoriteCommand = new AsyncRelayCommand(RemoveFavoriteAsync);
        RemoveHistoryCommand = new AsyncRelayCommand(RemoveHistoryAsync);
        GoBackCommand = new RelayCommand(browser.GoBack, () => browser.CanGoBack);
        GoForwardCommand = new RelayCommand(browser.GoForward, () => browser.CanGoForward);
        RecordToggleModeKeyCommand = new RelayCommand(StartRecordingToggleModeKey);
        RecordTogglePlaybackKeyCommand = new RelayCommand(StartRecordingTogglePlaybackKey);
        ToggleSettingsCommand = new RelayCommand(() => IsSettingsExpanded = !IsSettingsExpanded);
        ToggleDownloadsCommand = new RelayCommand(() => IsDownloadsExpanded = !IsDownloadsExpanded);
        ZoomInCommand = new RelayCommand(() => browser.ZoomFactor = Math.Clamp(browser.ZoomFactor + 0.1, 0.25, 5.0));
        ZoomOutCommand = new RelayCommand(() => browser.ZoomFactor = Math.Clamp(browser.ZoomFactor - 0.1, 0.25, 5.0));
        ResetZoomCommand = new RelayCommand(() => browser.ZoomFactor = 1.0);
        OpacityInCommand = new RelayCommand(() => WindowOpacity = Math.Clamp(WindowOpacity + 0.05, 0.1, 1.0));
        OpacityOutCommand = new RelayCommand(() => WindowOpacity = Math.Clamp(WindowOpacity - 0.05, 0.1, 1.0));
        ResetOpacityCommand = new RelayCommand(() => WindowOpacity = 1.0);
        RestoreDefaultSettingsCommand = new RelayCommand(RestoreDefaults);
        ClearBrowsingDataCommand = new AsyncRelayCommand(ClearBrowsingDataAsync);
        CancelDownloadCommand = new RelayCommand(parameter => ExecuteForDownload(parameter, browser.CancelDownload));
        RetryDownloadCommand = new RelayCommand(parameter => ExecuteForDownload(parameter, browser.RetryDownload));
        OpenDownloadFileCommand = new RelayCommand(parameter => ExecuteForDownload(parameter, browser.OpenDownloadFile));
        OpenDownloadFolderCommand = new RelayCommand(parameter => ExecuteForDownload(parameter, browser.OpenDownloadFolder));
        ClearFinishedDownloadsCommand = new RelayCommand(browser.ClearFinishedDownloads);
        SetThemeDarkCommand = new RelayCommand(() => SetTheme(UiPreferences.Themes.Dark));
        SetThemeLightCommand = new RelayCommand(() => SetTheme(UiPreferences.Themes.Light));
        SetThemeSystemCommand = new RelayCommand(() => SetTheme(UiPreferences.Themes.System));
        SetLanguageZhCommand = new RelayCommand(() => SetLanguage(UiPreferences.Languages.Chinese));
        SetLanguageEnCommand = new RelayCommand(() => SetLanguage(UiPreferences.Languages.English));
        MoveBrowserToCornerCommand = new RelayCommand(MoveBrowserToCorner);
        ApplyWindowSizeCommand = new RelayCommand(ApplyWindowSizeFromInput);
        ApplyOpacityCommand = new RelayCommand(ApplyOpacityFromInput);
        ApplyZoomCommand = new RelayCommand(ApplyZoomFromInput);

        toastTimer = timerFactory.Create(TimeSpan.FromSeconds(2.4));
        toastTimer.Tick += ToastTimerOnTick;
        browser.BrowserStateChanged += BrowserOnBrowserStateChanged;
        browser.DownloadsChanged += BrowserOnDownloadsChanged;
        text.LanguageChanged += TextOnLanguageChanged;

        SyncWindowSizeTexts();
        SyncOpacityZoomTexts();
        SyncDownloads(false);
        RefreshFromBrowser(BrowserStateChangeKind.All);
    }

    public ObservableCollection<ControlItemViewModel> HistoryItems { get; } = [];

    public ObservableCollection<ControlItemViewModel> FavoriteItems { get; } = [];

    public ObservableCollection<DownloadItem> Downloads { get; } = [];

    public RelayCommand GoBackCommand { get; }
    public RelayCommand GoForwardCommand { get; }
    public RelayCommand SetBrowsingModeCommand { get; }
    public RelayCommand SetFloatingModeCommand { get; }
    public AsyncRelayCommand TogglePlaybackCommand { get; }
    public RelayCommand ReloadCommand { get; }
    public RelayCommand NavigateFromAddressBarCommand { get; }
    public AsyncRelayCommand ToggleFavoriteCommand { get; }
    public RelayCommand OpenItemCommand { get; }
    public RelayCommand ToggleSettingsCommand { get; }
    public RelayCommand ToggleDownloadsCommand { get; }
    public AsyncRelayCommand RemoveFavoriteCommand { get; }
    public AsyncRelayCommand RemoveHistoryCommand { get; }
    public RelayCommand RecordToggleModeKeyCommand { get; }
    public RelayCommand RecordTogglePlaybackKeyCommand { get; }
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }
    public RelayCommand ResetZoomCommand { get; }
    public RelayCommand OpacityInCommand { get; }
    public RelayCommand OpacityOutCommand { get; }
    public RelayCommand ResetOpacityCommand { get; }
    public RelayCommand RestoreDefaultSettingsCommand { get; }
    public AsyncRelayCommand ClearBrowsingDataCommand { get; }
    public RelayCommand CancelDownloadCommand { get; }
    public RelayCommand RetryDownloadCommand { get; }
    public RelayCommand OpenDownloadFileCommand { get; }
    public RelayCommand OpenDownloadFolderCommand { get; }
    public RelayCommand ClearFinishedDownloadsCommand { get; }
    public RelayCommand SetThemeDarkCommand { get; }
    public RelayCommand SetThemeLightCommand { get; }
    public RelayCommand SetThemeSystemCommand { get; }
    public RelayCommand SetLanguageZhCommand { get; }
    public RelayCommand SetLanguageEnCommand { get; }
    public RelayCommand MoveBrowserToCornerCommand { get; }
    public RelayCommand ApplyWindowSizeCommand { get; }
    public RelayCommand ApplyOpacityCommand { get; }
    public RelayCommand ApplyZoomCommand { get; }

    public bool IsBrowsingMode => browser.CurrentMode == WindowMode.Free;

    public bool IsFloatingMode => browser.CurrentMode == WindowMode.Fixed;

    public string SwitchToBrowsingTooltip =>
        text.Format("Mode.SwitchToBrowsingTooltip", "切换到浏览（{0}）", FormatToggleModeHotkey());

    public string SwitchToFloatingTooltip =>
        text.Format("Mode.SwitchToFloatingTooltip", "切换到浮窗（{0}）", FormatToggleModeHotkey());

    public string PlaybackTooltip =>
        text.Format("Toolbar.PlaybackTooltip", "播放/暂停（{0}）", FormatTogglePlaybackHotkey());

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public string CurrentAddress
    {
        get => currentAddress;
        private set => SetProperty(ref currentAddress, value);
    }

    public string FavoriteToggleText
    {
        get => favoriteToggleText;
        private set => SetProperty(ref favoriteToggleText, value);
    }

    public string FavoriteToggleIcon
    {
        get => favoriteToggleIcon;
        private set => SetProperty(ref favoriteToggleIcon, value);
    }

    public bool IsCurrentFavorite
    {
        get => isCurrentFavorite;
        private set => SetProperty(ref isCurrentFavorite, value);
    }

    public bool CanGoBack
    {
        get => canGoBack;
        private set => SetProperty(ref canGoBack, value);
    }

    public bool CanGoForward
    {
        get => canGoForward;
        private set => SetProperty(ref canGoForward, value);
    }

    public bool IsNavigating
    {
        get => isNavigating;
        private set => SetProperty(ref isNavigating, value);
    }

    public string SearchText
    {
        get => searchText;
        set
        {
            string normalized = NormalizeSearch(value);
            if (SetProperty(ref searchText, normalized))
            {
                FilterCurrentTab();
                OnPropertyChanged(nameof(FavoritesEmptyText));
                OnPropertyChanged(nameof(HistoryEmptyText));
            }
        }
    }

    public bool IsFavoritesTab
    {
        get => isFavoritesTab;
        set
        {
            if (SetProperty(ref isFavoritesTab, value))
            {
                OnPropertyChanged(nameof(IsHistoryTab));
                FilterCurrentTab();
                OnPropertyChanged(nameof(ShowFavoritesEmpty));
                OnPropertyChanged(nameof(ShowHistoryEmpty));
            }
        }
    }

    public bool IsHistoryTab
    {
        get => !isFavoritesTab;
        set => IsFavoritesTab = !value;
    }

    public string FavoritesEmptyText => string.IsNullOrEmpty(SearchText)
        ? text.Get("Empty.Favorites", "暂无收藏\n打开视频后点 ☆ 即可添加")
        : text.Get("Empty.NoMatch", "未找到匹配项\n试试其它关键词");

    public string HistoryEmptyText => string.IsNullOrEmpty(SearchText)
        ? text.Get("Empty.History", "暂无浏览记录\n访问过的页面会出现在这里")
        : text.Get("Empty.NoMatch", "未找到匹配项\n试试其它关键词");

    public bool ShowFavoritesEmpty => IsFavoritesTab && FavoriteItems.Count == 0;

    public bool ShowHistoryEmpty => IsHistoryTab && HistoryItems.Count == 0;

    public string ToastMessage
    {
        get => toastMessage;
        private set => SetProperty(ref toastMessage, value);
    }

    public StatusLevel ToastSeverity
    {
        get => toastSeverity;
        private set => SetProperty(ref toastSeverity, value);
    }

    public bool IsToastVisible
    {
        get => isToastVisible;
        private set => SetProperty(ref isToastVisible, value);
    }

    public bool IsSettingsExpanded
    {
        get => isSettingsExpanded;
        set
        {
            if (!SetProperty(ref isSettingsExpanded, value))
            {
                return;
            }

            if (value && isDownloadsExpanded)
            {
                isDownloadsExpanded = false;
                OnPropertyChanged(nameof(IsDownloadsExpanded));
            }
        }
    }

    public bool IsDownloadsExpanded
    {
        get => isDownloadsExpanded;
        set
        {
            if (!SetProperty(ref isDownloadsExpanded, value))
            {
                return;
            }

            if (value && isSettingsExpanded)
            {
                isSettingsExpanded = false;
                OnPropertyChanged(nameof(IsSettingsExpanded));
            }
        }
    }

    public string DownloadsBadgeText
    {
        get => downloadsBadgeText;
        private set => SetProperty(ref downloadsBadgeText, value);
    }

    public bool HasDownloadsBadge => !string.IsNullOrEmpty(DownloadsBadgeText);

    public bool HasNoDownloads => Downloads.Count == 0;

    public string DownloadsEmptyText => text.Get("Downloads.Empty", "暂无下载任务\n文件下载会出现在这里");

    public string BrowserWindowWidthText
    {
        get => browserWindowWidthText;
        set => SetProperty(ref browserWindowWidthText, value ?? string.Empty);
    }

    public string BrowserWindowHeightText
    {
        get => browserWindowHeightText;
        set => SetProperty(ref browserWindowHeightText, value ?? string.Empty);
    }

    public string OpacityPercentText
    {
        get => opacityPercentText;
        set => SetProperty(ref opacityPercentText, value ?? string.Empty);
    }

    public string ZoomPercentText
    {
        get => zoomPercentText;
        set => SetProperty(ref zoomPercentText, value ?? string.Empty);
    }

    public double WindowOpacity
    {
        get => browser.WindowOpacity;
        set
        {
            double clamped = double.IsFinite(value) ? Math.Clamp(value, 0.1, 1.0) : 1.0;
            if (browser.WindowOpacity == clamped)
            {
                return;
            }

            browser.WindowOpacity = clamped;
        }
    }

    public bool IsRecordingAnyKey => isRecordingToggleModeKey || isRecordingTogglePlaybackKey;

    public string ToggleModeKeyText => isRecordingToggleModeKey
        ? HotkeyFormatter.GetModifiersString(currentRecordingModifiers) + "..."
        : FormatToggleModeHotkey();

    public string TogglePlaybackKeyText => isRecordingTogglePlaybackKey
        ? HotkeyFormatter.GetModifiersString(currentRecordingModifiers) + "..."
        : FormatTogglePlaybackHotkey();

    public bool IsThemeDark => string.Equals(browser.ThemeMode, UiPreferences.Themes.Dark, StringComparison.OrdinalIgnoreCase);

    public bool IsThemeLight => string.Equals(browser.ThemeMode, UiPreferences.Themes.Light, StringComparison.OrdinalIgnoreCase);

    public bool IsThemeSystem => string.Equals(browser.ThemeMode, UiPreferences.Themes.System, StringComparison.OrdinalIgnoreCase);

    public bool IsLanguageZh => string.Equals(browser.UiLanguage, UiPreferences.Languages.Chinese, StringComparison.OrdinalIgnoreCase);

    public bool IsLanguageEn => string.Equals(browser.UiLanguage, UiPreferences.Languages.English, StringComparison.OrdinalIgnoreCase);

    public void RefreshFromBrowser() => RefreshFromBrowser(BrowserStateChangeKind.All);

    public void RefreshFromBrowser(BrowserStateChangeKind kind)
    {
        if (kind == BrowserStateChangeKind.None || disposed)
        {
            return;
        }

        if (!dispatcher.HasThreadAccess)
        {
            dispatcher.TryEnqueue(() => RefreshFromBrowser(kind));
            return;
        }

        string previousStatus = StatusMessage;

        if (kind.HasFlag(BrowserStateChangeKind.Mode))
        {
            OnPropertyChanged(nameof(IsBrowsingMode));
            OnPropertyChanged(nameof(IsFloatingMode));
            NotifyHotkeyDependentTexts();
        }

        if (kind.HasFlag(BrowserStateChangeKind.Hotkeys))
        {
            NotifyHotkeyDependentTexts();
        }

        if (kind.HasFlag(BrowserStateChangeKind.Status))
        {
            StatusMessage = browser.StatusMessage;
        }

        if (kind.HasFlag(BrowserStateChangeKind.Navigation))
        {
            CurrentAddress = browser.CurrentAddress;
            CanGoBack = browser.CanGoBack;
            CanGoForward = browser.CanGoForward;
            IsNavigating = browser.IsNavigating;
            RefreshFavoriteState();
            GoBackCommand.RaiseCanExecuteChanged();
            GoForwardCommand.RaiseCanExecuteChanged();
        }

        if (kind.HasFlag(BrowserStateChangeKind.Favorites))
        {
            SyncSourceItems(allFavoriteItems, browser.FavoriteEntries);
            FilterFavorites();
            RefreshFavoriteState();
        }

        if (kind.HasFlag(BrowserStateChangeKind.History))
        {
            SyncSourceItems(allHistoryItems, browser.HistoryEntries);
            FilterHistory();
        }

        if (kind.HasFlag(BrowserStateChangeKind.Opacity))
        {
            SyncOpacityText();
            OnPropertyChanged(nameof(WindowOpacity));
        }

        if (kind.HasFlag(BrowserStateChangeKind.Zoom))
        {
            SyncZoomText();
        }

        if (kind.HasFlag(BrowserStateChangeKind.WindowSize))
        {
            SyncWindowSizeTexts();
        }

        if (kind.HasFlag(BrowserStateChangeKind.Status) &&
            !string.Equals(previousStatus, StatusMessage, StringComparison.Ordinal) &&
            ShouldToast(browser.LastStatusLevel))
        {
            ShowToast(StatusMessage, browser.LastStatusLevel);
        }
    }

    public void SyncWindowSizeTexts()
    {
        string width = Math.Round(browser.BrowserWindowWidth).ToString(CultureInfo.CurrentCulture);
        string height = Math.Round(browser.BrowserWindowHeight).ToString(CultureInfo.CurrentCulture);
        if (browserWindowWidthText == width && browserWindowHeightText == height)
        {
            return;
        }

        browserWindowWidthText = width;
        browserWindowHeightText = height;
        OnPropertyChanged(nameof(BrowserWindowWidthText));
        OnPropertyChanged(nameof(BrowserWindowHeightText));
    }

    public void UpdateRecordingModifiers(HotkeyModifiers modifiers)
    {
        currentRecordingModifiers = modifiers;
        NotifyHotkeyDependentTexts();
    }

    public void FinishRecordingKey(int virtualKey, HotkeyModifiers modifiers)
    {
        if (!IsRecordingAnyKey)
        {
            return;
        }

        if (virtualKey == 0x1B)
        {
            StopHotkeyRecording();
            ShowToast(text.Get("Toast.RecordCanceled", "已取消录制。"), StatusLevel.Warning);
            return;
        }

        HotkeyGesture gesture = new(virtualKey, modifiers);
        if (!gesture.IsValid)
        {
            return;
        }

        bool succeeded;
        string successMessage;
        if (isRecordingToggleModeKey)
        {
            succeeded = browser.TrySetToggleModeHotkey(gesture);
            successMessage = text.Format(
                "Toast.ToggleModeSet",
                "浏览/浮窗快捷键已设为 {0}",
                HotkeyFormatter.Format(browser.ToggleModeHotkey));
        }
        else
        {
            succeeded = browser.TrySetTogglePlaybackHotkey(gesture);
            successMessage = text.Format(
                "Toast.PlaybackSet",
                "播放快捷键已设为 {0}",
                HotkeyFormatter.Format(browser.TogglePlaybackHotkey));
        }

        if (succeeded)
        {
            ShowToast(successMessage, StatusLevel.Success);
            StopHotkeyRecording();
        }
        else
        {
            string conflictMessage = isRecordingToggleModeKey && gesture == browser.TogglePlaybackHotkey
                ? text.Get("Toast.HotkeyUsedByPlayback", "该快捷键已被视频播放占用！")
                : isRecordingTogglePlaybackKey && gesture == browser.ToggleModeHotkey
                    ? text.Get("Toast.HotkeyUsedByMode", "该快捷键已被「浏览 ⇄ 浮窗」占用！")
                    : text.Get("Toast.HotkeyConflict", "该快捷键已被另一个功能占用。");
            ShowToast(conflictMessage, StatusLevel.Warning);
        }

        NotifyHotkeyDependentTexts();
    }

    public void CancelHotkeyRecording()
    {
        if (IsRecordingAnyKey)
        {
            StopHotkeyRecording();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        CancelHotkeyRecording();
        browser.BrowserStateChanged -= BrowserOnBrowserStateChanged;
        browser.DownloadsChanged -= BrowserOnDownloadsChanged;
        text.LanguageChanged -= TextOnLanguageChanged;
        toastTimer.Stop();
        toastTimer.Tick -= ToastTimerOnTick;
        toastTimer.Dispose();
    }

    private async Task ToggleFavoriteAsync()
    {
        if (IsCurrentFavorite)
        {
            await browser.RemoveFavoriteAsync(browser.CurrentAddress).ConfigureAwait(true);
        }
        else
        {
            await browser.AddCurrentPageToFavoritesAsync().ConfigureAwait(true);
        }
    }

    private void NavigateToItem(object? parameter)
    {
        if (parameter is ControlItemViewModel item)
        {
            browser.NavigateTo(item.Url);
        }
    }

    private void NavigateFromAddressBar(object? parameter)
    {
        string? input = parameter as string;
        string placeholder = text.Get("Nav.AddressPlaceholder", AppConfig.Ui.AddressBarPlaceholder);
        if (!string.IsNullOrWhiteSpace(input) && input != placeholder && input != AppConfig.Ui.AddressBarPlaceholder)
        {
            browser.NavigateTo(input);
        }
    }

    private async Task RemoveFavoriteAsync(object? parameter)
    {
        if (parameter is ControlItemViewModel item)
        {
            await browser.RemoveFavoriteAsync(item.Url).ConfigureAwait(true);
        }
    }

    private async Task RemoveHistoryAsync(object? parameter)
    {
        if (parameter is ControlItemViewModel item)
        {
            await browser.RemoveHistoryEntryAsync(item.Url).ConfigureAwait(true);
        }
    }

    private void BrowserOnBrowserStateChanged(object? sender, BrowserStateChangedEventArgs e)
    {
        RefreshFromBrowser(e.Kind);
    }

    private void BrowserOnDownloadsChanged(object? sender, EventArgs e)
    {
        Dispatch(() => SyncDownloads(true));
    }

    private void TextOnLanguageChanged(object? sender, EventArgs e)
    {
        Dispatch(() =>
        {
            DownloadItem[] downloadSnapshot = browser.Downloads.ToArray();
            Downloads.Clear();
            foreach (DownloadItem item in downloadSnapshot)
            {
                Downloads.Add(item);
            }
            RefreshLocalizedTexts();
        });
    }

    private void SyncDownloads(bool autoExpand)
    {
        int previousCount = Downloads.Count;
        SyncObservableCollection(Downloads, browser.Downloads);
        if (autoExpand && Downloads.Count > previousCount)
        {
            IsDownloadsExpanded = true;
        }

        int inProgress = 0;
        int interrupted = 0;
        foreach (DownloadItem item in Downloads)
        {
            if (item.State == DownloadState.InProgress) inProgress++;
            else if (item.State == DownloadState.Interrupted) interrupted++;
        }

        DownloadsBadgeText = inProgress > 0
            ? inProgress.ToString(CultureInfo.CurrentCulture)
            : interrupted > 0
                ? interrupted.ToString(CultureInfo.CurrentCulture)
                : string.Empty;
        OnPropertyChanged(nameof(HasDownloadsBadge));
        OnPropertyChanged(nameof(HasNoDownloads));
        OnPropertyChanged(nameof(DownloadsEmptyText));
    }

    private void RestoreDefaults()
    {
        browser.RestoreDefaultSettings();
        text.Apply(browser.UiLanguage);
        RefreshFromBrowser();
        SyncDownloads(false);
        ShowToast(text.Get("Toast.RestoredDefaults", "已恢复默认设置"), StatusLevel.Success);
    }

    private async Task ClearBrowsingDataAsync()
    {
        try
        {
            if (!await browser.ClearBrowsingDataAsync().ConfigureAwait(true))
            {
                StatusMessage = browser.StatusMessage;
            }
        }
        catch (Exception ex)
        {
            string message = text.Format("Status.ClearBrowsingDataFailed", "清理 WebView2 磁盘缓存失败：{0}", ex.Message);
            StatusMessage = message;
            ShowToast(message, StatusLevel.Error);
        }
    }

    private static void ExecuteForDownload(object? parameter, Action<DownloadItem> action)
    {
        if (parameter is DownloadItem item)
        {
            action(item);
        }
    }

    private void FilterCurrentTab()
    {
        if (IsFavoritesTab) FilterFavorites();
        else FilterHistory();
    }

    private void FilterFavorites()
    {
        SyncObservableCollection(FavoriteItems, FilterItems(allFavoriteItems));
        OnPropertyChanged(nameof(ShowFavoritesEmpty));
    }

    private void FilterHistory()
    {
        SyncObservableCollection(HistoryItems, FilterItems(allHistoryItems));
        OnPropertyChanged(nameof(ShowHistoryEmpty));
    }

    private IReadOnlyList<ControlItemViewModel> FilterItems(IReadOnlyList<ControlItemViewModel> items)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return items;
        }

        return items.Where(item =>
            item.Title.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
            item.Url.Contains(SearchText, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private static void SyncObservableCollection<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
        where T : class
    {
        for (int index = target.Count - 1; index >= source.Count; index--)
        {
            target.RemoveAt(index);
        }

        for (int index = 0; index < source.Count; index++)
        {
            T sourceItem = source[index];
            if (index < target.Count && ReferenceEquals(target[index], sourceItem))
            {
                continue;
            }

            int existingIndex = target.IndexOf(sourceItem);
            if (existingIndex >= 0)
            {
                target.Move(existingIndex, index);
            }
            else if (index < target.Count)
            {
                target[index] = sourceItem;
            }
            else
            {
                target.Add(sourceItem);
            }
        }
    }

    private void SyncSourceItems(List<ControlItemViewModel> target, IReadOnlyList<FavoriteEntry> source)
    {
        Dictionary<string, ControlItemViewModel> existing = target.ToDictionary(item => item.Url, StringComparer.OrdinalIgnoreCase);
        target.Clear();
        foreach (FavoriteEntry item in source)
        {
            if (existing.TryGetValue(item.Url, out ControlItemViewModel? viewModel)) viewModel.Update(item);
            else viewModel = new ControlItemViewModel(item);
            RefreshTimeDisplay(viewModel);
            target.Add(viewModel);
        }
    }

    private void SyncSourceItems(List<ControlItemViewModel> target, IReadOnlyList<HistoryEntry> source)
    {
        Dictionary<string, ControlItemViewModel> existing = target.ToDictionary(item => item.Url, StringComparer.OrdinalIgnoreCase);
        target.Clear();
        foreach (HistoryEntry item in source)
        {
            if (existing.TryGetValue(item.Url, out ControlItemViewModel? viewModel)) viewModel.Update(item);
            else viewModel = new ControlItemViewModel(item);
            RefreshTimeDisplay(viewModel);
            target.Add(viewModel);
        }
    }

    private string NormalizeSearch(string? value)
    {
        string placeholder = text.Get("Search.Placeholder", AppConfig.Ui.SearchPlaceholder);
        return value == placeholder || value == AppConfig.Ui.SearchPlaceholder
            ? string.Empty
            : value?.Trim() ?? string.Empty;
    }

    private static bool ShouldToast(StatusLevel level)
    {
        return level is StatusLevel.Success or StatusLevel.Warning or StatusLevel.Error;
    }

    private void ShowToast(string message, StatusLevel severity)
    {
        ToastMessage = message;
        ToastSeverity = severity;
        IsToastVisible = true;
        toastTimer.Stop();
        toastTimer.Start();
    }

    private void ToastTimerOnTick(object? sender, EventArgs e)
    {
        toastTimer.Stop();
        IsToastVisible = false;
    }

    private void ApplyWindowSizeFromInput(object? parameter)
    {
        if (!TryParseSize(BrowserWindowWidthText, 640, out double width) ||
            !TryParseSize(BrowserWindowHeightText, 360, out double height))
        {
            SyncWindowSizeTexts();
            ShowToast(text.Get("Toast.InvalidWindowSize", "请输入有效的窗口宽高（像素）"), StatusLevel.Warning);
            return;
        }

        if (Math.Abs(browser.BrowserWindowWidth - width) < 0.5 &&
            Math.Abs(browser.BrowserWindowHeight - height) < 0.5)
        {
            SyncWindowSizeTexts();
            return;
        }

        browser.ResizeBrowserWindow(width, height);
        ShowToast(text.Format(
            "Toast.WindowSizeApplied",
            "窗口尺寸已设为 {0} × {1}",
            Math.Round(browser.BrowserWindowWidth),
            Math.Round(browser.BrowserWindowHeight)), StatusLevel.Success);
    }

    private void ApplyOpacityFromInput(object? parameter)
    {
        if (!TryParseNumber(OpacityPercentText, out double percent) || percent is < 10 or > 100)
        {
            SyncOpacityText();
            ShowToast(text.Get("Toast.InvalidOpacity", "请输入 10–100 的不透明度"), StatusLevel.Warning);
            return;
        }

        WindowOpacity = percent / 100.0;
    }

    private void ApplyZoomFromInput(object? parameter)
    {
        if (!TryParseNumber(ZoomPercentText, out double percent) || percent is < 25 or > 500)
        {
            SyncZoomText();
            ShowToast(text.Get("Toast.InvalidZoom", "请输入 25–500 的缩放百分比"), StatusLevel.Warning);
            return;
        }

        browser.ZoomFactor = percent / 100.0;
    }

    private void SyncOpacityZoomTexts()
    {
        SyncOpacityText();
        SyncZoomText();
    }

    private void SyncOpacityText()
    {
        opacityPercentText = (browser.WindowOpacity * 100).ToString("0.##", CultureInfo.CurrentCulture);
        OnPropertyChanged(nameof(OpacityPercentText));
    }

    private void SyncZoomText()
    {
        zoomPercentText = Math.Round(browser.ZoomFactor * 100).ToString(CultureInfo.CurrentCulture);
        OnPropertyChanged(nameof(ZoomPercentText));
    }

    private static bool TryParseSize(string? value, double minimum, out double result)
    {
        return TryParseNumber(value, out result) && result >= minimum && result <= 10_000;
    }

    private static bool TryParseNumber(string? value, out double result)
    {
        string raw = value?.Trim().TrimEnd('%') ?? string.Empty;
        return (double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out result) ||
                double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out result)) &&
               double.IsFinite(result);
    }

    private void MoveBrowserToCorner(object? parameter)
    {
        if (!Enum.TryParse(parameter as string, true, out WindowCorner corner))
        {
            corner = WindowCorner.TopRight;
        }

        browser.MoveBrowserToCorner(corner);
    }

    private void SetWindowMode(WindowMode mode)
    {
        browser.SetWindowMode(mode);
        OnPropertyChanged(nameof(IsBrowsingMode));
        OnPropertyChanged(nameof(IsFloatingMode));
    }

    private void StartRecordingToggleModeKey()
    {
        isRecordingToggleModeKey = true;
        isRecordingTogglePlaybackKey = false;
        currentRecordingModifiers = HotkeyModifiers.None;
        browser.SetHotkeyRecordingActive(true);
        OnPropertyChanged(nameof(IsRecordingAnyKey));
        NotifyHotkeyDependentTexts();
        ShowToast(text.Get("Toast.RecordToggleMode", "请按下「浏览 ⇄ 浮窗」快捷键（按 Esc 取消）"), StatusLevel.Info);
    }

    private void StartRecordingTogglePlaybackKey()
    {
        isRecordingToggleModeKey = false;
        isRecordingTogglePlaybackKey = true;
        currentRecordingModifiers = HotkeyModifiers.None;
        browser.SetHotkeyRecordingActive(true);
        OnPropertyChanged(nameof(IsRecordingAnyKey));
        NotifyHotkeyDependentTexts();
        ShowToast(text.Get("Toast.RecordPlayback", "请按下“视频播放”快捷键（按 Esc 取消）"), StatusLevel.Info);
    }

    private void StopHotkeyRecording()
    {
        isRecordingToggleModeKey = false;
        isRecordingTogglePlaybackKey = false;
        currentRecordingModifiers = HotkeyModifiers.None;
        browser.SetHotkeyRecordingActive(false);
        OnPropertyChanged(nameof(IsRecordingAnyKey));
        NotifyHotkeyDependentTexts();
    }

    private void SetTheme(string mode)
    {
        browser.ThemeMode = mode;
        RefreshThemeLanguageFlags();
        string message = mode switch
        {
            UiPreferences.Themes.Light => text.Get("Toast.ThemeLight", "已切换到亮色主题"),
            UiPreferences.Themes.System => text.Get("Toast.ThemeSystem", "已切换到跟随系统主题"),
            _ => text.Get("Toast.ThemeDark", "已切换到暗色主题"),
        };
        ShowToast(message, StatusLevel.Success);
    }

    private void SetLanguage(string language)
    {
        browser.UiLanguage = language;
        RefreshThemeLanguageFlags();
        ShowToast(language == UiPreferences.Languages.English
            ? text.Get("Toast.LanguageEn", "Language: English")
            : text.Get("Toast.LanguageZh", "界面语言：中文"), StatusLevel.Success);
    }

    private void RefreshLocalizedTexts()
    {
        RefreshFavoriteState();
        OnPropertyChanged(nameof(FavoritesEmptyText));
        OnPropertyChanged(nameof(HistoryEmptyText));
        OnPropertyChanged(nameof(DownloadsEmptyText));
        foreach (ControlItemViewModel item in allFavoriteItems)
        {
            RefreshTimeDisplay(item);
        }
        foreach (ControlItemViewModel item in allHistoryItems)
        {
            RefreshTimeDisplay(item);
        }
        NotifyHotkeyDependentTexts();
        RefreshThemeLanguageFlags();
    }

    private void RefreshFavoriteState()
    {
        IsCurrentFavorite = browser.IsFavorite(browser.CurrentAddress);
        FavoriteToggleText = IsCurrentFavorite
            ? text.Get("Fav.Added", "已收藏")
            : text.Get("Fav.Add", "收藏当前页");
        FavoriteToggleIcon = IsCurrentFavorite ? "\uE734" : "\uE735";
    }

    private void RefreshThemeLanguageFlags()
    {
        OnPropertyChanged(nameof(IsThemeDark));
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsThemeSystem));
        OnPropertyChanged(nameof(IsLanguageZh));
        OnPropertyChanged(nameof(IsLanguageEn));
    }

    private void NotifyHotkeyDependentTexts()
    {
        OnPropertyChanged(nameof(ToggleModeKeyText));
        OnPropertyChanged(nameof(TogglePlaybackKeyText));
        OnPropertyChanged(nameof(SwitchToBrowsingTooltip));
        OnPropertyChanged(nameof(SwitchToFloatingTooltip));
        OnPropertyChanged(nameof(PlaybackTooltip));
    }

    private void RefreshTimeDisplay(ControlItemViewModel item)
    {
        item.RefreshTimeDisplay(
            text.Get("Time.Today", "今天"),
            text.Get("Time.Yesterday", "昨天"));
    }

    private string FormatToggleModeHotkey() => HotkeyFormatter.Format(browser.ToggleModeHotkey);

    private string FormatTogglePlaybackHotkey() => HotkeyFormatter.Format(browser.TogglePlaybackHotkey);

    private void Dispatch(Action action)
    {
        if (dispatcher.HasThreadAccess) action();
        else dispatcher.TryEnqueue(action);
    }
}
