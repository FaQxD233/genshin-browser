using System.Runtime.InteropServices;

namespace GenshinBrowser.Windowing;

internal static class WindowOwnerService
{
    private const int GwlHwndParent = -8;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExAppWindow = 0x00040000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    public static bool TrySetOwner(nint childWindow, nint ownerWindow, out int errorCode)
    {
        if (childWindow == 0 || ownerWindow == 0 || childWindow == ownerWindow)
        {
            errorCode = 87;
            return false;
        }

        Marshal.SetLastPInvokeError(0);
        nint previousOwner = SetWindowLongPtr(childWindow, GwlHwndParent, ownerWindow);
        errorCode = Marshal.GetLastPInvokeError();
        if (previousOwner == 0 && errorCode != 0)
        {
            return false;
        }

        Marshal.SetLastPInvokeError(0);
        nint extendedStyleValue = GetWindowLongPtr(childWindow, GwlExStyle);
        errorCode = Marshal.GetLastPInvokeError();
        if (extendedStyleValue == 0 && errorCode != 0)
        {
            return false;
        }

        long extendedStyle = extendedStyleValue.ToInt64();
        long ownedWindowStyle = (extendedStyle & ~WsExAppWindow) | WsExToolWindow;
        if (ownedWindowStyle == extendedStyle)
        {
            errorCode = 0;
            return true;
        }

        Marshal.SetLastPInvokeError(0);
        nint previousStyle = SetWindowLongPtr(childWindow, GwlExStyle, new IntPtr(ownedWindowStyle));
        errorCode = Marshal.GetLastPInvokeError();
        if (previousStyle == 0 && errorCode != 0)
        {
            return false;
        }

        if (!SetWindowPos(
                childWindow,
                0,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged))
        {
            errorCode = Marshal.GetLastPInvokeError();
            return false;
        }

        errorCode = 0;
        return true;
    }

    private static nint GetWindowLongPtr(nint windowHandle, int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(windowHandle, index)
            : new IntPtr(GetWindowLong32(windowHandle, index));
    }

    private static nint SetWindowLongPtr(nint windowHandle, int index, nint value)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(windowHandle, index, value)
            : new IntPtr(SetWindowLong32(windowHandle, index, value.ToInt32()));
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint windowHandle, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(nint windowHandle, int index, int value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(nint windowHandle, int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
