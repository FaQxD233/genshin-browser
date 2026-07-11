using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace GenshinBrowser.Services;

internal static class WindowBoundsHelper
{
    public static Rect GetWorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var screen = handle == IntPtr.Zero
            ? Forms.Screen.PrimaryScreen!
            : Forms.Screen.FromHandle(handle);

        var workArea = screen.WorkingArea;
        var transform = GetDeviceToDipTransform(window);
        var topLeft = transform.Transform(new System.Windows.Point(workArea.Left, workArea.Top));
        var bottomRight = transform.Transform(new System.Windows.Point(workArea.Right, workArea.Bottom));

        return new Rect(topLeft, bottomRight);
    }

    /// <summary>
    /// 设备像素 → DIP。窗口尚未挂上 PresentationSource 时，回退到系统 DPI，
    /// 避免高分屏上把物理像素工作区当成 DIP 导致 clamp 错位。
    /// </summary>
    private static Matrix GetDeviceToDipTransform(Window window)
    {
        var fromSource = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice;
        if (fromSource is { } matrix && !matrix.IsIdentity)
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

        try
        {
            using var graphics = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
            if (graphics.DpiX > 0 && graphics.DpiY > 0)
            {
                return new Matrix(96.0 / graphics.DpiX, 0, 0, 96.0 / graphics.DpiY, 0, 0);
            }
        }
        catch
        {
            // 回退 Identity
        }

        return Matrix.Identity;
    }

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
}
