using System.Windows;
using System.Windows.Interop;

namespace GenshinBrowser.Services;

/// <summary>
/// 看窗口收到的是手指触摸还是鼠标，用来提示切换触屏 / 键鼠模式：
/// - 手指：WM_POINTERDOWN 且指针是手指，或者系统由手指触摸转出来的鼠标按下（WPF 的触摸栈接手后窗口只收到这一种，见 <see cref="PointerMessages"/>）；
/// - 鼠标：鼠标按键按下或滚轮，且不是由触摸 / 笔转出来的。远程软件注入的鼠标在这里和实体鼠标分不出来，也算鼠标。
/// 只看不拦（不置 handled），WPF 照常处理这次输入。不看系统「有没有触摸屏」：串流结束后系统有时仍报告有。
/// </summary>
internal sealed class PointerInputWatcher
{
    private readonly Action _onTouch;
    private readonly Action _onMouse;
    // 窗口过程钩子（HwndSource 强引用挂上的钩子，本对象随窗口存活）
    private readonly HwndSourceHook _hook;

    /// <param name="onTouch">收到手指按下时调用。</param>
    /// <param name="onMouse">收到鼠标按下 / 滚轮时调用。</param>
    /// <remarks>两个回调都在 UI 线程的窗口过程里调用，须尽快返回，界面改动请投递出去。</remarks>
    public PointerInputWatcher(Window window, Action onTouch, Action onMouse)
    {
        _onTouch = onTouch;
        _onMouse = onMouse;
        _hook = WndProc;
        if (!TryAddHook(window))
        {
            // 句柄还没创建：等 SourceInitialized 再挂
            window.SourceInitialized += (_, _) => TryAddHook(window);
        }
    }

    // 按句柄取 HwndSource：SourceInitialized 时 PresentationSource.FromVisual 还可能取不到（根视觉稍后才挂上）
    private bool TryAddHook(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || HwndSource.FromHwnd(handle) is not { } source)
        {
            return false;
        }

        source.AddHook(_hook);
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case PointerMessages.WmPointerDown when PointerMessages.IsTouchPointer(wParam):
                _onTouch();
                break;
            case PointerMessages.WmLButtonDown or PointerMessages.WmRButtonDown or PointerMessages.WmMButtonDown or
                PointerMessages.WmXButtonDown or PointerMessages.WmNcLButtonDown or
                PointerMessages.WmMouseWheel or PointerMessages.WmMouseHWheel:
                if (PointerMessages.IsPromotedFromTouch())
                {
                    _onTouch();
                }
                else if (!PointerMessages.IsPromotedFromPenOrTouch())
                {
                    _onMouse();
                }

                break;
        }

        return IntPtr.Zero;
    }
}
