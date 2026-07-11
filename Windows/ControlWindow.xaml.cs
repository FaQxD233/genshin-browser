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

    public ControlWindow(IControlBrowser browser)
    {
        InitializeComponent();
        _browser = browser;
        _browserOwner = browser as Window ?? throw new ArgumentException("Control browser must also be a WPF window.", nameof(browser));
        _viewModel = new ControlWindowViewModel(browser);
        DataContext = _viewModel;
        Owner = _browserOwner;

        Loaded += ControlWindow_OnLoaded;
        LocationChanged += ControlWindow_OnLocationOrSizeChanged;
        SizeChanged += ControlWindow_OnLocationOrSizeChanged;

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
        _boundsDebounceTimer?.Stop();
        if (_boundsDebounceTimer is not null)
        {
            _boundsDebounceTimer.Tick -= BoundsDebounceTimer_OnTick;
        }
        _viewModel.Dispose();

        base.OnClosing(e);
    }

    private void EnableSmoothScrolling(System.Windows.Controls.ListBox listBox)
    {
        listBox.Loaded += (_, _) => TryAttachSmoothScroll(listBox);
        listBox.IsVisibleChanged += (_, _) => TryAttachSmoothScroll(listBox);
    }

    private void TryAttachSmoothScroll(System.Windows.Controls.ListBox listBox)
    {
        if (!listBox.IsVisible || VisualTreeHelper.GetChildrenCount(listBox) == 0)
        {
            return;
        }

        if (VisualTreeHelper.GetChild(listBox, 0) is Border loadedBorder &&
            loadedBorder.Child is ScrollViewer scrollViewer)
        {
            scrollViewer.PreviewMouseWheel -= SmoothScrollViewer_PreviewMouseWheel;
            scrollViewer.PreviewMouseWheel += SmoothScrollViewer_PreviewMouseWheel;
        }
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

    private void FavoritesListBox_OnRightClick(object sender, MouseButtonEventArgs e)
    {
        ShowListItemContextMenu(FavoritesListBox, e, LocalizationService.Get("Context.RemoveFavorite", "删除收藏"), item => _viewModel.RemoveFavoriteCommand.Execute(item));
    }

    private void HistoryListBox_OnRightClick(object sender, MouseButtonEventArgs e)
    {
        ShowListItemContextMenu(HistoryListBox, e, LocalizationService.Get("Context.RemoveHistory", "从历史中移除"), item => _viewModel.RemoveHistoryCommand.Execute(item));
    }

    private void ShowListItemContextMenu(
        System.Windows.Controls.ListBox listBox,
        MouseButtonEventArgs e,
        string deleteHeader,
        Action<ControlItemViewModel> deleteAction)
    {
        if (SelectItemFromRightClick(listBox, e) is not ControlItemViewModel item)
        {
            return;
        }

        var menu = new ContextMenu();
        var openItem = new MenuItem { Header = LocalizationService.Get("Context.Open", "打开") };
        openItem.Click += (_, _) => _viewModel.OpenItemCommand.Execute(item);
        menu.Items.Add(openItem);

        var copyItem = new MenuItem { Header = LocalizationService.Get("Context.CopyLink", "复制链接") };
        copyItem.Click += (_, _) => System.Windows.Clipboard.SetText(item.Url);
        menu.Items.Add(copyItem);
        menu.Items.Add(new Separator());

        var deleteItem = new MenuItem { Header = deleteHeader };
        deleteItem.Click += (_, _) => deleteAction(item);
        menu.Items.Add(deleteItem);
        menu.IsOpen = true;
    }

    private void ListBox_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox listBox ||
            listBox.SelectedItem is not ControlItemViewModel item)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            _viewModel.OpenItemCommand.Execute(item);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete)
        {
            if (ReferenceEquals(listBox, FavoritesListBox))
            {
                _viewModel.RemoveFavoriteCommand.Execute(item);
            }
            else if (ReferenceEquals(listBox, HistoryListBox))
            {
                _viewModel.RemoveHistoryCommand.Execute(item);
            }

            e.Handled = true;
        }
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
            _viewModel.OpenItemCommand.Execute(item);
        }
    }

    private void FavoritesListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FavoritesListBox.SelectedItem is ControlItemViewModel item)
        {
            _viewModel.OpenItemCommand.Execute(item);
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

    /// <summary>
    /// 仅设置地址栏文本，占位符由 <see cref="Services.Placeholder"/> 附加属性通过 Adorner 渲染。
    /// 当地址为空时，文本被清空，Adorner 会自动显示提示文字。
    /// </summary>
    private void SetAddressText(string address)
    {
        AddressBarTextBox.Text = string.IsNullOrWhiteSpace(address) ? string.Empty : address;
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
            return;
        }

        // Ctrl+L 聚焦地址栏并全选
        if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            AddressBarTextBox.Focus();
            AddressBarTextBox.SelectAll();
            return;
        }

        base.OnPreviewKeyDown(e);
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
}
