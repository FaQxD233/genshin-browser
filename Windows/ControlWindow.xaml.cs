using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GenshinBrowser.Constants;
using GenshinBrowser.Models;
using GenshinBrowser.Services;
using GenshinBrowser.Utils;

namespace GenshinBrowser.Windows;

public partial class ControlWindow : Window
{
    private readonly MainWindow _browserWindow;
    private readonly ObservableCollection<HistoryItemViewModel> _historyItems = new();
    private readonly ObservableCollection<FavoriteItemViewModel> _favoriteItems = new();
    private readonly List<HistoryItemViewModel> _allHistoryItems = new();
    private readonly List<FavoriteItemViewModel> _allFavoriteItems = new();
    private bool _hasUserMovedWindow;
    private bool _isRestoringBounds;

    public ControlWindow(MainWindow browserWindow)
    {
        InitializeComponent();
        _browserWindow = browserWindow;
        Owner = browserWindow;
        HistoryListBox.ItemsSource = _historyItems;
        FavoritesListBox.ItemsSource = _favoriteItems;

        Loaded += ControlWindow_OnLoaded;
        LocationChanged += ControlWindow_OnLocationOrSizeChanged;
        SizeChanged += ControlWindow_OnLocationOrSizeChanged;

        // 地址栏占位符逻辑
        AddressBarTextBox.GotFocus += AddressBarTextBox_GotFocus;
        AddressBarTextBox.LostFocus += AddressBarTextBox_LostFocus;
        AddressBarTextBox.TextChanged += AddressBarTextBox_TextChanged;
        AddressBarTextBox.Text = AppConfig.Ui.AddressBarPlaceholder;
        AddressBarTextBox.Foreground = new SolidColorBrush(AppConfig.Ui.PlaceholderTextColor);

        // 搜索框占位符
        FavoriteSearchBox.GotFocus += SearchBox_GotFocus;
        FavoriteSearchBox.LostFocus += SearchBox_LostFocus;
        FavoriteSearchBox.Text = AppConfig.Ui.SearchPlaceholder;
        FavoriteSearchBox.Foreground = new SolidColorBrush(AppConfig.Ui.PlaceholderTextColor);

        HistorySearchBox.GotFocus += SearchBox_GotFocus;
        HistorySearchBox.LostFocus += SearchBox_LostFocus;
        HistorySearchBox.Text = AppConfig.Ui.SearchPlaceholder;
        HistorySearchBox.Foreground = new SolidColorBrush(AppConfig.Ui.PlaceholderTextColor);

        // 启用平滑滚动
        EnableSmoothScrolling(HistoryListBox);
        EnableSmoothScrolling(FavoritesListBox);
    }

    public bool AllowClose { get; set; }

    public void RefreshFromBrowser()
    {
        ModeTextBlock.Text = _browserWindow.CurrentMode == WindowMode.Fixed ? "📌 固定模式" : "🔓 自由模式";
        ToggleModeButton.Content = _browserWindow.CurrentMode == WindowMode.Fixed ? "🔓" : "📌";
        ToggleModeButton.ToolTip = _browserWindow.CurrentMode == WindowMode.Fixed ? "解锁窗口" : "固定窗口";
        StatusTextBlock.Text = _browserWindow.StatusMessage;

        var isFavorite = _browserWindow.IsFavorite(_browserWindow.CurrentAddress);
        FavoriteToggleButton.Content = isFavorite ? "⭐ 已收藏" : "☆ 收藏当前页";
        FavoriteToggleButton.Style = isFavorite
            ? (Style)FindResource("ModernButton")
            : (Style)FindResource("PrimaryButton");

        // 更新地址栏（保护占位符逻辑）
        if (!AddressBarTextBox.IsKeyboardFocusWithin)
        {
            if (string.IsNullOrWhiteSpace(_browserWindow.CurrentAddress))
            {
                AddressBarTextBox.Text = AppConfig.Ui.AddressBarPlaceholder;
                AddressBarTextBox.Foreground = new SolidColorBrush(AppConfig.Ui.PlaceholderTextColor);
            }
            else
            {
                AddressBarTextBox.Text = _browserWindow.CurrentAddress;
                AddressBarTextBox.Foreground = new SolidColorBrush(AppConfig.Ui.ActiveTextColor);
            }
        }

        // 刷新收藏夹
        _allFavoriteItems.Clear();
        foreach (var item in _browserWindow.FavoriteEntries)
        {
            _allFavoriteItems.Add(new FavoriteItemViewModel(item));
        }
        FilterFavorites(FavoriteSearchBox.Text);

        // 刷新历史记录
        _allHistoryItems.Clear();
        foreach (var item in _browserWindow.HistoryEntries)
        {
            _allHistoryItems.Add(new HistoryItemViewModel(item));
        }
        FilterHistory(HistorySearchBox.Text);
    }

    public void ShowNearBrowserWindow()
    {
        if (_hasUserMovedWindow)
        {
            return;
        }

        var workArea = WindowBoundsHelper.GetWorkArea(_browserWindow);
        var preferredLeft = _browserWindow.Left + _browserWindow.Width + 12;
        var preferredTop = _browserWindow.Top;

        if (preferredLeft + Width > workArea.Right - 8)
        {
            preferredLeft = _browserWindow.Left - Width - 12;
        }

        if (preferredLeft < workArea.Left + 8)
        {
            preferredLeft = workArea.Left + 8;
        }

        var maxTop = Math.Max(workArea.Top + 8, workArea.Bottom - Height - 8);
        if (preferredTop > maxTop)
        {
            preferredTop = maxTop;
        }

        if (preferredTop < workArea.Top + 8)
        {
            preferredTop = workArea.Top + 8;
        }

        _isRestoringBounds = true;
        try
        {
            Left = preferredLeft;
            Top = preferredTop;
            WindowBoundsHelper.ClampToWorkArea(this, workArea);
        }
        finally
        {
            _isRestoringBounds = false;
        }
    }

    public void SaveWindowBounds()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        _browserWindow.SaveControlWindowBounds(Left, Top, Width, Height);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            SaveWindowBounds();
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    private void ToggleModeButton_OnClick(object sender, RoutedEventArgs e)
    {
        _browserWindow.ToggleWindowMode();
    }

    private async void PlayPauseButton_OnClick(object sender, RoutedEventArgs e)
    {
        await _browserWindow.ToggleVideoPlaybackAsync();
    }

    private void HomeButton_OnClick(object sender, RoutedEventArgs e)
    {
        _browserWindow.NavigateHome();
    }

    private void GoButton_OnClick(object sender, RoutedEventArgs e)
    {
        var input = AddressBarTextBox.Text;
        if (input == AppConfig.Ui.AddressBarPlaceholder)
        {
            return;
        }

        _browserWindow.NavigateTo(input);
    }

    private void ReloadButton_OnClick(object sender, RoutedEventArgs e)
    {
        _browserWindow.ReloadPage();
    }

    private void AddressBarTextBox_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var input = AddressBarTextBox.Text;
            if (input == AppConfig.Ui.AddressBarPlaceholder)
            {
                return;
            }

            _browserWindow.NavigateTo(input);
        }
    }

    private void AddressBarTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (AddressBarTextBox.Text == AppConfig.Ui.AddressBarPlaceholder)
        {
            AddressBarTextBox.Text = string.Empty;
            AddressBarTextBox.Foreground = new SolidColorBrush(AppConfig.Ui.ActiveTextColor);
        }
    }

    private void AddressBarTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AddressBarTextBox.Text))
        {
            AddressBarTextBox.Text = AppConfig.Ui.AddressBarPlaceholder;
            AddressBarTextBox.Foreground = new SolidColorBrush(AppConfig.Ui.PlaceholderTextColor);
        }
    }

    private void AddressBarTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 用户手动输入时，导航到新地址
        if (AddressBarTextBox.IsKeyboardFocusWithin &&
            !string.IsNullOrWhiteSpace(AddressBarTextBox.Text) &&
            AddressBarTextBox.Text != AppConfig.Ui.AddressBarPlaceholder)
        {
            // 这里不做实时导航，等用户按 Enter 或点击打开按钮
        }
    }

    private void EnableSmoothScrolling(System.Windows.Controls.ListBox listBox)
    {
        if (listBox.Template?.FindName("Border", listBox) is not Border border)
        {
            listBox.Loaded += (s, e) =>
            {
                if (VisualTreeHelper.GetChild(listBox, 0) is Border loadedBorder &&
                    loadedBorder.Child is ScrollViewer scrollViewer)
                {
                    scrollViewer.PreviewMouseWheel += SmoothScrollViewer_PreviewMouseWheel;
                }
            };
        }
    }

    private void SmoothScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scrollViewer = (ScrollViewer)sender;

        // 计算目标位置
        var currentOffset = scrollViewer.VerticalOffset;
        var delta = e.Delta * AppConfig.Ui.SmoothScrollFactor;
        var targetOffset = currentOffset - delta;

        // 限制在有效范围内
        var maxScroll = scrollViewer.ExtentHeight - scrollViewer.ViewportHeight;
        targetOffset = Math.Max(0, Math.Min(maxScroll, targetOffset));

        // 如果已经到边界，使用默认滚动行为
        if ((currentOffset <= 0 && e.Delta > 0) ||
            (currentOffset >= maxScroll && e.Delta < 0))
        {
            e.Handled = false;  // 让系统处理边界滚动
            return;
        }

        // 先停止正在运行的动画，避免并发
        scrollViewer.BeginAnimation(ScrollViewerBehavior.VerticalOffsetProperty, null);

        // 使用动画平滑滚动
        var animation = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = currentOffset,
            To = targetOffset,
            Duration = TimeSpan.FromMilliseconds(AppConfig.Ui.SmoothScrollDurationMs),
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };

        scrollViewer.BeginAnimation(ScrollViewerBehavior.VerticalOffsetProperty, animation);
        e.Handled = true;
    }

    private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        var textBox = (System.Windows.Controls.TextBox)sender;
        if (textBox.Text == AppConfig.Ui.SearchPlaceholder)
        {
            textBox.Text = string.Empty;
            textBox.Foreground = new SolidColorBrush(AppConfig.Ui.ActiveTextColor);
        }
    }

    private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var textBox = (System.Windows.Controls.TextBox)sender;
        if (string.IsNullOrWhiteSpace(textBox.Text))
        {
            textBox.Text = AppConfig.Ui.SearchPlaceholder;
            textBox.Foreground = new SolidColorBrush(AppConfig.Ui.PlaceholderTextColor);
        }
    }

    private void FavoriteSearchBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        FilterFavorites(FavoriteSearchBox.Text);
    }

    private void HistorySearchBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        FilterHistory(HistorySearchBox.Text);
    }

    private void FilterFavorites(string searchText)
    {
        var shouldShowAll = string.IsNullOrWhiteSpace(searchText) ||
                           searchText == AppConfig.Ui.SearchPlaceholder;

        if (shouldShowAll)
        {
            // 显示全部：增量同步
            SyncObservableCollection(_favoriteItems, _allFavoriteItems);
        }
        else
        {
            // 过滤搜索：增量同步
            var filtered = _allFavoriteItems.Where(item =>
                item.Title.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                item.Url.Contains(searchText, StringComparison.OrdinalIgnoreCase)).ToList();

            SyncObservableCollection(_favoriteItems, filtered);
        }
    }

    private void FilterHistory(string searchText)
    {
        var shouldShowAll = string.IsNullOrWhiteSpace(searchText) ||
                           searchText == AppConfig.Ui.SearchPlaceholder;

        if (shouldShowAll)
        {
            // 显示全部：增量同步
            SyncObservableCollection(_historyItems, _allHistoryItems);
        }
        else
        {
            // 过滤搜索：增量同步
            var filtered = _allHistoryItems.Where(item =>
                item.Title.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                item.Url.Contains(searchText, StringComparison.OrdinalIgnoreCase)).ToList();

            SyncObservableCollection(_historyItems, filtered);
        }
    }

    private void SyncObservableCollection<T>(ObservableCollection<T> target, IList<T> source)
    {
        // 移除不在源列表中的项
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!source.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        // 添加源列表中的新项
        foreach (var item in source)
        {
            if (!target.Contains(item))
            {
                target.Add(item);
            }
        }
    }

    private async void ClearHistoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "确定要清空所有浏览历史吗？此操作不可撤销。",
            "确认清空",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            await _browserWindow.ClearHistoryAsync();
        }
    }

    private void FavoritesListBox_OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (FavoritesListBox.SelectedItem is not FavoriteItemViewModel item)
        {
            return;
        }

        var menu = new ContextMenu();

        var openItem = new MenuItem { Header = "打开" };
        openItem.Click += (_, _) => _browserWindow.NavigateTo(item.Url);
        menu.Items.Add(openItem);

        var copyItem = new MenuItem { Header = "复制链接" };
        copyItem.Click += (_, _) => System.Windows.Clipboard.SetText(item.Url);
        menu.Items.Add(copyItem);

        menu.Items.Add(new Separator());

        var deleteItem = new MenuItem { Header = "删除收藏" };
        deleteItem.Click += async (_, _) => await _browserWindow.RemoveFavoriteAsync(item.Url);
        menu.Items.Add(deleteItem);

        menu.IsOpen = true;
    }

    private void HistoryListBox_OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (HistoryListBox.SelectedItem is not HistoryItemViewModel item)
        {
            return;
        }

        var menu = new ContextMenu();

        var openItem = new MenuItem { Header = "打开" };
        openItem.Click += (_, _) => _browserWindow.NavigateTo(item.Url);
        menu.Items.Add(openItem);

        var copyItem = new MenuItem { Header = "复制链接" };
        copyItem.Click += (_, _) => System.Windows.Clipboard.SetText(item.Url);
        menu.Items.Add(copyItem);

        menu.Items.Add(new Separator());

        var deleteItem = new MenuItem { Header = "从历史中移除" };
        deleteItem.Click += async (_, _) => await _browserWindow.RemoveHistoryEntryAsync(item.Url);
        menu.Items.Add(deleteItem);

        menu.IsOpen = true;
    }

    private void HistoryListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HistoryListBox.SelectedItem is HistoryItemViewModel item)
        {
            _browserWindow.NavigateTo(item.Url);
        }
    }

    private async void FavoriteToggleButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_browserWindow.IsFavorite(_browserWindow.CurrentAddress))
        {
            await _browserWindow.RemoveFavoriteAsync(_browserWindow.CurrentAddress);
            return;
        }

        await _browserWindow.AddCurrentPageToFavoritesAsync();
    }

    private void FavoritesListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FavoritesListBox.SelectedItem is FavoriteItemViewModel item)
        {
            _browserWindow.NavigateTo(item.Url);
        }
    }

    private void ControlWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        _isRestoringBounds = true;
        var restoredPosition = _browserWindow.RestoreControlWindowBounds(this);
        _hasUserMovedWindow = restoredPosition;
        _isRestoringBounds = false;

        if (!restoredPosition)
        {
            ShowNearBrowserWindow();
        }
    }

    private void ControlWindow_OnLocationOrSizeChanged(object? sender, EventArgs e)
    {
        if (!IsLoaded || WindowState != WindowState.Normal || _isRestoringBounds)
        {
            return;
        }

        _hasUserMovedWindow = true;
        SaveWindowBounds();
    }

    private sealed class HistoryItemViewModel
    {
        public HistoryItemViewModel(HistoryEntry item)
        {
            Url = item.Url;
            Title = item.Title;
            TimeDisplay = TimeFormatter.FormatRelativeTime(item.VisitedAt);
        }

        public string Url { get; }
        public string Title { get; }
        public string TimeDisplay { get; }
    }

    private sealed class FavoriteItemViewModel
    {
        public FavoriteItemViewModel(FavoriteEntry item)
        {
            Url = item.Url;
            Title = item.Title;
            TimeDisplay = TimeFormatter.FormatRelativeTime(item.SavedAt);
        }

        public string Url { get; }
        public string Title { get; }
        public string TimeDisplay { get; }
    }
}
