using System.Runtime.InteropServices;
using Windows.Graphics;

namespace GenshinBrowser.Windowing;

internal static class WindowDpi
{
    public static double GetScale(nint windowHandle)
    {
        uint dpi = GetDpiForWindow(windowHandle);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    public static int ScaleLength(double value, nint windowHandle)
    {
        return ToPixel(value, GetScale(windowHandle));
    }

    public static SizeInt32 ScaleSize(double width, double height, nint windowHandle)
    {
        double scale = GetScale(windowHandle);
        return new SizeInt32(ToPixel(width, scale), ToPixel(height, scale));
    }

    public static int ToPixel(double value, double scale)
    {
        double pixels = Math.Round(value * scale);
        return (int)Math.Clamp(pixels, 1, int.MaxValue);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
