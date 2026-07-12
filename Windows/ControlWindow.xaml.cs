using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GenshinBrowser.Constants;
using GenshinBrowser.Models;
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

        // 地址栏不走双向绑定（避免与用户输入打架），跟随 ViewModel.CurrentAddress 推送；
        // 用户正在编辑时跳过，行为与常见浏览器一致。
        _viewModel.PropertyChanged += ViewModel_OnPropertyChanged;
        SyncAddressBarFromViewModel(force: true);

        Loaded += ControlWindow_OnLoaded;
        LocationChanged += ControlWindow_OnLocationOrSizeChanged;
        SizeChanged += ControlWindow_OnLocationOrSizeChanged;
        // 点击空白区域时让输入框失焦（WPF 默认点到 Panel/Border 不会挪走键盘焦点）
        PreviewMouseDown += ControlWindow_OnPreviewMouseDown;

        EnableSmoothScrolling(HistoryListBox);
        EnableSmoothScrolling(FavoritesListBox);
    }

    public bool AllowClose { get; set; }

    public void RefreshFromBrowser()
    {
        RefreshFromBrowser(BrowserStateChangeKind.All);
    }

    public void RefreshFromBrowser(BrowserStateChangeKind kind)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => RefreshFromBrowser(kind));
            return;
        }

        _viewModel.RefreshFromBrowser(kind);

        // CurrentAddress 变更也会经 PropertyChanged → SyncAddressBarFromViewModel；
        // 这里在 Navigation 时再兜底一次，覆盖「同 URL 仅加载态变化」等未触发属性变更的情况。
        if (kind.HasFlag(BrowserStateChangeKind.Navigation))
        {
            SyncAddressBarFromViewModel();
        }

        if (kind.HasFlag(BrowserStateChangeKind.Appearance))
        {
            RefreshWindowSizeDisplay();
        }
    }

    /// <summary>
    /// 用主窗当前尺寸更新设置面板宽高显示；用户正在编辑输入框时跳过。
    /// </summary>
    public void RefreshWindowSizeDisplay()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(RefreshWindowSizeDisplay);
            return;
        }

        if (BrowserWindowWidthBox.IsKeyboardFocusWithin || BrowserWindowHeightBox.IsKeyboardFocusWithin)
        {
            return;
        }

        _viewModel.SyncWindowSizeTexts();
    }

    private void OpacityPercentBox_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_viewModel.ApplyOpacityCommand.CanExecute(null))
        {
            _viewModel.ApplyOpacityCommand.Execute(null);
        }
    }

    private void ZoomPercentBox_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_viewModel.ApplyZoomCommand.CanExecute(null))
        {
            _viewModel.ApplyZoomCommand.Execute(null);
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
        PreviewMouseDown -= ControlWindow_OnPreviewMouseDown;
        _viewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
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

    private void ControlWindow_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 仅当键盘焦点在可编辑输入上时才处理
        if (Keyboard.FocusedElement is not DependencyObject focused || !IsEditableInput(focused))
        {
            return;
        }

        if (e.OriginalSource is not DependencyObject clicked)
        {
            return;
        }

        // 点在当前输入控件内部：保持焦点
        if (IsDescendantOf(clicked, focused))
        {
            return;
        }

        // 点在另一个可编辑输入上：交给系统自然切换
        if (FindEditableInputAncestor(clicked) is not null)
        {
            return;
        }

        // 点空白 / 标签 / 面板等非编辑区域：清除键盘焦点。
        // 在 Preview 阶段先失焦，便于设置框 LostKeyboardFocus 提交数值后再响应按钮 Click。
        ClearInputFocus();
    }

    /// <summary>
    /// 将键盘焦点从输入框移到窗口本身，触发 LostKeyboardFocus（提交设置数值、恢复地址栏同步等）。
    /// </summary>
    private void ClearInputFocus()
    {
        try
        {
            FocusManager.SetFocusedElement(this, this);
            Keyboard.Focus(this);
        }
        catch
        {
            // 焦点操作失败时仍尝试清空，避免输入框“粘住”
            Keyboard.ClearFocus();
        }
    }

    private static bool IsEditableInput(DependencyObject element)
    {
        return element is System.Windows.Controls.Primitives.TextBoxBase
            or System.Windows.Controls.PasswordBox
            or System.Windows.Controls.ComboBox;
    }

    private static DependencyObject? FindEditableInputAncestor(DependencyObject? node)
    {
        while (node is not null)
        {
            if (IsEditableInput(node))
            {
                return node;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor))
            {
                return true;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ControlWindowViewModel.CurrentAddress))
        {
            SyncAddressBarFromViewModel();
        }
    }

    /// <summary>
    /// 将 ViewModel 中的当前页 URL 推到地址栏，模拟普通浏览器地址栏随导航更新。
    /// 用户正在地址栏输入时不覆盖（除非 <paramref name="force"/>）。
    /// </summary>
    private void SyncAddressBarFromViewModel(bool force = false)
    {
        if (!force && AddressBarTextBox.IsKeyboardFocusWithin)
        {
            return;
        }

        SetAddressText(_viewModel.CurrentAddress);
    }

    /// <summary>
    /// 仅设置地址栏文本，占位符由 <see cref="Services.Placeholder"/> 附加属性通过 Adorner 渲染。
    /// 当地址为空时，文本被清空，Adorner 会自动显示提示文字。
    /// </summary>
    private void SetAddressText(string address)
    {
        var text = string.IsNullOrWhiteSpace(address) ? string.Empty : address;
        if (string.Equals(AddressBarTextBox.Text, text, StringComparison.Ordinal))
        {
            return;
        }

        AddressBarTextBox.Text = text;
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
