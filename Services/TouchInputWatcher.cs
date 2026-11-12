using System.Windows;
using System.Windows.Interop;

namespace GenshinBrowser.Services;

/// <summary>
/// 看窗口有没有收到手指触摸（笔、触摸板、鼠标都不算）：WM_POINTERDOWN 且指针是手指，或者系统由手指触摸转出来的
/// 鼠标按下（WPF 的触摸栈接手后窗口只收到这一种，见 <see cref="PointerMessages"/>）。
/// 只看不拦（不置 handled），WPF 照常处理这次输入；远程软件注入的触摸同样会走到这里。
/// 用来提示切换触屏模式。不看系统「有没有触摸屏」：串流结束后系统有时仍报告有。
/// </summary>
internal sealed class TouchInputWatcher
{
    private readonly Action _onTouch;
    // HwndSource 只弱引用钩子：用实例字段保住委托，否则被回收后静默失效
    private readonly HwndSourceHook _hook;

    /// <param name="onTouch">收到触摸按下时调用（UI 线程、在窗口过程里，须尽快返回，界面改动请投递出去）。</param>
    public TouchInputWatcher(Window window, Action onTouch)
    {
        _onTouch = onTouch;
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
        if ((msg == PointerMessages.WmPointerDown && PointerMessages.IsTouchPointer(wParam)) ||
            (msg == PointerMessages.WmLButtonDown && PointerMessages.IsPromotedFromTouch()))
        {
            _onTouch();
        }

        return IntPtr.Zero;
    }
}
