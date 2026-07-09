using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GenshinBrowser.Constants;
using GenshinBrowser.Services;
using GenshinBrowser.ViewModels;

namespace GenshinBrowser.Windows;

public partial class ControlWindow : Window
{
    private readonly IControlBrowser _browser;
    private readonly Window _browserOwner;
    private readonly ControlWindowViewModel _viewModel;
    private bool _hasUserMovedWindow;
    private bool _isRestoringBounds;
    private System.Windows.Threading.DispatcherTimer? _boundsDebounceTimer;

    private static readonly SolidColorBrush PlaceholderBrush = new(AppConfig.Ui.PlaceholderTextColor);
    private static readonly SolidColorBrush ActiveBrush = new(AppConfig.Ui.ActiveTextColor);

    public ControlWindow(IControlBrowser browser)
    {
        InitializeComponent();
        _browser = browser;
        _browserOwner = browser as Window ?? throw new ArgumentException("Control browser must also be a WPF window.", nameof(browser));
        _viewModel = new ControlWindowViewModel(browser)
        {
            ConfirmClear = () => ThemedMessageBox.ShowYesNo(
                this,
                "确定要清空所有浏览历史吗？此操作不可撤销。",
                "确认清空") == MessageBoxResult.Yes,
        };
        DataContext = _viewModel;
        Owner = _browserOwner;

        Loaded += ControlWindow_OnLoaded;
        LocationChanged += ControlWindow_OnLocationOrSizeChanged;
        SizeChanged += ControlWindow_OnLocationOrSizeChanged;

        AddressBarTextBox.GotFocus += AddressBarTextBox_GotFocus;
        AddressBarTextBox.LostFocus += AddressBarTextBox_LostFocus;
        AddressBarTextBox.Text = AppConfig.Ui.AddressBarPlaceholder;
        AddressBarTextBox.Foreground = PlaceholderBrush;

        FavoriteSearchBox.GotFocus += SearchBox_GotFocus;
        FavoriteSearchBox.LostFocus += SearchBox_LostFocus;
        FavoriteSearchBox.Text = AppConfig.Ui.SearchPlaceholder;
        FavoriteSearchBox.Foreground = PlaceholderBrush;

        HistorySearchBox.GotFocus += SearchBox_GotFocus;
        HistorySearchBox.LostFocus += SearchBox_LostFocus;
        HistorySearchBox.Text = AppConfig.Ui.SearchPlaceholder;
        HistorySearchBox.Foreground = PlaceholderBrush;

        EnableSmoothScrolling(HistoryListBox);
        EnableSmoothScrolling(FavoritesListBox);
    }

    public bool AllowClose { get; set; }

    public void RefreshFromBrowser()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(RefreshFromBrowser);
            return;
        }

        _viewModel.RefreshFromBrowser();
        if (!AddressBarTextBox.IsKeyboardFocusWithin)
        {
            SetAddressText(_viewModel.CurrentAddress);
        }
    }

    public void ShowNearBrowserWindow()
    {
        if (_hasUserMovedWindow)
        {
            return;
        }

        var workArea = WindowBoundsHelper.GetWorkArea(_browserOwner);
        var preferredLeft = _browserOwner.Left + _browserOwner.Width + 12;
        var preferredTop = _browserOwner.Top;

        if (preferredLeft + Width > workArea.Right - 8)
        {
            preferredLeft = _browserOwner.Left - Width - 12;
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

        _browser.SaveControlWindowBounds(Left, Top, Width, Height);
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

        LocationChanged -= ControlWindow_OnLocationOrSizeChanged;
        SizeChanged -= ControlWindow_OnLocationOrSizeChanged;
        AddressBarTextBox.GotFocus -= AddressBarTextBox_GotFocus;
        AddressBarTextBox.LostFocus -= AddressBarTextBox_LostFocus;
        FavoriteSearchBox.GotFocus -= SearchBox_GotFocus;
        FavoriteSearchBox.LostFocus -= SearchBox_LostFocus;
        HistorySearchBox.GotFocus -= SearchBox_GotFocus;
        HistorySearchBox.LostFocus -= SearchBox_LostFocus;
        _boundsDebounceTimer?.Stop();
        if (_boundsDebounceTimer is not null)
        {
            _boundsDebounceTimer.Tick -= BoundsDebounceTimer_OnTick;
        }
        _viewModel.Dispose();

        base.OnClosing(e);
    }

    private void AddressBarTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (AddressBarTextBox.Text == AppConfig.Ui.AddressBarPlaceholder)
        {
            AddressBarTextBox.Text = string.Empty;
            AddressBarTextBox.Foreground = ActiveBrush;
        }
    }

    private void AddressBarTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AddressBarTextBox.Text))
        {
            AddressBarTextBox.Text = AppConfig.Ui.AddressBarPlaceholder;
            AddressBarTextBox.Foreground = PlaceholderBrush;
        }
    }

    private void EnableSmoothScrolling(System.Windows.Controls.ListBox listBox)
    {
        listBox.Loaded += (_, _) =>
        {
            if (VisualTreeHelper.GetChild(listBox, 0) is Border loadedBorder &&
                loadedBorder.Child is ScrollViewer scrollViewer)
            {
                scrollViewer.PreviewMouseWheel -= SmoothScrollViewer_PreviewMouseWheel;
                scrollViewer.PreviewMouseWheel += SmoothScrollViewer_PreviewMouseWheel;
            }
        };
    }

    private void SmoothScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scrollViewer = (ScrollViewer)sender;
        var currentOffset = scrollViewer.VerticalOffset;
        var delta = e.Delta * AppConfig.Ui.SmoothScrollFactor;
        var targetOffset = currentOffset - delta;
        var maxScroll = scrollViewer.ExtentHeight - scrollViewer.ViewportHeight;
        targetOffset = Math.Max(0, Math.Min(maxScroll, targetOffset));

        if ((currentOffset <= 0 && e.Delta > 0) ||
            (currentOffset >= maxScroll && e.Delta < 0))
        {
            e.Handled = false;
            return;
        }

        scrollViewer.BeginAnimation(ScrollViewerBehavior.VerticalOffsetProperty, null);
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
            textBox.Foreground = ActiveBrush;
        }
    }

    private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var textBox = (System.Windows.Controls.TextBox)sender;
        if (string.IsNullOrWhiteSpace(textBox.Text))
        {
            textBox.Text = AppConfig.Ui.SearchPlaceholder;
            textBox.Foreground = PlaceholderBrush;
        }
    }

    private void FavoriteSearchBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.FavoriteSearchText = FavoriteSearchBox.Text;
        }
    }

    private void HistorySearchBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.HistorySearchText = HistorySearchBox.Text;
        }
    }

    private void FavoritesListBox_OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectItemFromRightClick(FavoritesListBox, e) is not ControlItemViewModel item)
        {
            return;
        }

        var menu = new ContextMenu();
        var openItem = new MenuItem { Header = "打开" };
        openItem.Click += (_, _) => _browser.NavigateTo(item.Url);
        menu.Items.Add(openItem);

        var copyItem = new MenuItem { Header = "复制链接" };
        copyItem.Click += (_, _) => System.Windows.Clipboard.SetText(item.Url);
        menu.Items.Add(copyItem);
        menu.Items.Add(new Separator());

        var deleteItem = new MenuItem { Header = "删除收藏" };
        deleteItem.Click += async (_, _) => await _browser.RemoveFavoriteAsync(item.Url);
        menu.Items.Add(deleteItem);
        menu.IsOpen = true;
    }

    private void HistoryListBox_OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectItemFromRightClick(HistoryListBox, e) is not ControlItemViewModel item)
        {
            return;
        }

        var menu = new ContextMenu();
        var openItem = new MenuItem { Header = "打开" };
        openItem.Click += (_, _) => _browser.NavigateTo(item.Url);
        menu.Items.Add(openItem);

        var copyItem = new MenuItem { Header = "复制链接" };
        copyItem.Click += (_, _) => System.Windows.Clipboard.SetText(item.Url);
        menu.Items.Add(copyItem);
        menu.Items.Add(new Separator());

        var deleteItem = new MenuItem { Header = "从历史中移除" };
        deleteItem.Click += async (_, _) => await _browser.RemoveHistoryEntryAsync(item.Url);
        menu.Items.Add(deleteItem);
        menu.IsOpen = true;
    }

    private static object? SelectItemFromRightClick(System.Windows.Controls.ListBox listBox, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null && element is not ListBoxItem)
        {
            element = VisualTreeHelper.GetParent(element);
        }

        if (element is not ListBoxItem listBoxItem)
        {
            return null;
        }

        listBoxItem.IsSelected = true;
        e.Handled = true;
        return listBox.ItemContainerGenerator.ItemFromContainer(listBoxItem);
    }

    private void HistoryListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HistoryListBox.SelectedItem is ControlItemViewModel item)
        {
            _browser.NavigateTo(item.Url);
        }
    }

    private void FavoritesListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FavoritesListBox.SelectedItem is ControlItemViewModel item)
        {
            _browser.NavigateTo(item.Url);
        }
    }

    private void ControlWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        _isRestoringBounds = true;
        var restoredPosition = _browser.RestoreControlWindowBounds(this);
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
        _boundsDebounceTimer ??= CreateBoundsDebounceTimer();
        _boundsDebounceTimer.Stop();
        _boundsDebounceTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer CreateBoundsDebounceTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        timer.Tick += BoundsDebounceTimer_OnTick;
        return timer;
    }

    private void BoundsDebounceTimer_OnTick(object? sender, EventArgs e)
    {
        _boundsDebounceTimer?.Stop();
        SaveWindowBounds();
    }

    private void SetAddressText(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            AddressBarTextBox.Text = AppConfig.Ui.AddressBarPlaceholder;
            AddressBarTextBox.Foreground = PlaceholderBrush;
            return;
        }

        AddressBarTextBox.Text = address;
        AddressBarTextBox.Foreground = ActiveBrush;
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (_viewModel.IsRecordingAnyKey)
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            
            if (key == Key.LeftCtrl || key == Key.RightCtrl ||
                key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift ||
                key == Key.LWin || key == Key.RWin)
            {
                var modifiers = Keyboard.Modifiers;
                _viewModel.UpdateRecordingModifiers(modifiers);
                return;
            }
            
            var finalModifiers = Keyboard.Modifiers;
            _viewModel.FinishRecordingKey(key, finalModifiers);
        }
        else
        {
            base.OnPreviewKeyDown(e);
        }
    }

    protected override void OnPreviewKeyUp(System.Windows.Input.KeyEventArgs e)
    {
        if (_viewModel.IsRecordingAnyKey)
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.LeftCtrl || key == Key.RightCtrl ||
                key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift ||
                key == Key.LWin || key == Key.RWin)
            {
                var modifiers = Keyboard.Modifiers;
                _viewModel.UpdateRecordingModifiers(modifiers);
            }
        }
        else
        {
            base.OnPreviewKeyUp(e);
        }
    }

    private void SettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
    }
}
