using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using GenshinBrowser.Services;

namespace GenshinBrowser.Windows;

/// <summary>
/// 触屏模式的浮窗整窗穿透、标题栏又要能点时，盖在主窗标题栏上的点击层。
/// 主窗整窗带 WS_EX_TRANSPARENT，标题栏本身收不到点击；这层是独立的不激活窗口，按物理像素对齐标题栏，
/// 自己不画任何东西：点到按钮就触发主窗标题栏上的同一个按钮，点到空白处拖动主窗，双击空白处交给调用方（最大化 / 还原）。
/// 鼠标走 WPF 的鼠标事件；触摸 / 笔在窗口过程里用系统转出来的鼠标消息处理（见 <see cref="WndProc"/>）。
/// </summary>
internal sealed class TitleBarClickLayer : Window
{
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private readonly Window _owner;
    private readonly FrameworkElement _titleBar;
    private readonly Action _onDoubleClick;
    private readonly Func<bool> _canDrag;
    // HwndSource 只弱引用钩子：用实例字段保住委托
    private readonly HwndSourceHook _hook;

    private ButtonBase? _pressedButton;
    private bool _isDragging;
    private Point _dragStartCursor;
    private NativeRect _dragStartOwnerRect;
    // 这次按下来自触摸 / 笔、由窗口过程处理，并用 SetCapture 接住之后的移动与抬起
    private bool _isTouchPress;

    // 触摸点按钮：松开后先不执行，等 WPF 自己那份触摸事件（比系统转出的鼠标消息晚几十毫秒）在这层上走完、被忽略，
    // 期间这层保持显示。先执行的话，点击切走模式、这层隐藏，晚到的触摸事件会落到下面主窗同一个按钮上再点一次
    // （2026-11-12 实测：手指点模式按钮，切到浏览又切回浮窗）。这次触摸 WPF 不产生事件（进程里第一次触摸）时等满 WispArrivalWait。
    private static readonly TimeSpan WispArrivalWait = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WispContactWait = TimeSpan.FromSeconds(1);
    private DispatcherTimer? _touchReleaseTimer;
    private bool _hasPendingTouchRelease;
    private Point _pendingTouchPoint;
    // WPF 的这份触摸已经开始（StylusDown 到了、StylusUp 还没到）
    private bool _wispContactOpen;

    public TitleBarClickLayer(Window owner, FrameworkElement titleBar, Action onDoubleClick, Func<bool> canDrag)
    {
        _owner = owner;
        _titleBar = titleBar;
        _onDoubleClick = onDoubleClick;
        _canDrag = canDrag;

        Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        // Alpha=1：肉眼看不见；分层窗只有 alpha 为 0 的像素才让点击穿过去，这里要接住点击
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        // 只在浮窗模式（主窗置顶）显示；作为被拥有窗口本来就在主窗之上
        Topmost = true;
        _hook = WndProc;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 点它不激活（游戏不丢焦点），也不进 Alt-Tab
        var handle = new WindowInteropHelper(this).Handle;
        WindowModeService.UpdateExtendedStyle(handle, set: WsExNoActivate | WsExToolWindow, clear: 0);
        // 按句柄取 HwndSource：这时 PresentationSource.FromVisual 还取不到（根视觉在 SourceInitialized 之后才挂上）
        HwndSource.FromHwnd(handle)?.AddHook(_hook);
    }

    /// <summary>
    /// 窗口过程：
    /// 1. 激活一律拒绝：WS_EX_NOACTIVATE 挡不住触摸带来的激活（2026-11-12 实测，手指点一下点击层就成了前台，游戏丢焦点）。
    /// 2. 触摸 / 笔用系统转出来的鼠标消息处理：WPF 的触摸栈会漏掉进程里的第一次触摸（第一下点控制按钮没反应），
    ///    转出来的鼠标消息每次都有。WPF 那边由触摸产生的鼠标事件在下面的重写里忽略，免得同一下处理两次；
    ///    点按钮时等 WPF 那份事件走完再执行（见 <see cref="WispArrivalWait"/>）。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case PointerMessages.WmPointerActivate:
                handled = true;
                return PointerMessages.PaNoActivate;
            case PointerMessages.WmMouseActivate:
                handled = true;
                return PointerMessages.MaNoActivate;
            case PointerMessages.WmLButtonDown when PointerMessages.IsPromotedFromPenOrTouch():
                handled = true;
                // 上一下还在等就先执行掉；触摸不认双击（双击空白处最大化只给鼠标）
                CompletePendingTouchRelease();
                if (BeginPress(PointerMessages.ClientToScreen(hwnd, lParam), clickCount: 1))
                {
                    _isTouchPress = true;
                    SetCapture(hwnd);
                }

                break;
            case PointerMessages.WmMouseMove when _isTouchPress:
                handled = true;
                MovePress(PointerMessages.ClientToScreen(hwnd, lParam));
                break;
            case PointerMessages.WmLButtonUp when _isTouchPress:
                handled = true;
                ReleaseTouchCapture();
                var releasePoint = PointerMessages.ClientToScreen(hwnd, lParam);
                if (_isDragging)
                {
                    // 拖动结束不切换任何东西，直接收尾
                    EndPress(releasePoint);
                }
                else
                {
                    DeferTouchRelease(releasePoint, _wispContactOpen ? WispContactWait : WispArrivalWait);
                }

                break;
            case PointerMessages.WmCaptureChanged when _isTouchPress:
                // 捕获被收走（切窗口等）：结束这次按下，不算点击
                ReleaseTouchCapture();
                EndPress(screenPoint: null);
                break;
        }

        return IntPtr.Zero;
    }

    private void ReleaseTouchCapture()
    {
        // 先清标记再释放捕获：ReleaseCapture 会同步发 WM_CAPTURECHANGED
        _isTouchPress = false;
        ReleaseCapture();
    }

    private void DeferTouchRelease(Point screenPoint, TimeSpan wait)
    {
        _pendingTouchPoint = screenPoint;
        _hasPendingTouchRelease = true;
        if (_touchReleaseTimer is null)
        {
            _touchReleaseTimer = new DispatcherTimer();
            _touchReleaseTimer.Tick += (_, _) => CompletePendingTouchRelease();
        }

        _touchReleaseTimer.Stop();
        _touchReleaseTimer.Interval = wait;
        _touchReleaseTimer.Start();
    }

    private void CompletePendingTouchRelease()
    {
        _touchReleaseTimer?.Stop();
        if (!_hasPendingTouchRelease)
        {
            return;
        }

        _hasPendingTouchRelease = false;
        _wispContactOpen = false;
        EndPress(_pendingTouchPoint);
    }

    // WPF 自己那份触摸事件：开始了就等它抬起（最多 WispContactWait），抬起后执行等着的点击
    protected override void OnPreviewStylusDown(StylusDownEventArgs e)
    {
        base.OnPreviewStylusDown(e);
        if (_isTouchPress || _hasPendingTouchRelease)
        {
            _wispContactOpen = true;
            if (_hasPendingTouchRelease)
            {
                DeferTouchRelease(_pendingTouchPoint, WispContactWait);
            }
        }
    }

    protected override void OnPreviewStylusUp(StylusEventArgs e)
    {
        base.OnPreviewStylusUp(e);
        _wispContactOpen = false;
        CompletePendingTouchRelease();
    }

    /// <summary>对齐到标题栏后显示：先建句柄、定好位置再显示，不会在默认位置闪一下。</summary>
    public void ShowOverTitleBar()
    {
        new WindowInteropHelper(this).EnsureHandle();
        AlignToTitleBar();
        if (!IsVisible)
        {
            Show();
        }
    }

    /// <summary>
    /// 按物理像素对齐到主窗标题栏（主窗移动、改尺寸、换 DPI 后调用）。拖动中不跟随，松手后再对齐：
    /// 拖动时这层保持不动，按它换算的屏幕坐标才可靠。
    /// </summary>
    public void AlignToTitleBar()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (_isDragging || handle == IntPtr.Zero || PresentationSource.FromVisual(_titleBar) is null ||
            _titleBar.ActualWidth <= 0 || _titleBar.ActualHeight <= 0)
        {
            return;
        }

        var topLeft = _titleBar.PointToScreen(new Point(0, 0));
        var bottomRight = _titleBar.PointToScreen(new Point(_titleBar.ActualWidth, _titleBar.ActualHeight));
        SetWindowPos(
            handle,
            IntPtr.Zero,
            (int)Math.Round(topLeft.X),
            (int)Math.Round(topLeft.Y),
            (int)Math.Round(bottomRight.X - topLeft.X),
            (int)Math.Round(bottomRight.Y - topLeft.Y),
            SwpNoZOrder | SwpNoActivate);
    }

    // 鼠标事件只负责换算屏幕坐标与捕获鼠标，逻辑在 BeginPress / MovePress / EndPress（端到端测试按屏幕坐标直接驱动）。
    // 由触摸 / 笔产生的鼠标事件（StylusDevice 不为空）已在窗口过程里处理，这里忽略
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = true;
        if (e.StylusDevice is null && BeginPress(PointToScreen(e.GetPosition(this)), e.ClickCount) && !CaptureMouse())
        {
            EndPress(screenPoint: null);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isDragging && !_isTouchPress && e.StylusDevice is null)
        {
            MovePress(PointToScreen(e.GetPosition(this)));
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        e.Handled = true;
        if (!_isTouchPress && e.StylusDevice is null && (_isDragging || _pressedButton is not null))
        {
            EndPress(PointToScreen(e.GetPosition(this)));
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        // 捕获被系统收走（切窗口等）：结束拖动 / 按下状态，不算点击；触摸的按下由窗口过程的 WM_CAPTURECHANGED 处理
        if (!_isTouchPress && (_isDragging || _pressedButton is not null))
        {
            EndPress(screenPoint: null);
        }
    }

    /// <summary>按下：点到按钮记下它，点到空白处开始拖动，双击空白处交给调用方。返回 true 表示要捕获鼠标等松手。</summary>
    internal bool BeginPress(Point screenPoint, int clickCount)
    {
        _pressedButton = FindTitleBarButton(screenPoint);
        if (_pressedButton is not null)
        {
            return true;
        }

        if (clickCount >= 2)
        {
            _onDoubleClick();
            return false;
        }

        var ownerHandle = new WindowInteropHelper(_owner).Handle;
        if (!_canDrag() || ownerHandle == IntPtr.Zero || !GetWindowRect(ownerHandle, out _dragStartOwnerRect))
        {
            return false;
        }

        // 不用 DragMove：系统移动循环会激活主窗（游戏丢焦点），触摸下也不可靠
        _dragStartCursor = screenPoint;
        _isDragging = true;
        return true;
    }

    /// <summary>拖动中：按屏幕物理像素移动主窗（这层拖动时不动，见 <see cref="AlignToTitleBar"/>）。</summary>
    internal void MovePress(Point screenPoint)
    {
        if (!_isDragging)
        {
            return;
        }

        SetWindowPos(
            new WindowInteropHelper(_owner).Handle,
            IntPtr.Zero,
            _dragStartOwnerRect.Left + (int)Math.Round(screenPoint.X - _dragStartCursor.X),
            _dragStartOwnerRect.Top + (int)Math.Round(screenPoint.Y - _dragStartCursor.Y),
            0,
            0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    /// <summary>
    /// 松手：和普通按钮一样，松手时仍在按下的那个按钮上才算点击；拖动结束后重新对齐标题栏。
    /// <paramref name="screenPoint"/> 为 null 表示取消（捕获丢失等），不触发点击。
    /// </summary>
    internal void EndPress(Point? screenPoint)
    {
        var button = _pressedButton;
        var wasDragging = _isDragging;
        _pressedButton = null;
        _isDragging = false;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        if (wasDragging)
        {
            AlignToTitleBar();
        }

        if (button is not null && screenPoint is { } point && ReferenceEquals(button, FindTitleBarButton(point)))
        {
            // 标题栏按钮的处理都挂在 Click 事件上（不用 Command），直接引发 Click
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
        }
    }

    /// <summary>屏幕物理坐标处、主窗标题栏上的按钮；没点到可用的按钮返回 null。</summary>
    internal ButtonBase? FindTitleBarButton(Point screenPoint)
    {
        if (PresentationSource.FromVisual(_titleBar) is null)
        {
            return null;
        }

        var point = _titleBar.PointFromScreen(screenPoint);
        if (point.X < 0 || point.Y < 0 || point.X >= _titleBar.ActualWidth || point.Y >= _titleBar.ActualHeight)
        {
            return null;
        }

        for (var current = _titleBar.InputHitTest(point) as DependencyObject;
             current is not null && !ReferenceEquals(current, _titleBar);
             current = current is Visual or Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is ButtonBase button)
            {
                return button.IsEnabled ? button : null;
            }
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();
}
