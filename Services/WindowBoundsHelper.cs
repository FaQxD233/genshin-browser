using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace GenshinBrowser.Services;

internal static class WindowBoundsHelper
{
    public static Rect GetWorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return SystemParameters.WorkArea;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            return SystemParameters.WorkArea;
        }

        var transform = GetDeviceToDipTransform(window);
        var topLeft = transform.Transform(new System.Windows.Point(monitorInfo.WorkArea.Left, monitorInfo.WorkArea.Top));
        var bottomRight = transform.Transform(new System.Windows.Point(monitorInfo.WorkArea.Right, monitorInfo.WorkArea.Bottom));

        return new Rect(topLeft, bottomRight);
    }

    /// <summary>
    /// 设备像素 → DIP。窗口尚未挂上 PresentationSource 时，回退到系统 DPI，
    /// 避免高分屏上把物理像素工作区当成 DIP 导致 clamp 错位。
    /// </summary>
    private static Matrix GetDeviceToDipTransform(Window window)
    {
        var fromSource = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice;
        if (fromSource is { } matrix)
        {
            return matrix;
        }

        try
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            if (dpi.DpiScaleX > 0 && dpi.DpiScaleY > 0)
            {
                return new Matrix(1.0 / dpi.DpiScaleX, 0, 0, 1.0 / dpi.DpiScaleY, 0, 0);
            }
        }
        catch
        {
            // 视觉树未就绪时忽略
        }

        return Matrix.Identity;
    }

    public static bool HasHandle(Window window) => new WindowInteropHelper(window).Handle != IntPtr.Zero;

    public static void ClampToWorkArea(Window window)
    {
        ClampToWorkArea(window, GetWorkArea(window));
    }

    public static void ClampToWorkArea(Window window, Rect workArea)
    {
        var maxWidth = Math.Max(window.MinWidth, workArea.Width);
        var maxHeight = Math.Max(window.MinHeight, workArea.Height);

        if (window.Width > maxWidth)
        {
            window.Width = maxWidth;
        }

        if (window.Height > maxHeight)
        {
            window.Height = maxHeight;
        }

        var maxLeft = Math.Max(workArea.Left, workArea.Right - window.Width);
        var maxTop = Math.Max(workArea.Top, workArea.Bottom - window.Height);

        window.Left = ClampFinite(window.Left, workArea.Left, maxLeft);
        window.Top = ClampFinite(window.Top, workArea.Top, maxTop);
    }

    private static double ClampFinite(double value, double min, double max)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return min;
        }

        return Math.Min(Math.Max(value, min), max);
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref MonitorInfo monitorInfo);
}
