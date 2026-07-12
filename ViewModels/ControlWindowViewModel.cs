using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using GenshinBrowser.Constants;
using GenshinBrowser.Models;
using GenshinBrowser.Services;
using GenshinBrowser.Windows;

namespace GenshinBrowser.ViewModels;

public sealed class ControlWindowViewModel : ViewModelBase
{
    private readonly IControlBrowser _browser;
    private readonly DispatcherTimer _toastTimer;
    private readonly List<ControlItemViewModel> _allHistoryItems = new();
    private readonly List<ControlItemViewModel> _allFavoriteItems = new();
    // Sync 方法复用的临时集合，避免每次刷新都 new List/Dictionary（UI 线程独占，无需加锁）
    private readonly List<ControlItemViewModel> _syncScratchList = new();
    private readonly Dictionary<string, ControlItemViewModel> _syncScratchDict = new(StringComparer.OrdinalIgnoreCase);
    // 单次扫描用：源端 URL 集合，配合 _syncScratchDict 一次完成 target 清理 + source 插入
    private readonly HashSet<string> _syncScratchSet = new(StringComparer.OrdinalIgnoreCase);
    // 搜索过滤复用缓冲：FilterItems 返回的 IReadOnlyList 在 SyncObservableCollection 内同步期间被消费，
    // 同步返回后即可复用。避免每次搜索 Where(...).ToList() 分配新 List。
    private readonly List<ControlItemViewModel> _filterBuffer = new();
    private string _modeText = LocalizationService.Get("Mode.Free", "浏览");
    private string _modeToggleIcon = "\uE718";
    private string _statusMessage = LocalizationService.Get("Status.InitBrowser", "正在初始化浏览器...");
    private string _currentAddress = string.Empty;
    private string _favoriteToggleText = LocalizationService.Get("Fav.Add", "收藏当前页");
    private string _favoriteToggleIcon = "\uE735";
    private bool _isCurrentFavorite;
    private string _searchText = string.Empty;
    private bool _isFavoritesTab = true;
    private string _toastMessage = string.Empty;
    private StatusLevel _toastSeverity = StatusLevel.Info;
    private bool _isToastVisible;
    private Visibility _toastVisibility = Visibility.Collapsed;
    private bool _canGoBack;
    private bool _canGoForward;
    private bool _isNavigating;

    public ControlWindowViewModel(IControlBrowser browser)
    {
        _browser = browser;
        ToggleModeCommand = new RelayCommand(_browser.ToggleWindowMode);
        SetBrowsingModeCommand = new RelayCommand(() => _browser.SetWindowMode(WindowMode.Free));
        SetFloatingModeCommand = new RelayCommand(() => _browser.SetWindowMode(WindowMode.Fixed));
        TogglePlaybackCommand = new AsyncRelayCommand(_browser.ToggleVideoPlaybackAsync);
        ReloadCommand = new RelayCommand(_browser.ReloadPage);
        NavigateCommand = new RelayCommand(parameter => _browser.NavigateTo(parameter as string));
        NavigateFromAddressBarCommand = new RelayCommand(NavigateFromAddressBar);
        ToggleFavoriteCommand = new AsyncRelayCommand(ToggleFavoriteAsync);
        OpenItemCommand = new RelayCommand(NavigateToItem);
        RemoveFavoriteCommand = new AsyncRelayCommand(parameter => RemoveFavoriteAsync(parameter));
        RemoveHistoryCommand = new AsyncRelayCommand(parameter => RemoveHistoryAsync(parameter));
        GoBackCommand = new RelayCommand(_browser.GoBack, () => _browser.CanGoBack);
        GoForwardCommand = new RelayCommand(_browser.GoForward, () => _browser.CanGoForward);
        RecordToggleModeKeyCommand = new RelayCommand(StartRecordingToggleModeKey);
        RecordTogglePlaybackKeyCommand = new RelayCommand(StartRecordingTogglePlaybackKey);
        ToggleSettingsCommand = new RelayCommand(ToggleSettings);
        ToggleDownloadsCommand = new RelayCommand(ToggleDownloads);

        ZoomInCommand = new RelayCommand(() => _browser.ZoomFactor = Math.Clamp(_browser.ZoomFactor + 0.1, 0.25, 5.0));
        ZoomOutCommand = new RelayCommand(() => _browser.ZoomFactor = Math.Clamp(_browser.ZoomFactor - 0.1, 0.25, 5.0));
        ResetZoomCommand = new RelayCommand(() => _browser.ZoomFactor = 1.0);
        // 不透明度步进 5%，与设置区 −/+ 按钮一致
        OpacityInCommand = new RelayCommand(() => WindowOpacity = Math.Clamp(WindowOpacity + 0.05, 0.1, 1.0));
        OpacityOutCommand = new RelayCommand(() => WindowOpacity = Math.Clamp(WindowOpacity - 0.05, 0.1, 1.0));
        ResetOpacityCommand = new RelayCommand(() => WindowOpacity = 1.0);
        RestoreDefaultSettingsCommand = new RelayCommand(RestoreDefaults);
        ClearBrowsingDataCommand = new AsyncRelayCommand(ClearBrowsingDataAsync);
        CancelDownloadCommand = new RelayCommand(parameter => CancelDownload(parameter));
        OpenDownloadFileCommand = new RelayCommand(parameter => OpenDownloadFile(parameter));
        OpenDownloadFolderCommand = new RelayCommand(parameter => OpenDownloadFolder(parameter));
        ClearFinishedDownloadsCommand = new RelayCommand(_browser.ClearFinishedDownloads);
        SetThemeDarkCommand = new RelayCommand(() => SetTheme(ThemeService.Dark));
        SetThemeLightCommand = new RelayCommand(() => SetTheme(ThemeService.Light));
        SetLanguageZhCommand = new RelayCommand(() => SetLanguage(LocalizationService.ZhCn));
        SetLanguageEnCommand = new RelayCommand(() => SetLanguage(LocalizationService.EnUs));
        MoveBrowserToCornerCommand = new RelayCommand(MoveBrowserToCorner);
        ApplyWindowSizeCommand = new RelayCommand(ApplyWindowSizeFromInput);
        ApplyOpacityCommand = new RelayCommand(ApplyOpacityFromInput);
        ApplyZoomCommand = new RelayCommand(ApplyZoomFromInput);

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
        _toastTimer.Tick += ToastTimer_OnTick;
        _browser.BrowserStateChanged += Browser_OnBrowserStateChanged;
        _browser.ZoomChanged += Browser_OnZoomChanged;
        _browser.DownloadsChanged += Browser_OnDownloadsChanged;
        _browser.Downloads.CollectionChanged += Downloads_CollectionChanged;
        LocalizationService.LanguageChanged += Localization_OnLanguageChanged;
        UpdateDownloadsBadge();
        SyncWindowSizeTexts();
        SyncOpacityZoomTexts();
        RefreshThemeLanguageFlags();
        // 首次全量对齐
        RefreshFromBrowser(BrowserStateChangeKind.All);
    }

    public ObservableCollection<ControlItemViewModel> HistoryItems { get; } = new();

    public ObservableCollection<ControlItemViewModel> FavoriteItems { get; } = new();

    public RelayCommand GoBackCommand { get; }

    public RelayCommand GoForwardCommand { get; }

    public RelayCommand ToggleModeCommand { get; }

    public RelayCommand SetBrowsingModeCommand { get; }

    public RelayCommand SetFloatingModeCommand { get; }

    public AsyncRelayCommand TogglePlaybackCommand { get; }

    public RelayCommand ReloadCommand { get; }

    public RelayCommand NavigateCommand { get; }

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

    public RelayCommand OpenDownloadFileCommand { get; }

    public RelayCommand OpenDownloadFolderCommand { get; }

    public RelayCommand ClearFinishedDownloadsCommand { get; }

    public RelayCommand SetThemeDarkCommand { get; }

    public RelayCommand SetThemeLightCommand { get; }

    public RelayCommand SetLanguageZhCommand { get; }

    public RelayCommand SetLanguageEnCommand { get; }

    public RelayCommand MoveBrowserToCornerCommand { get; }

    public RelayCommand ApplyWindowSizeCommand { get; }

    public RelayCommand ApplyOpacityCommand { get; }

    public RelayCommand ApplyZoomCommand { get; }

    public string ModeText
    {
        get => _modeText;
        private set => SetProperty(ref _modeText, value);
    }

    public string ModeToggleIcon
    {
        get => _modeToggleIcon;
        private set => SetProperty(ref _modeToggleIcon, value);
    }

    /// <summary>当前是否为浏览模式（分段控件高亮）。</summary>
    public bool IsBrowsingMode => _browser.CurrentMode == WindowMode.Free;

    /// <summary>当前是否为浮窗模式（分段控件高亮）。</summary>
    public bool IsFloatingMode => _browser.CurrentMode == WindowMode.Fixed;

    /// <summary>切换到浏览的动作型 tooltip（含当前快捷键）。</summary>
    public string SwitchToBrowsingTooltip =>
        LocalizationService.Format("Mode.SwitchToBrowsingTooltip", FormatToggleModeHotkey());

    /// <summary>切换到浮窗的动作型 tooltip（含当前快捷键）。</summary>
    public string SwitchToFloatingTooltip =>
        LocalizationService.Format("Mode.SwitchToFloatingTooltip", FormatToggleModeHotkey());

    /// <summary>播放按钮 tooltip（含当前快捷键）。</summary>
    public string PlaybackTooltip =>
        LocalizationService.Format("Toolbar.PlaybackTooltip", FormatTogglePlaybackHotkey());

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string CurrentAddress
    {
        get => _currentAddress;
        private set => SetProperty(ref _currentAddress, value);
    }

    public string FavoriteToggleText
    {
        get => _favoriteToggleText;
        private set => SetProperty(ref _favoriteToggleText, value);
    }

    public string FavoriteToggleIcon
    {
        get => _favoriteToggleIcon;
        private set => SetProperty(ref _favoriteToggleIcon, value);
    }

    public bool IsCurrentFavorite
    {
        get => _isCurrentFavorite;
        private set => SetProperty(ref _isCurrentFavorite, value);
    }

    public bool CanGoBack
    {
        get => _canGoBack;
        private set => SetProperty(ref _canGoBack, value);
    }

    public bool CanGoForward
    {
        get => _canGoForward;
        private set => SetProperty(ref _canGoForward, value);
    }

    public bool IsNavigating
    {
        get => _isNavigating;
        private set => SetProperty(ref _isNavigating, value);
    }

    /// <summary>
    /// 合并后的全局搜索文本，根据当前 Tab 搜索收藏或历史。
    /// </summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, NormalizeSearch(value)))
            {
                // 只过滤当前可见 Tab，另一 Tab 切换时再过滤
                FilterCurrentTab();
                OnPropertyChanged(nameof(FavoritesEmptyText));
                OnPropertyChanged(nameof(HistoryEmptyText));
            }
        }
    }

    private void FilterCurrentTab()
    {
        if (_isFavoritesTab)
        {
            FilterFavorites();
        }
        else
        {
            FilterHistory();
        }
    }

    /// <summary>
    /// 当前是否显示收藏夹 Tab（默认 true）。
    /// </summary>
    public bool IsFavoritesTab
    {
        get => _isFavoritesTab;
        set
        {
            if (SetProperty(ref _isFavoritesTab, value))
            {
                OnPropertyChanged(nameof(IsHistoryTab));
                // 切换 Tab 时重新过滤新显示的列表（搜索文本可能已变）
                FilterCurrentTab();
            }
        }
    }

    /// <summary>
    /// 当前是否显示浏览记录 Tab（与 IsFavoritesTab 互斥）。
    /// </summary>
    public bool IsHistoryTab
    {
        get => !_isFavoritesTab;
        set => IsFavoritesTab = !value;
    }

    /// <summary>
    /// 收藏列表为空时的提示文案（区分「无数据」与「搜索无结果」）。
    /// </summary>
    public string FavoritesEmptyText => string.IsNullOrEmpty(SearchText)
            ? LocalizationService.Get("Empty.Favorites", "暂无收藏\n打开视频后点 ☆ 即可添加")
            : LocalizationService.Get("Empty.NoMatch", "未找到匹配项\n试试其它关键词");

    /// <summary>
    /// 历史列表为空时的提示文案。
    /// </summary>
    public string HistoryEmptyText => string.IsNullOrEmpty(SearchText)
            ? LocalizationService.Get("Empty.History", "暂无浏览记录\n访问过的页面会出现在这里")
            : LocalizationService.Get("Empty.NoMatch", "未找到匹配项\n试试其它关键词");

    public string ToastMessage
    {
        get => _toastMessage;
        private set => SetProperty(ref _toastMessage, value);
    }

    /// <summary>
    /// 当前 Toast 的严重度（Info/Success/Warning/Error）。
    /// XAML 端用 DataTrigger 据此切换图标与强调色。
    /// </summary>
    public StatusLevel ToastSeverity
    {
        get => _toastSeverity;
        private set => SetProperty(ref _toastSeverity, value);
    }

    public bool IsToastVisible
    {
        get => _isToastVisible;
        private set => SetProperty(ref _isToastVisible, value);
    }

    public Visibility ToastVisibility
    {
        get => _toastVisibility;
        private set => SetProperty(ref _toastVisibility, value);
    }

    private bool _isSettingsExpanded;

    /// <summary>
    /// 浮窗设置面板展开状态。与下载面板互斥。
    /// </summary>
    public bool IsSettingsExpanded
    {
        get => _isSettingsExpanded;
        set
        {
            if (!SetProperty(ref _isSettingsExpanded, value))
            {
                return;
            }

            if (value && _isDownloadsExpanded)
            {
                _isDownloadsExpanded = false;
                OnPropertyChanged(nameof(IsDownloadsExpanded));
            }
        }
    }

    private bool _isDownloadsExpanded;

    /// <summary>
    /// 下载管理面板展开状态。与设置面板互斥。
    /// </summary>
    public bool IsDownloadsExpanded
    {
        get => _isDownloadsExpanded;
        set
        {
            if (!SetProperty(ref _isDownloadsExpanded, value))
            {
                return;
            }

            if (value && _isSettingsExpanded)
            {
                _isSettingsExpanded = false;
                OnPropertyChanged(nameof(IsSettingsExpanded));
            }
        }
    }

    /// <summary>
    /// 当前页面缩放百分比文本，如 "120%"。
    /// </summary>
    public string ZoomPercentageText => $"{Math.Round(_browser.ZoomFactor * 100)}%";

    /// <summary>
    /// 下载列表，直接转发自浏览器宿主。
    /// </summary>
    public ObservableCollection<DownloadItem> Downloads => _browser.Downloads;

    /// <summary>
    /// 下载按钮角标文本（进行中数量，无括号）。
    /// </summary>
    public string DownloadsBadgeText
    {
        get => _downloadsBadgeText;
        private set => SetProperty(ref _downloadsBadgeText, value);
    }

    private string _downloadsBadgeText = string.Empty;

    /// <summary>是否显示下载角标（有进行中任务时）。</summary>
    public bool HasDownloadsBadge => !string.IsNullOrEmpty(DownloadsBadgeText);

    /// <summary>
    /// 是否存在中断的下载任务（用于角标变红警示，提醒用户重试或处理）。
    /// 仅在角标可见（仍有进行中任务）时联动显示。
    /// </summary>
    public bool HasInterruptedDownloads
    {
        get => _hasInterruptedDownloads;
        private set => SetProperty(ref _hasInterruptedDownloads, value);
    }

    private bool _hasInterruptedDownloads;

    /// <summary>下载列表是否为空（用于空状态展示）。</summary>
    public bool HasNoDownloads => Downloads.Count == 0;

    /// <summary>
    /// 下载列表为空时的提示文案。
    /// </summary>
    public string DownloadsEmptyText => LocalizationService.Get(
        "Downloads.Empty",
        "暂无下载任务\n文件下载会出现在这里");

    /// <summary>
    /// 全量刷新（兼容旧调用点，如恢复默认设置）。
    /// </summary>
    public void RefreshFromBrowser() => RefreshFromBrowser(BrowserStateChangeKind.All);

    /// <summary>
    /// 按 <paramref name="kind"/> 增量刷新控制窗状态，避免状态文案变化时同步历史/收藏列表。
    /// </summary>
    public void RefreshFromBrowser(BrowserStateChangeKind kind)
    {
        if (kind == BrowserStateChangeKind.None)
        {
            return;
        }

        var previousStatus = StatusMessage;

        if (kind.HasFlag(BrowserStateChangeKind.Mode))
        {
            ModeText = _browser.CurrentMode == WindowMode.Fixed
                ? LocalizationService.Get("Mode.Fixed", "浮窗")
                : LocalizationService.Get("Mode.Free", "浏览");
            ModeToggleIcon = _browser.CurrentMode == WindowMode.Fixed ? "" : "";
            OnPropertyChanged(nameof(IsBrowsingMode));
            OnPropertyChanged(nameof(IsFloatingMode));
            NotifyHotkeyDependentTexts();
        }

        if (kind.HasFlag(BrowserStateChangeKind.Status))
        {
            StatusMessage = _browser.StatusMessage;
        }

        if (kind.HasFlag(BrowserStateChangeKind.Navigation))
        {
            CurrentAddress = _browser.CurrentAddress;
            CanGoBack = _browser.CanGoBack;
            CanGoForward = _browser.CanGoForward;
            IsNavigating = _browser.IsNavigating;

            IsCurrentFavorite = _browser.IsFavorite(_browser.CurrentAddress);
            FavoriteToggleText = IsCurrentFavorite
                ? LocalizationService.Get("Fav.Added", "已收藏")
                : LocalizationService.Get("Fav.Add", "收藏当前页");
            FavoriteToggleIcon = IsCurrentFavorite ? "" : "";

            GoBackCommand.RaiseCanExecuteChanged();
            GoForwardCommand.RaiseCanExecuteChanged();
        }

        if (kind.HasFlag(BrowserStateChangeKind.Favorites))
        {
            SyncSourceItems(_allFavoriteItems, _browser.FavoriteEntries, item => new ControlItemViewModel(item), (viewModel, item) => viewModel.Update(item));
            FilterFavorites();

            // 收藏列表变更时同步当前页星标（即使未带 Navigation）
            if (!kind.HasFlag(BrowserStateChangeKind.Navigation))
            {
                IsCurrentFavorite = _browser.IsFavorite(_browser.CurrentAddress);
                FavoriteToggleText = IsCurrentFavorite
                    ? LocalizationService.Get("Fav.Added", "已收藏")
                    : LocalizationService.Get("Fav.Add", "收藏当前页");
                FavoriteToggleIcon = IsCurrentFavorite ? "" : "";
            }
        }

        if (kind.HasFlag(BrowserStateChangeKind.History))
        {
            SyncSourceItems(_allHistoryItems, _browser.HistoryEntries, item => new ControlItemViewModel(item), (viewModel, item) => viewModel.Update(item));
            FilterHistory();
        }

        if (kind.HasFlag(BrowserStateChangeKind.Status)
            && !string.Equals(previousStatus, StatusMessage, StringComparison.Ordinal)
            && ShouldToast(_browser.LastStatusLevel))
        {
            ShowToast(StatusMessage, _browser.LastStatusLevel);
        }

        if (kind.HasFlag(BrowserStateChangeKind.Appearance))
        {
            OnPropertyChanged(nameof(ZoomPercentageText));
            OnPropertyChanged(nameof(OpacityPercentageText));
            OnPropertyChanged(nameof(WindowOpacity));
            // 仅在输入框为空时回填，避免刷新覆盖用户正在编辑的数字。
            // 窗口尺寸由 ControlWindow.RefreshWindowSizeDisplay 在焦点安全时同步。
            if (string.IsNullOrWhiteSpace(_opacityPercentText) || string.IsNullOrWhiteSpace(_zoomPercentText))
            {
                SyncOpacityZoomTexts();
            }
        }
    }

    public void Dispose()
    {
        _browser.BrowserStateChanged -= Browser_OnBrowserStateChanged;
        _browser.ZoomChanged -= Browser_OnZoomChanged;
        _browser.DownloadsChanged -= Browser_OnDownloadsChanged;
        _browser.Downloads.CollectionChanged -= Downloads_CollectionChanged;
        LocalizationService.LanguageChanged -= Localization_OnLanguageChanged;
        _toastTimer.Stop();
        _toastTimer.Tick -= ToastTimer_OnTick;
    }

    private async Task ToggleFavoriteAsync()
    {
        if (IsCurrentFavorite)
        {
            await _browser.RemoveFavoriteAsync(_browser.CurrentAddress).ConfigureAwait(true);
            return;
        }

        await _browser.AddCurrentPageToFavoritesAsync().ConfigureAwait(true);
    }

    private void NavigateToItem(object? parameter)
    {
        if (parameter is ControlItemViewModel item)
        {
            _browser.NavigateTo(item.Url);
        }
    }

    private void NavigateFromAddressBar(object? parameter)
    {
        var input = parameter as string;
        // 忽略占位符文本，避免误触发搜索
        var addressPlaceholder = LocalizationService.Get("Nav.AddressPlaceholder", AppConfig.Ui.AddressBarPlaceholder);
        if (string.IsNullOrEmpty(input) || input == addressPlaceholder || input == AppConfig.Ui.AddressBarPlaceholder)
        {
            return;
        }

        _browser.NavigateTo(input);
    }

    private async Task RemoveFavoriteAsync(object? parameter)
    {
        if (parameter is ControlItemViewModel item)
        {
            await _browser.RemoveFavoriteAsync(item.Url).ConfigureAwait(true);
        }
    }

    private async Task RemoveHistoryAsync(object? parameter)
    {
        if (parameter is ControlItemViewModel item)
        {
            await _browser.RemoveHistoryEntryAsync(item.Url).ConfigureAwait(true);
        }
    }

    private void Browser_OnBrowserStateChanged(object? sender, BrowserStateChangedEventArgs e)
        => RefreshFromBrowser(e.Kind);

    private void Browser_OnZoomChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ZoomPercentageText));
        SyncOpacityZoomTexts();
    }

    private void Browser_OnDownloadsChanged(object? sender, EventArgs e)
    {
        UpdateDownloadsBadge();
        OnPropertyChanged(nameof(DownloadsEmptyText));
    }

    private void Downloads_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // 新增下载时自动展开下载面板（setter 会互斥关闭设置面板）
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && _browser.Downloads.Count > 0)
        {
            IsDownloadsExpanded = true;
        }
        UpdateDownloadsBadge();
        OnPropertyChanged(nameof(DownloadsEmptyText));
    }

    private void ToggleSettings()
    {
        IsSettingsExpanded = !IsSettingsExpanded;
    }

    private void ToggleDownloads()
    {
        IsDownloadsExpanded = !IsDownloadsExpanded;
    }

    private void UpdateDownloadsBadge()
    {
        var inProgress = 0;
        var interrupted = 0;
        foreach (var item in _browser.Downloads)
        {
            if (item.State == DownloadState.InProgress) inProgress++;
            else if (item.State == DownloadState.Interrupted) interrupted++;
        }
        DownloadsBadgeText = inProgress > 0 ? inProgress.ToString() : string.Empty;
        // 仅有进行中任务时才显示角标，因此中断状态也只在此时联动；无进行中时清掉红色
        HasInterruptedDownloads = inProgress > 0 && interrupted > 0;
        OnPropertyChanged(nameof(HasDownloadsBadge));
        OnPropertyChanged(nameof(HasNoDownloads));
    }

    private void RestoreDefaults()
    {
        _browser.RestoreDefaultSettings();
        RefreshFromBrowser();
        SyncOpacityZoomTexts();
        SyncWindowSizeTexts();
        ShowToast(LocalizationService.Get("Toast.RestoredDefaults", "已恢复默认设置"), StatusLevel.Success);
    }

    private async Task ClearBrowsingDataAsync()
    {
        // 不立刻禁用按钮：AsyncRelayCommand 默认支持并发保护；这里给个直观反馈
        StatusMessage = LocalizationService.Get("Status.ClearingBrowsingData", "正在清理浏览数据...");
        try
        {
            await _browser.ClearBrowsingDataAsync().ConfigureAwait(true);
            StatusMessage = LocalizationService.Get("Status.BrowsingDataCleared", "已清理浏览数据。");
            ShowToast(LocalizationService.Get("Toast.BrowsingDataCleared", "已清理浏览数据"), StatusLevel.Success);
        }
        catch (Exception ex)
        {
            var msg = LocalizationService.Format("Status.ClearBrowsingDataFailed", ex.Message);
            StatusMessage = msg;
            ShowToast(msg, StatusLevel.Error);
        }
    }

    private void CancelDownload(object? parameter)
    {
        if (parameter is DownloadItem item)
        {
            _browser.CancelDownload(item);
        }
    }

    private void OpenDownloadFile(object? parameter)
    {
        if (parameter is DownloadItem item)
        {
            _browser.OpenDownloadFile(item);
        }
    }

    private void OpenDownloadFolder(object? parameter)
    {
        if (parameter is DownloadItem item)
        {
            _browser.OpenDownloadFolder(item);
        }
    }

    private void FilterFavorites()
    {
        SyncObservableCollection(FavoriteItems, FilterItems(_allFavoriteItems, SearchText));
    }

    private void FilterHistory()
    {
        SyncObservableCollection(HistoryItems, FilterItems(_allHistoryItems, SearchText));
    }

    private IReadOnlyList<ControlItemViewModel> FilterItems(IReadOnlyList<ControlItemViewModel> items, string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            // 无搜索条件：直接返回原集合，跳过 _filterBuffer 复制
            return items;
        }

        // 复用 _filterBuffer，避免每次按键都 Where(...).ToList() 分配新 List。
        // 由于 FilterFavorites/FilterHistory 在 UI 线程顺序调用、SyncObservableCollection 同步消费返回值，
        // 两次调用之间 _filterBuffer 会被 Clear+重新填充，不存在交叉引用问题。
        _filterBuffer.Clear();
        _filterBuffer.EnsureCapacity(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Title.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                item.Url.Contains(searchText, StringComparison.OrdinalIgnoreCase))
            {
                _filterBuffer.Add(item);
            }
        }
        return _filterBuffer;
    }

    private void SyncObservableCollection(ObservableCollection<ControlItemViewModel> target, IReadOnlyList<ControlItemViewModel> source)
    {
        // 单次扫描策略：
        //   1) _syncScratchSet ← source URLs（仅用于判断 target 是否需要删除，集合够用）
        //   2) 倒序扫 target：不在 set 的直接 Remove，仍在的同步登记进 _syncScratchDict（url → 现存 item），
        //      这样删完一遍就把 dict 建好，省掉原本第二轮 target 重建 dict 的开销
        //   3) 正序扫 source：dict 命中则 UpdateFrom + 必要时 Reorder，未命中则 Insert +登记进 dict
        _syncScratchSet.EnsureCapacity(source.Count);
        _syncScratchSet.Clear();
        for (var i = 0; i < source.Count; i++)
        {
            _syncScratchSet.Add(source[i].Url);
        }

        _syncScratchDict.EnsureCapacity(Math.Max(target.Count, source.Count));
        _syncScratchDict.Clear();
        for (var targetIndex = target.Count - 1; targetIndex >= 0; targetIndex--)
        {
            var targetItem = target[targetIndex];
            if (_syncScratchSet.Contains(targetItem.Url))
            {
                // 保留：同时登记到 dict，供后续 source 扫描做 O(1) 查找
                _syncScratchDict[targetItem.Url] = targetItem;
            }
            else
            {
                target.RemoveAt(targetIndex);
            }
        }

        // _syncScratchSet 已完成使命，立刻清掉释放内部 bucket 引用（容量保留）
        _syncScratchSet.Clear();

        for (var sourceIndex = 0; sourceIndex < source.Count; sourceIndex++)
        {
            var sourceItem = source[sourceIndex];
            if (!_syncScratchDict.TryGetValue(sourceItem.Url, out var existingItem))
            {
                target.Insert(sourceIndex, sourceItem);
                _syncScratchDict[sourceItem.Url] = sourceItem;
                continue;
            }

            existingItem.UpdateFrom(sourceItem);
            if (sourceIndex >= target.Count || !string.Equals(target[sourceIndex].Url, sourceItem.Url, StringComparison.OrdinalIgnoreCase))
            {
                target.Remove(existingItem);
                target.Insert(sourceIndex, existingItem);
            }
        }
    }

    private void SyncSourceItems<T>(List<ControlItemViewModel> target, IReadOnlyList<T> source, Func<T, ControlItemViewModel> create, Action<ControlItemViewModel, T> update)
        where T : class
    {
        _syncScratchDict.Clear();
        for (var i = 0; i < target.Count; i++)
        {
            _syncScratchDict[target[i].Url] = target[i];
        }

        _syncScratchList.Clear();
        _syncScratchList.EnsureCapacity(source.Count);

        for (var sourceIndex = 0; sourceIndex < source.Count; sourceIndex++)
        {
            var sourceItem = source[sourceIndex];
            var sourceUrl = GetUrl(sourceItem);
            if (!_syncScratchDict.TryGetValue(sourceUrl, out var existingItem))
            {
                _syncScratchList.Add(create(sourceItem));
                continue;
            }

            update(existingItem, sourceItem);
            _syncScratchList.Add(existingItem);
        }

        target.Clear();
        target.AddRange(_syncScratchList);
    }

    private static string GetUrl<T>(T item) where T : class
    {
        return item switch
        {
            HistoryEntry history => history.Url,
            FavoriteEntry favorite => favorite.Url,
            _ => string.Empty
        };
    }

    private static string NormalizeSearch(string? text)
    {
        var placeholder = LocalizationService.Get("Search.Placeholder", AppConfig.Ui.SearchPlaceholder);
        return text == placeholder || text == AppConfig.Ui.SearchPlaceholder
            ? string.Empty
            : text?.Trim() ?? string.Empty;
    }

    private static bool ShouldToast(StatusLevel level)
    {
        return level is StatusLevel.Success or StatusLevel.Warning or StatusLevel.Error;
    }

    private void ShowToast(string message) => ShowToast(message, StatusLevel.Info);

    private void ShowToast(string message, StatusLevel severity)
    {
        ToastMessage = message;
        ToastSeverity = severity;
        IsToastVisible = true;
        ToastVisibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void ToastTimer_OnTick(object? sender, EventArgs e)
    {
        _toastTimer.Stop();
        IsToastVisible = false;
        _ = HideToastAfterAnimationAsync();
    }

    private async Task HideToastAfterAnimationAsync()
    {
        await Task.Delay(180).ConfigureAwait(true);
        if (!IsToastVisible)
        {
            ToastVisibility = Visibility.Collapsed;
        }
    }

    private bool _isRecordingToggleModeKey;
    private bool _isRecordingTogglePlaybackKey;
    private ModifierKeys _currentRecordingModifiers;


    public string BrowserWindowWidthText
    {
        get => _browserWindowWidthText;
        set
        {
            if (SetProperty(ref _browserWindowWidthText, value ?? string.Empty))
            {
                // 输入中不立刻改窗体，失焦/回车由 Apply 提交
            }
        }
    }

    public string BrowserWindowHeightText
    {
        get => _browserWindowHeightText;
        set => SetProperty(ref _browserWindowHeightText, value ?? string.Empty);
    }

    private string _browserWindowWidthText = string.Empty;
    private string _browserWindowHeightText = string.Empty;

    /// <summary>
    /// 用主窗当前尺寸刷新设置里的宽高文本（调用方需确保用户未在编辑输入框）。
    /// </summary>
    public void SyncWindowSizeTexts()
    {
        var widthText = Math.Round(_browser.BrowserWindowWidth).ToString();
        var heightText = Math.Round(_browser.BrowserWindowHeight).ToString();

        if (string.Equals(_browserWindowWidthText, widthText, StringComparison.Ordinal)
            && string.Equals(_browserWindowHeightText, heightText, StringComparison.Ordinal))
        {
            return;
        }

        _browserWindowWidthText = widthText;
        _browserWindowHeightText = heightText;
        OnPropertyChanged(nameof(BrowserWindowWidthText));
        OnPropertyChanged(nameof(BrowserWindowHeightText));
    }

    private void ApplyWindowSizeFromInput(object? parameter = null)
    {
        if (!TryParseSize(_browserWindowWidthText, out var width) ||
            !TryParseSize(_browserWindowHeightText, out var height))
        {
            SyncWindowSizeTexts();
            ShowToast(LocalizationService.Get("Toast.InvalidWindowSize", "请输入有效的窗口宽高（像素）"), StatusLevel.Warning);
            return;
        }

        _browser.BrowserWindowWidth = width;
        _browser.BrowserWindowHeight = height;
        SyncWindowSizeTexts();
        ShowToast(LocalizationService.Format("Toast.WindowSizeApplied", Math.Round(width), Math.Round(height)), StatusLevel.Success);
    }

    private static bool TryParseSize(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (!double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) &&
            !double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out value))
        {
            return false;
        }

        return value >= 100 && value <= 10000;
    }


    public string OpacityPercentText
    {
        get => _opacityPercentText;
        set => SetProperty(ref _opacityPercentText, value ?? string.Empty);
    }

    public string ZoomPercentText
    {
        get => _zoomPercentText;
        set => SetProperty(ref _zoomPercentText, value ?? string.Empty);
    }

    private string _opacityPercentText = string.Empty;
    private string _zoomPercentText = string.Empty;

    private void SyncOpacityZoomTexts()
    {
        _opacityPercentText = Math.Round(_browser.WindowOpacity * 100).ToString();
        _zoomPercentText = Math.Round(_browser.ZoomFactor * 100).ToString();
        OnPropertyChanged(nameof(OpacityPercentText));
        OnPropertyChanged(nameof(ZoomPercentText));
        OnPropertyChanged(nameof(OpacityPercentageText));
        OnPropertyChanged(nameof(ZoomPercentageText));
    }

    private void ApplyOpacityFromInput(object? parameter = null)
    {
        if (!TryParsePercent(_opacityPercentText, out var percent) || percent < 10 || percent > 100)
        {
            SyncOpacityZoomTexts();
            ShowToast(LocalizationService.Get("Toast.InvalidOpacity", "请输入 10–100 的不透明度"), StatusLevel.Warning);
            return;
        }

        WindowOpacity = percent / 100.0;
        SyncOpacityZoomTexts();
    }

    private void ApplyZoomFromInput(object? parameter = null)
    {
        if (!TryParsePercent(_zoomPercentText, out var percent) || percent < 25 || percent > 500)
        {
            SyncOpacityZoomTexts();
            ShowToast(LocalizationService.Get("Toast.InvalidZoom", "请输入 25–500 的缩放百分比"), StatusLevel.Warning);
            return;
        }

        _browser.ZoomFactor = percent / 100.0;
        SyncOpacityZoomTexts();
    }

    private void MoveBrowserToCorner(object? parameter)
    {
        var corner = parameter as string ?? "TopRight";
        _browser.MoveBrowserToCorner(corner);
    }

    private static bool TryParsePercent(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var raw = text.Trim().TrimEnd('%');
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) &&
            !double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out value))
        {
            return false;
        }

        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    public double WindowOpacity
    {
        get => _browser.WindowOpacity;
        set
        {
            if (Math.Abs(_browser.WindowOpacity - value) > 0.001)
            {
                _browser.WindowOpacity = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(OpacityPercentageText));
                _opacityPercentText = Math.Round(value * 100).ToString();
                OnPropertyChanged(nameof(OpacityPercentText));
            }
        }
    }

    public string OpacityPercentageText => $"{Math.Round(_browser.WindowOpacity * 100)}%";

    public bool IsRecordingAnyKey => _isRecordingToggleModeKey || _isRecordingTogglePlaybackKey;

    public string ToggleModeKeyText
    {
        get
        {
            if (_isRecordingToggleModeKey)
            {
                return HotkeyFormatter.GetModifiersString(_currentRecordingModifiers) + "...";
            }
            return FormatToggleModeHotkey();
        }
    }

    public string TogglePlaybackKeyText
    {
        get
        {
            if (_isRecordingTogglePlaybackKey)
            {
                return HotkeyFormatter.GetModifiersString(_currentRecordingModifiers) + "...";
            }
            return FormatTogglePlaybackHotkey();
        }
    }

    private void StartRecordingToggleModeKey()
    {
        _isRecordingToggleModeKey = true;
        _isRecordingTogglePlaybackKey = false;
        _currentRecordingModifiers = ModifierKeys.None;
        OnPropertyChanged(nameof(ToggleModeKeyText));
        OnPropertyChanged(nameof(TogglePlaybackKeyText));
        ShowToast(LocalizationService.Get("Toast.RecordToggleMode", "请按下「浏览 ⇄ 浮窗」快捷键（按 Esc 取消）"), StatusLevel.Info);
    }

    private void StartRecordingTogglePlaybackKey()
    {
        _isRecordingTogglePlaybackKey = true;
        _isRecordingToggleModeKey = false;
        _currentRecordingModifiers = ModifierKeys.None;
        OnPropertyChanged(nameof(ToggleModeKeyText));
        OnPropertyChanged(nameof(TogglePlaybackKeyText));
        ShowToast(LocalizationService.Get("Toast.RecordPlayback", "请按下“视频播放”快捷键（按 Esc 取消）"), StatusLevel.Info);
    }

    public void UpdateRecordingModifiers(ModifierKeys modifiers)
    {
        _currentRecordingModifiers = modifiers;
        OnPropertyChanged(nameof(ToggleModeKeyText));
        OnPropertyChanged(nameof(TogglePlaybackKeyText));
    }

    public void FinishRecordingKey(Key key, ModifierKeys modifiers)
    {
        if (key == Key.Escape)
        {
            _isRecordingToggleModeKey = false;
            _isRecordingTogglePlaybackKey = false;
            _currentRecordingModifiers = ModifierKeys.None;
            OnPropertyChanged(nameof(ToggleModeKeyText));
            OnPropertyChanged(nameof(TogglePlaybackKeyText));
            ShowToast(LocalizationService.Get("Toast.RecordCanceled", "已取消录制。"), StatusLevel.Warning);
            return;
        }

        if (_isRecordingToggleModeKey)
        {
            if (key == _browser.TogglePlaybackKey && modifiers == _browser.TogglePlaybackModifiers)
            {
                ShowToast(LocalizationService.Get("Toast.HotkeyUsedByPlayback", "该快捷键已被视频播放占用！"), StatusLevel.Warning);
            }
            else
            {
                _browser.ToggleModeKey = key;
                _browser.ToggleModeModifiers = modifiers;
                ShowToast(LocalizationService.Format("Toast.ToggleModeSet", HotkeyFormatter.Format(key, modifiers)), StatusLevel.Success);
            }
            _isRecordingToggleModeKey = false;
        }
        else if (_isRecordingTogglePlaybackKey)
        {
            if (key == _browser.ToggleModeKey && modifiers == _browser.ToggleModeModifiers)
            {
                ShowToast(LocalizationService.Get("Toast.HotkeyUsedByMode", "该快捷键已被「浏览 ⇄ 浮窗」占用！"), StatusLevel.Warning);
            }
            else
            {
                _browser.TogglePlaybackKey = key;
                _browser.TogglePlaybackModifiers = modifiers;
                ShowToast(LocalizationService.Format("Toast.PlaybackSet", HotkeyFormatter.Format(key, modifiers)), StatusLevel.Success);
            }
            _isRecordingTogglePlaybackKey = false;
        }

        _currentRecordingModifiers = ModifierKeys.None;
        NotifyHotkeyDependentTexts();
    }


    public bool IsThemeDark => string.Equals(_browser.ThemeMode, ThemeService.Dark, StringComparison.OrdinalIgnoreCase);

    public bool IsThemeLight => string.Equals(_browser.ThemeMode, ThemeService.Light, StringComparison.OrdinalIgnoreCase);

    public bool IsLanguageZh => string.Equals(_browser.UiLanguage, LocalizationService.ZhCn, StringComparison.OrdinalIgnoreCase);

    public bool IsLanguageEn => string.Equals(_browser.UiLanguage, LocalizationService.EnUs, StringComparison.OrdinalIgnoreCase);

    private void SetTheme(string mode)
    {
        _browser.ThemeMode = mode;
        RefreshThemeLanguageFlags();
        ShowToast(mode == ThemeService.Light
            ? LocalizationService.Get("Toast.ThemeLight", "已切换到亮色主题")
            : LocalizationService.Get("Toast.ThemeDark", "已切换到暗色主题"), StatusLevel.Success);
    }

    private void SetLanguage(string language)
    {
        _browser.UiLanguage = language;
        RefreshLocalizedTexts();
        RefreshThemeLanguageFlags();
        ShowToast(language == LocalizationService.EnUs
            ? LocalizationService.Get("Toast.LanguageEn", "Language: English")
            : LocalizationService.Get("Toast.LanguageZh", "界面语言：中文"), StatusLevel.Success);
    }

    private void Localization_OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLocalizedTexts();
        RefreshThemeLanguageFlags();
        // 下载状态文案依赖语言资源
        foreach (var item in Downloads)
        {
            item.NotifyLanguageChanged();
        }
    }

    private void RefreshThemeLanguageFlags()
    {
        OnPropertyChanged(nameof(IsThemeDark));
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsLanguageZh));
        OnPropertyChanged(nameof(IsLanguageEn));
    }

    private void RefreshLocalizedTexts()
    {
        ModeText = _browser.CurrentMode == WindowMode.Fixed
            ? LocalizationService.Get("Mode.Fixed", "浮窗")
            : LocalizationService.Get("Mode.Free", "浏览");
        OnPropertyChanged(nameof(IsBrowsingMode));
        OnPropertyChanged(nameof(IsFloatingMode));
        FavoriteToggleText = IsCurrentFavorite
            ? LocalizationService.Get("Fav.Added", "已收藏")
            : LocalizationService.Get("Fav.Add", "收藏当前页");
        OnPropertyChanged(nameof(FavoritesEmptyText));
        OnPropertyChanged(nameof(HistoryEmptyText));
        OnPropertyChanged(nameof(DownloadsEmptyText));
        NotifyHotkeyDependentTexts();
        StatusMessage = _browser.StatusMessage;
    }

    private void NotifyHotkeyDependentTexts()
    {
        OnPropertyChanged(nameof(ToggleModeKeyText));
        OnPropertyChanged(nameof(TogglePlaybackKeyText));
        OnPropertyChanged(nameof(SwitchToBrowsingTooltip));
        OnPropertyChanged(nameof(SwitchToFloatingTooltip));
        OnPropertyChanged(nameof(PlaybackTooltip));
    }

    private string FormatToggleModeHotkey() =>
        HotkeyFormatter.Format(_browser.ToggleModeKey, _browser.ToggleModeModifiers);

    private string FormatTogglePlaybackHotkey() =>
        HotkeyFormatter.Format(_browser.TogglePlaybackKey, _browser.TogglePlaybackModifiers);
}
