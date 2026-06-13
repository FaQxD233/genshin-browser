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
        var transform = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new System.Windows.Point(workArea.Left, workArea.Top));
        var bottomRight = transform.Transform(new System.Windows.Point(workArea.Right, workArea.Bottom));

        return new Rect(topLeft, bottomRight);
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
