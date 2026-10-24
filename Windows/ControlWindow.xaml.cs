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
    // 程序回填地址栏（打开联想候选后写入其网址）时不重新触发联想
    private bool _suppressAddressSuggestions;
    private System.Windows.Threading.DispatcherTimer? _boundsDebounceTimer;
    // 滚轮平滑滚动的缓动函数可复用（无状态）；DoubleAnimation 必须每次实例化：
    // WPF 动画时钟会跟随未冻结 Timeline 的属性变更，共享实例会让一个列表仍在
    // 进行的滚动动画被另一个列表的目标位置劫持。
    private static readonly System.Windows.Media.Animation.EasingFunctionBase SmoothScrollEasing =
        new System.Windows.Media.Animation.CubicEase
        {
            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
        };

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
        SourceInitialized += ControlWindow_OnSourceInitialized;
        LocationChanged += ControlWindow_OnLocationOrSizeChanged;
        SizeChanged += ControlWindow_OnLocationOrSizeChanged;
        // 点击空白区域时让输入框失焦（WPF 默认点到 Panel/Border 不会挪走键盘焦点）
        PreviewMouseDown += ControlWindow_OnPreviewMouseDown;
        ThemeService.ThemeChanged += ThemeService_OnThemeChanged;

        EnableSmoothScrolling(HistoryListBox);
        EnableSmoothScrolling(FavoritesListBox);
    }

    public bool AllowClose { get; set; }

    /// <summary>
    /// 取消进行中的热键录制并恢复钩子。程序化隐藏控制窗前调用——
    /// Hide() 不经过 OnClosing 的取消逻辑，否则 SuspendBuiltInHotkeys 会残留。
    /// </summary>
    public void CancelHotkeyRecordingIfActive()
    {
        _viewModel.CancelActiveRecording();
    }

    private void ControlWindow_OnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyNativeTitleBarTheme();
    }

    private void ThemeService_OnThemeChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(ApplyNativeTitleBarTheme);
            return;
        }

        ApplyNativeTitleBarTheme();
    }

    private void ApplyNativeTitleBarTheme()
    {
        NativeTitleBarService.Apply(
            this,
            string.Equals(ThemeService.Current, ThemeService.Dark, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>设置面板右上角「管理配置」：在按钮下方右对齐弹出菜单（导入、导出配置、恢复默认）。</summary>
    private void ManageConfigButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ManageConfigButton.ContextMenu is not { } menu)
        {
            return;
        }

        menu.PlacementTarget = ManageConfigButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = static (popupSize, targetSize, _) =>
        [
            new System.Windows.Controls.Primitives.CustomPopupPlacement(
                new Point(targetSize.Width - popupSize.Width, targetSize.Height + 4),
                System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal),
        ];
        menu.IsOpen = true;
    }

    private void RestoreDefaultsMenuItem_OnClick(object sender, RoutedEventArgs e) =>
        _viewModel.RestoreDefaultSettingsCommand.Execute(null);

    private async void ExportConfigMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = LocalizationService.Get("Config.ExportDialogTitle"),
            Filter = LocalizationService.Get("Config.FileFilter"),
            FileName = ConfigBackupService.SuggestFileName(DateTime.Now),
            DefaultExt = ".json",
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.ExportConfigAsync(dialog.FileName);
        }
    }

    /// <summary>
    /// 导入配置：选文件 → 校验 → 有冲突时依次在窗内询问（任一步取消则什么都不改）：
    /// 现有收藏 / 浏览记录里有文件里没有的网址时问合并还是替换；窗口大小 / 位置与现在不同时问是否恢复 → 导入。
    /// </summary>
    private async void ImportConfigMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = LocalizationService.Get("Config.ImportDialogTitle"),
            Filter = LocalizationService.Get("Config.FileFilter"),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var bundle = await _viewModel.ReadConfigAsync(dialog.FileName);
        if (bundle is null)
        {
            return;
        }

        var mergeLists = true;
        if (_viewModel.ConfigWouldReplaceEntries(bundle))
        {
            var choice = await AskAsync(
                LocalizationService.Get("ImportLists.Title"),
                LocalizationService.Get("ImportLists.Body"),
                [LocalizationService.Get("ImportLists.Merge"), LocalizationService.Get("ImportLists.Replace")]);
            if (choice is null)
            {
                return;
            }

            mergeLists = choice == 0;
        }

        var applyWindowBounds = true;
        if (_viewModel.CompareImportedWindowBounds(bundle) is { HasDifference: true } windows)
        {
            var offScreen = windows.Main.ImportedPositionOffScreen || windows.Control.ImportedPositionOffScreen;
            var choice = await AskAsync(
                LocalizationService.Get("ImportWindows.Title"),
                LocalizationService.Get(offScreen ? "ImportWindows.BodyOffScreen" : "ImportWindows.Body"),
                [LocalizationService.Get("ImportWindows.Restore"), LocalizationService.Get("ImportWindows.KeepCurrent")]);
            if (choice is null)
            {
                return;
            }

            applyWindowBounds = choice == 0;
        }

        await _viewModel.ImportConfigAsync(bundle, mergeLists, applyWindowBounds);
    }

    /// <summary>
    /// 「管理配置 → 数据目录」：卡片显示当前位置，可打开文件夹或更改位置。新位置已有本程序数据则切换过去，没有则可选搬过去
    /// 或从空白开始；确认后记下设置（下次启动生效），再问是否立即重启——浏览器数据目录在运行中被占用，只能重启后切换。
    /// </summary>
    private async void DataDirectoryMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var current = AppPaths.DataRoot;
        var action = await AskAsync(
            LocalizationService.Get("DataDir.Title"),
            AppPaths.IsDefault(current) ? LocalizationService.Format("DataDir.BodyDefault", current) : current,
            [LocalizationService.Get("DataDir.Change"), LocalizationService.Get("DataDir.Open")],
            LocalizationService.Get("DataDir.Close"));
        if (action == 1)
        {
            OpenFolder(current);
            return;
        }

        if (action != 0)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = LocalizationService.Get("DataDir.PickTitle"),
            InitialDirectory = System.IO.Path.GetDirectoryName(current) ?? current,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var target = dialog.FolderName;
        var problem = AppPaths.SamePath(target, current) ? "Status.DataDirSame"
            : AppPaths.AreNested(target, current) ? "Status.DataDirNested"
            : !AppPaths.IsUsable(target) ? "Status.DataDirNotWritable"
            : null;
        if (problem is not null)
        {
            _viewModel.ShowNotice(LocalizationService.Get(problem), StatusLevel.Warning);
            return;
        }

        string? moveFrom = null;
        if (AppPaths.HasAppData(target))
        {
            var confirmed = await AskAsync(
                LocalizationService.Get("DataDir.SwitchTitle"),
                LocalizationService.Get("DataDir.SwitchBody"),
                [LocalizationService.Get("DataDir.Switch")]);
            if (confirmed is null)
            {
                return;
            }
        }
        else
        {
            var move = await AskAsync(
                LocalizationService.Get("DataDir.MoveTitle"),
                LocalizationService.Get("DataDir.MoveBody"),
                [LocalizationService.Get("DataDir.Move"), LocalizationService.Get("DataDir.StartFresh")]);
            if (move is null)
            {
                return;
            }

            moveFrom = move == 0 ? current : null;
        }

        try
        {
            AppPaths.SaveDataDirectory(target, moveFrom);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            FileLogger.LogException(ex, "Save data directory");
            _viewModel.ShowNotice(LocalizationService.Format("Status.DataDirSaveFailed", ex.Message), StatusLevel.Error);
            return;
        }

        var restart = await AskAsync(
            LocalizationService.Get("DataDir.RestartTitle"),
            LocalizationService.Format("DataDir.RestartBody", target),
            [LocalizationService.Get("DataDir.RestartNow")],
            LocalizationService.Get("DataDir.Later"));
        if (restart == 0)
        {
            _browser.RestartApplication();
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            FileLogger.LogException(ex, "Open data directory");
        }
    }

    private TaskCompletionSource<int?>? _modalChoice;

    /// <summary>
    /// 窗内模态询问：标题 + 一句说明 + 底栏等宽按钮。<paramref name="choices"/> 第一项为主按钮并获得焦点（回车即选），
    /// 末尾自动加取消按钮（文字默认「取消」，可用 <paramref name="cancelLabel"/> 换成「关闭」「稍后」等）；
    /// 选它或按 Esc 返回 null，否则返回所选项的序号。
    /// </summary>
    private Task<int?> AskAsync(string title, string body, IReadOnlyList<string> choices, string? cancelLabel = null)
    {
        CloseModal(null);
        _modalChoice = new TaskCompletionSource<int?>();
        ModalTitle.Text = title;
        ModalBody.Text = body;
        for (var i = 0; i <= choices.Count; i++)
        {
            var index = i;
            var isCancel = i == choices.Count;
            var button = new Button
            {
                Content = isCancel ? cancelLabel ?? LocalizationService.Get("Dialog.Cancel") : choices[i],
                Style = (Style)FindResource(i == 0 ? "PrimaryButton" : "ModernButton"),
                Height = 30,
                FontSize = 12,
                Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(4, 0, 4, 0),
            };
            button.Click += (_, _) => CloseModal(isCancel ? null : index);
            ModalButtons.Children.Add(button);
        }

        ModalLayer.Visibility = Visibility.Visible;
        // 布局完成后再给主按钮焦点：回车直接选它，Tab 只在卡片的按钮间循环
        _ = Dispatcher.InvokeAsync(() => ModalButtons.Children[0].Focus(), System.Windows.Threading.DispatcherPriority.Input);
        return _modalChoice.Task;
    }

    private void CloseModal(int? choice)
    {
        ModalLayer.Visibility = Visibility.Collapsed;
        ModalButtons.Children.Clear();
        var pending = _modalChoice;
        _modalChoice = null;
        pending?.TrySetResult(choice);
    }

    private void ModalLayer_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseModal(null);
        }
    }

    private void PercentBox_OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _viewModel.UpdatePercentInputFocus(
            OpacityPercentBox.IsKeyboardFocusWithin,
            ZoomPercentBox.IsKeyboardFocusWithin);
    }

    private void OpacityPercentBox_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _viewModel.UpdatePercentInputFocus(
            OpacityPercentBox.IsKeyboardFocusWithin,
            ZoomPercentBox.IsKeyboardFocusWithin);
        if (_viewModel.ApplyOpacityCommand.CanExecute(null))
        {
            _viewModel.ApplyOpacityCommand.Execute(null);
        }
    }

    private void ZoomPercentBox_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _viewModel.UpdatePercentInputFocus(
            OpacityPercentBox.IsKeyboardFocusWithin,
            ZoomPercentBox.IsKeyboardFocusWithin);
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
            // Hide 不是 Dispose：若正在录制热键，取消录制并恢复全局钩子，避免一直 Suspend。
            if (_viewModel.IsRecordingAnyKey)
            {
                _viewModel.FinishRecordingKey(Key.Escape, ModifierKeys.None);
            }

            SaveWindowBounds();
            e.Cancel = true;
            Hide();
            return;
        }

        LocationChanged -= ControlWindow_OnLocationOrSizeChanged;
        SizeChanged -= ControlWindow_OnLocationOrSizeChanged;
        SourceInitialized -= ControlWindow_OnSourceInitialized;
        ThemeService.ThemeChanged -= ThemeService_OnThemeChanged;
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
        var animation = new System.Windows.Media.Animation.DoubleAnimation(
            currentOffset,
            targetOffset,
            TimeSpan.FromMilliseconds(AppConfig.Ui.SmoothScrollDurationMs))
        {
            EasingFunction = SmoothScrollEasing,
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
        copyItem.Click += (_, _) =>
        {
            try
            {
                System.Windows.Clipboard.SetText(item.Url);
            }
            catch (Exception ex)
            {
                // 剪贴板可能被其它进程短暂锁定；失败仅记日志，不让异常走到全局未处理弹框
                FileLogger.LogException(ex, "Copy link to clipboard");
            }
        };
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
        // try/finally：恢复边界抛异常（持久化数据损坏等）时标志不能卡在 true，
        // 否则 OnLocationOrSizeChanged 从此直接 return，窗口边界防抖保存永久失效。
        _isRestoringBounds = true;
        try
        {
            var restoredPosition = _browser.RestoreControlWindowBounds(this);
            _hasUserMovedWindow = restoredPosition;

            if (!restoredPosition)
            {
                ShowNearBrowserWindow();
            }
        }
        finally
        {
            _isRestoringBounds = false;
        }
    }

    private void ControlWindow_OnLocationOrSizeChanged(object? sender, EventArgs e)
    {
        // Popup 不跟随窗口移动，避免候选悬空在原位置
        _viewModel.CloseAddressSuggestions();

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

        // 点在地址栏联想下拉里：Popup 内容的路由事件也会经过窗口，这里清焦点会先关掉下拉、吞掉这次点击
        if (AddressSuggestionsPopup.IsOpen && IsDescendantOf(clicked, AddressSuggestionsPopup.Child))
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

    private void AddressBarTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        // 只响应用户输入：无焦点时的导航同步、打开候选后的程序回填都跳过
        if (_suppressAddressSuggestions || !AddressBarTextBox.IsKeyboardFocusWithin)
        {
            return;
        }

        _viewModel.UpdateAddressSuggestions(AddressBarTextBox.Text);
    }

    private void AddressBarTextBox_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _viewModel.CloseAddressSuggestions();
    }

    private void AddressSuggestionsList_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var dataContext = (e.OriginalSource as FrameworkElement)?.DataContext
                          ?? (e.OriginalSource as FrameworkContentElement)?.DataContext;
        if (dataContext is AddressSuggestion suggestion)
        {
            e.Handled = true;
            OpenAddressSuggestion(suggestion);
        }
    }

    /// <summary>联想下拉打开时的按键；返回 true 表示已处理。输入法组字（ImeProcessed）不会走到这些分支。</summary>
    private bool HandleAddressSuggestionKey(Key key)
    {
        var count = AddressSuggestionsList.Items.Count;
        switch (key)
        {
            case Key.Down when count > 0:
                AddressSuggestionsList.SelectedIndex = AddressSuggestionsList.SelectedIndex + 1 >= count
                    ? 0
                    : AddressSuggestionsList.SelectedIndex + 1;
                return true;
            case Key.Up when count > 0:
                // 从输入框向上进入末项；在首项再按 ↑ 回到「未选中」，Enter 即按输入内容跳转
                AddressSuggestionsList.SelectedIndex = AddressSuggestionsList.SelectedIndex < 0
                    ? count - 1
                    : AddressSuggestionsList.SelectedIndex - 1;
                return true;
            case Key.Enter when AddressSuggestionsList.SelectedItem is AddressSuggestion suggestion:
                OpenAddressSuggestion(suggestion);
                return true;
            case Key.Enter:
            case Key.Tab:
                // 未选候选：交给地址栏原有的回车跳转 / Tab 导航
                _viewModel.CloseAddressSuggestions();
                return false;
            case Key.Escape:
                _viewModel.CloseAddressSuggestions();
                return true;
            default:
                return false;
        }
    }

    private void OpenAddressSuggestion(AddressSuggestion suggestion)
    {
        // 回填所选网址并全选（与浏览器地址栏一致），回填本身不触发联想
        _suppressAddressSuggestions = true;
        try
        {
            SetAddressText(suggestion.Url);
        }
        finally
        {
            _suppressAddressSuggestions = false;
        }

        AddressBarTextBox.SelectAll();
        _viewModel.OpenAddressSuggestion(suggestion);
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

        // 联想下拉打开时：↑↓ 选择、Enter 打开所选、Esc 先关下拉（再按一次才恢复地址）
        if (_viewModel.IsAddressSuggestionsOpen &&
            AddressBarTextBox.IsKeyboardFocusWithin &&
            HandleAddressSuggestionKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        // 地址栏按 Esc：放弃编辑，恢复当前页网址（浏览器惯例）
        if (e.Key == Key.Escape && AddressBarTextBox.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            SyncAddressBarFromViewModel(force: true);
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
