using System.Runtime.InteropServices;
using System.Windows;

namespace GenshinBrowser.Services;

/// <summary>
/// 触摸 / 笔输入在窗口过程里的样子：WM_POINTER* 消息，以及系统把它们转成的鼠标消息（GetMessageExtraInfo 带 MI_WP_SIGNATURE）。
/// 2026-11-12 用 InjectTouchInput 实测（分层窗口）：进程里的第一次触摸只有指针消息和转出来的鼠标消息，WPF 不产生任何事件；
/// 之后 WPF 的触摸栈接手，窗口过程只剩转出来的鼠标消息。所以检测和处理都要在窗口过程里认这两种。
/// </summary>
internal static class PointerMessages
{
    public const int WmMouseActivate = 0x0021;
    public const int WmMouseMove = 0x0200;
    public const int WmLButtonDown = 0x0201;
    public const int WmLButtonUp = 0x0202;
    public const int WmCaptureChanged = 0x0215;
    public const int WmPointerDown = 0x0246;
    public const int WmPointerActivate = 0x024B;
    /// <summary>WM_MOUSEACTIVATE 的返回值：不激活，点击照常送达。</summary>
    public const int MaNoActivate = 3;
    /// <summary>WM_POINTERACTIVATE 的返回值：不激活，指针输入照常送达。</summary>
    public const int PaNoActivate = 3;

    private const int PtTouch = 2;
    private const long SignatureMask = 0xFFFFFF00;
    // MI_WP_SIGNATURE：系统由触摸 / 笔转出的鼠标消息，附加信息高 24 位是它；0x80 位表示触摸（否则是笔）
    private const long PromotedSignature = 0xFF515700;
    private const long TouchFlag = 0x80;

    /// <summary>窗口过程正在处理的鼠标消息是不是系统由触摸或笔转出来的。</summary>
    public static bool IsPromotedFromPenOrTouch() =>
        (GetMessageExtraInfo().ToInt64() & SignatureMask) == PromotedSignature;

    /// <summary>窗口过程正在处理的鼠标消息是不是由手指触摸转出来的。</summary>
    public static bool IsPromotedFromTouch()
    {
        var extraInfo = GetMessageExtraInfo().ToInt64();
        return (extraInfo & SignatureMask) == PromotedSignature && (extraInfo & TouchFlag) != 0;
    }

    /// <summary>WM_POINTER* 消息（wParam 低 16 位是指针 ID）对应的是不是手指。</summary>
    public static bool IsTouchPointer(IntPtr wParam) =>
        GetPointerType((uint)(wParam.ToInt64() & 0xFFFF), out var pointerType) && pointerType == PtTouch;

    /// <summary>鼠标消息 lParam 里的客户区坐标换成屏幕物理坐标。</summary>
    public static Point ClientToScreen(IntPtr hwnd, IntPtr lParam)
    {
        var value = lParam.ToInt64();
        var point = new NativePoint { X = (short)(value & 0xFFFF), Y = (short)((value >> 16) & 0xFFFF) };
        ClientToScreenNative(hwnd, ref point);
        return new Point(point.X, point.Y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetMessageExtraInfo();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPointerType(uint pointerId, out int pointerType);

    [DllImport("user32.dll", EntryPoint = "ClientToScreen")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreenNative(IntPtr hwnd, ref NativePoint point);
}
