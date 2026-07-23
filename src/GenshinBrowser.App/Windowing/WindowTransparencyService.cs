using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GenshinBrowser.Windowing;

internal sealed class WindowTransparencyService : IWindowTransparencyService
{
    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x00080000L;
    private const uint LwaAlpha = 0x00000002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private readonly nint windowHandle;

    public WindowTransparencyService(nint windowHandle)
    {
        if (windowHandle == 0)
        {
            throw new ArgumentException("A valid window handle is required.", nameof(windowHandle));
        }

        this.windowHandle = windowHandle;
    }

    public double Opacity { get; private set; } = 1.0;

    public bool IsLayered => (GetExtendedStyle() & WsExLayered) != 0;

    public void Apply(double opacity)
    {
        WindowTransparencyDecision decision = WindowTransparencyPolicy.Create(opacity);
        if (!decision.UseLayeredWindow)
        {
            RestoreOpaqueWindow();
            Opacity = 1.0;
            return;
        }

        long style = GetExtendedStyle();
        if ((style & WsExLayered) == 0)
        {
            SetExtendedStyle(style | WsExLayered);
        }

        if (!SetLayeredWindowAttributes(windowHandle, 0, decision.Alpha, LwaAlpha))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        Opacity = decision.Opacity;
    }

    private void RestoreOpaqueWindow()
    {
        long style = GetExtendedStyle();
        if ((style & WsExLayered) == 0)
        {
            return;
        }

        if (!SetLayeredWindowAttributes(windowHandle, 0, byte.MaxValue, LwaAlpha))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        SetExtendedStyle(style & ~WsExLayered);
        if (!SetWindowPos(
                windowHandle,
                0,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private long GetExtendedStyle()
    {
        Marshal.SetLastPInvokeError(0);
        nint value = GetWindowLongPtr(windowHandle, GwlExStyle);
        int error = Marshal.GetLastPInvokeError();
        if (value == 0 && error != 0)
        {
            throw new Win32Exception(error);
        }

        return value.ToInt64();
    }

    private void SetExtendedStyle(long style)
    {
        Marshal.SetLastPInvokeError(0);
        nint previous = SetWindowLongPtr(windowHandle, GwlExStyle, new IntPtr(style));
        int error = Marshal.GetLastPInvokeError();
        if (previous == 0 && error != 0)
        {
            throw new Win32Exception(error);
        }
    }

    private static nint GetWindowLongPtr(nint hwnd, int index)
    {
        return IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));
    }

    private static nint SetWindowLongPtr(nint hwnd, int index, nint value)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hwnd, index, value)
            : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint hwnd, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(nint hwnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
