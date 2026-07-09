using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using GenshinBrowser.Constants;
using GenshinBrowser.Models;
using GenshinBrowser.Windows;

namespace GenshinBrowser.ViewModels;

public sealed class ControlWindowViewModel : ViewModelBase
{
    private readonly IControlBrowser _browser;
    private readonly DispatcherTimer _toastTimer;
    private readonly List<ControlItemViewModel> _allHistoryItems = new();
    private readonly List<ControlItemViewModel> _allFavoriteItems = new();
    private string _modeText = "自由模式";
    private string _modeToggleIcon = "\uE718";
    private string _statusMessage = "正在初始化浏览器...";
    private string _currentAddress = string.Empty;
    private string _favoriteToggleText = "收藏当前页";
    private string _favoriteToggleIcon = "\uE735";
    private bool _isCurrentFavorite;
    private string _favoriteSearchText = string.Empty;
    private string _historySearchText = string.Empty;
    private string _toastMessage = string.Empty;
    private bool _isToastVisible;
    private Visibility _toastVisibility = Visibility.Collapsed;
    private bool _canGoBack;
    private bool _canGoForward;

    public ControlWindowViewModel(IControlBrowser browser)
    {
        _browser = browser;
        ToggleModeCommand = new RelayCommand(_browser.ToggleWindowMode);
        TogglePlaybackCommand = new AsyncRelayCommand(_browser.ToggleVideoPlaybackAsync);
        NavigateHomeCommand = new RelayCommand(_browser.NavigateHome);
        ReloadCommand = new RelayCommand(_browser.ReloadPage);
        NavigateCommand = new RelayCommand(parameter => _browser.NavigateTo(parameter as string));
        NavigateFromAddressBarCommand = new RelayCommand(NavigateFromAddressBar);
        ToggleFavoriteCommand = new AsyncRelayCommand(ToggleFavoriteAsync);
        ClearHistoryCommand = new AsyncRelayCommand(ClearHistoryWithConfirmAsync);
        OpenItemCommand = new RelayCommand(NavigateToItem);
        RemoveFavoriteCommand = new AsyncRelayCommand(parameter => RemoveFavoriteAsync(parameter));
        RemoveHistoryCommand = new AsyncRelayCommand(parameter => RemoveHistoryAsync(parameter));
        GoBackCommand = new RelayCommand(_browser.GoBack, () => _browser.CanGoBack);
        GoForwardCommand = new RelayCommand(_browser.GoForward, () => _browser.CanGoForward);
        RecordToggleModeKeyCommand = new RelayCommand(StartRecordingToggleModeKey);
        RecordTogglePlaybackKeyCommand = new RelayCommand(StartRecordingTogglePlaybackKey);

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
        _toastTimer.Tick += ToastTimer_OnTick;
        _browser.BrowserStateChanged += Browser_OnBrowserStateChanged;
    }

    public ObservableCollection<ControlItemViewModel> HistoryItems { get; } = new();

    public ObservableCollection<ControlItemViewModel> FavoriteItems { get; } = new();

    public RelayCommand GoBackCommand { get; }

    public RelayCommand GoForwardCommand { get; }

    public RelayCommand ToggleModeCommand { get; }

    public AsyncRelayCommand TogglePlaybackCommand { get; }

    public RelayCommand NavigateHomeCommand { get; }

    public RelayCommand ReloadCommand { get; }

    public RelayCommand NavigateCommand { get; }

    public RelayCommand NavigateFromAddressBarCommand { get; }

    public AsyncRelayCommand ToggleFavoriteCommand { get; }

    public AsyncRelayCommand ClearHistoryCommand { get; }

    /// <summary>
    /// 清空历史前的确认回调，由视图层注入（显示主题化确认框）。
    /// 返回 true 才会真正清空。
    /// </summary>
    public Func<bool>? ConfirmClear { get; set; }

    public RelayCommand OpenItemCommand { get; }

    public AsyncRelayCommand RemoveFavoriteCommand { get; }

    public AsyncRelayCommand RemoveHistoryCommand { get; }

    public RelayCommand RecordToggleModeKeyCommand { get; }

    public RelayCommand RecordTogglePlaybackKeyCommand { get; }

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

    public string FavoriteSearchText
    {
        get => _favoriteSearchText;
        set
        {
            if (SetProperty(ref _favoriteSearchText, NormalizeSearch(value)))
            {
                FilterFavorites();
                OnPropertyChanged(nameof(FavoritesEmptyText));
            }
        }
    }

    public string HistorySearchText
    {
        get => _historySearchText;
        set
        {
            if (SetProperty(ref _historySearchText, NormalizeSearch(value)))
            {
                FilterHistory();
                OnPropertyChanged(nameof(HistoryEmptyText));
            }
        }
    }

    /// <summary>
    /// 收藏列表为空时的提示文案（区分「无数据」与「搜索无结果」）。
    /// </summary>
    public string FavoritesEmptyText => string.IsNullOrEmpty(FavoriteSearchText) ? "暂无收藏" : "未找到匹配项";

    /// <summary>
    /// 历史列表为空时的提示文案。
    /// </summary>
    public string HistoryEmptyText => string.IsNullOrEmpty(HistorySearchText) ? "暂无浏览记录" : "未找到匹配项";

    public string ToastMessage
    {
        get => _toastMessage;
        private set => SetProperty(ref _toastMessage, value);
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

    public void RefreshFromBrowser()
    {
        var previousStatus = StatusMessage;

        ModeText = _browser.CurrentMode == WindowMode.Fixed ? "固定模式" : "自由模式";
        ModeToggleIcon = _browser.CurrentMode == WindowMode.Fixed ? "\uE785" : "\uE718";
        StatusMessage = _browser.StatusMessage;
        CurrentAddress = _browser.CurrentAddress;

        CanGoBack = _browser.CanGoBack;
        CanGoForward = _browser.CanGoForward;

        IsCurrentFavorite = _browser.IsFavorite(_browser.CurrentAddress);
        FavoriteToggleText = IsCurrentFavorite ? "已收藏" : "收藏当前页";
        FavoriteToggleIcon = IsCurrentFavorite ? "\uE734" : "\uE735";

        SyncSourceItems(_allFavoriteItems, _browser.FavoriteEntries, item => new ControlItemViewModel(item), (viewModel, item) => viewModel.Update(item));
        FilterFavorites();

        SyncSourceItems(_allHistoryItems, _browser.HistoryEntries, item => new ControlItemViewModel(item), (viewModel, item) => viewModel.Update(item));
        FilterHistory();

        if (!string.Equals(previousStatus, StatusMessage, StringComparison.Ordinal) && ShouldToast(StatusMessage))
        {
            ShowToast(StatusMessage);
        }

        GoBackCommand.RaiseCanExecuteChanged();
        GoForwardCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        _browser.BrowserStateChanged -= Browser_OnBrowserStateChanged;
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
        if (string.IsNullOrEmpty(input) || input == AppConfig.Ui.AddressBarPlaceholder)
        {
            return;
        }

        _browser.NavigateTo(input);
    }

    private async Task ClearHistoryWithConfirmAsync()
    {
        if (ConfirmClear?.Invoke() != true)
        {
            return;
        }

        await _browser.ClearHistoryAsync().ConfigureAwait(true);
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

    private void Browser_OnBrowserStateChanged(object? sender, EventArgs e) => RefreshFromBrowser();

    private void FilterFavorites()
    {
        SyncObservableCollection(FavoriteItems, FilterItems(_allFavoriteItems, FavoriteSearchText));
    }

    private void FilterHistory()
    {
        SyncObservableCollection(HistoryItems, FilterItems(_allHistoryItems, HistorySearchText));
    }

    private static IReadOnlyList<ControlItemViewModel> FilterItems(IReadOnlyList<ControlItemViewModel> items, string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return items;
        }

        return items.Where(item =>
            item.Title.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
            item.Url.Contains(searchText, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static void SyncObservableCollection(ObservableCollection<ControlItemViewModel> target, IReadOnlyList<ControlItemViewModel> source)
    {
        var sourceUrls = source
            .Select(item => item.Url)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var targetIndex = target.Count - 1; targetIndex >= 0; targetIndex--)
        {
            if (!sourceUrls.Contains(target[targetIndex].Url))
            {
                target.RemoveAt(targetIndex);
            }
        }

        var targetByUrl = target.ToDictionary(item => item.Url, StringComparer.OrdinalIgnoreCase);

        for (var sourceIndex = 0; sourceIndex < source.Count; sourceIndex++)
        {
            var sourceItem = source[sourceIndex];
            if (!targetByUrl.TryGetValue(sourceItem.Url, out var existingItem))
            {
                target.Insert(sourceIndex, sourceItem);
                targetByUrl[sourceItem.Url] = sourceItem;
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

    private static void SyncSourceItems<T>(List<ControlItemViewModel> target, IReadOnlyList<T> source, Func<T, ControlItemViewModel> create, Action<ControlItemViewModel, T> update)
        where T : class
    {
        var targetByUrl = target.ToDictionary(item => item.Url, StringComparer.OrdinalIgnoreCase);
        var nextItems = new List<ControlItemViewModel>(source.Count);

        for (var sourceIndex = 0; sourceIndex < source.Count; sourceIndex++)
        {
            var sourceItem = source[sourceIndex];
            var sourceUrl = GetUrl(sourceItem);
            if (!targetByUrl.TryGetValue(sourceUrl, out var existingItem))
            {
                nextItems.Add(create(sourceItem));
                continue;
            }

            update(existingItem, sourceItem);
            nextItems.Add(existingItem);
        }

        target.Clear();
        target.AddRange(nextItems);
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
        return text == AppConfig.Ui.SearchPlaceholder ? string.Empty : text?.Trim() ?? string.Empty;
    }

    private static bool ShouldToast(string message)
    {
        return message.Contains("失败", StringComparison.Ordinal) ||
               message.Contains("已", StringComparison.Ordinal) ||
               message.Contains("没有可控制", StringComparison.Ordinal) ||
               message.Contains("尚未就绪", StringComparison.Ordinal);
    }

    private void ShowToast(string message)
    {
        ToastMessage = message;
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
                return GetModifiersString(_currentRecordingModifiers) + "...";
            }
            return FormatHotkey(_browser.ToggleModeKey, _browser.ToggleModeModifiers);
        }
    }

    public string TogglePlaybackKeyText
    {
        get
        {
            if (_isRecordingTogglePlaybackKey)
            {
                return GetModifiersString(_currentRecordingModifiers) + "...";
            }
            return FormatHotkey(_browser.TogglePlaybackKey, _browser.TogglePlaybackModifiers);
        }
    }

    private void StartRecordingToggleModeKey()
    {
        _isRecordingToggleModeKey = true;
        _isRecordingTogglePlaybackKey = false;
        _currentRecordingModifiers = ModifierKeys.None;
        OnPropertyChanged(nameof(ToggleModeKeyText));
        OnPropertyChanged(nameof(TogglePlaybackKeyText));
        ShowToast("请按下“切换模式”快捷键（按 Esc 取消）");
    }

    private void StartRecordingTogglePlaybackKey()
    {
        _isRecordingTogglePlaybackKey = true;
        _isRecordingToggleModeKey = false;
        _currentRecordingModifiers = ModifierKeys.None;
        OnPropertyChanged(nameof(ToggleModeKeyText));
        OnPropertyChanged(nameof(TogglePlaybackKeyText));
        ShowToast("请按下“视频播放”快捷键（按 Esc 取消）");
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
            ShowToast("已取消录制。");
            return;
        }

        if (_isRecordingToggleModeKey)
        {
            if (key == _browser.TogglePlaybackKey && modifiers == _browser.TogglePlaybackModifiers)
            {
                ShowToast("该快捷键已被视频播放占用！");
            }
            else
            {
                _browser.ToggleModeKey = key;
                _browser.ToggleModeModifiers = modifiers;
                ShowToast($"已将“切换模式”设为 {FormatHotkey(key, modifiers)}");
            }
            _isRecordingToggleModeKey = false;
        }
        else if (_isRecordingTogglePlaybackKey)
        {
            if (key == _browser.ToggleModeKey && modifiers == _browser.ToggleModeModifiers)
            {
                ShowToast("该快捷键已被切换模式占用！");
            }
            else
            {
                _browser.TogglePlaybackKey = key;
                _browser.TogglePlaybackModifiers = modifiers;
                ShowToast($"已将“视频播放”设为 {FormatHotkey(key, modifiers)}");
            }
            _isRecordingTogglePlaybackKey = false;
        }

        _currentRecordingModifiers = ModifierKeys.None;
        OnPropertyChanged(nameof(ToggleModeKeyText));
        OnPropertyChanged(nameof(TogglePlaybackKeyText));
    }

    private static string GetModifiersString(ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (parts.Count == 0) return string.Empty;
        return string.Join(" + ", parts) + " + ";
    }

    private static string FormatHotkey(Key key, ModifierKeys modifiers)
    {
        if (key == Key.None) return "未设置";
        return GetModifiersString(modifiers) + GetKeyName(key);
    }

    private static string GetKeyName(Key key)
    {
        return key switch
        {
            Key.None => "无",
            Key.OemPeriod => ".",
            Key.OemComma => ",",
            Key.OemQuestion => "/",
            Key.OemPlus => "+",
            Key.OemMinus => "-",
            Key.D0 => "0",
            Key.D1 => "1",
            Key.D2 => "2",
            Key.D3 => "3",
            Key.D4 => "4",
            Key.D5 => "5",
            Key.D6 => "6",
            Key.D7 => "7",
            Key.D8 => "8",
            Key.D9 => "9",
            _ => key.ToString()
        };
    }
}
