using GenshinBrowser.Browser;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace GenshinBrowser.Windowing;

internal sealed class AppWindowPlacementService : IWindowPlacementService
{
    private readonly AppWindow appWindow;
    private readonly nint windowHandle;

    public AppWindowPlacementService(AppWindow appWindow, nint windowHandle)
    {
        this.appWindow = appWindow;
        this.windowHandle = windowHandle;
    }

    public bool CanPersistCurrentBounds =>
        appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored };

    public WindowBounds Capture()
    {
        // The settings contract intentionally remains WPF-compatible DIP. AppWindow
        // exposes physical virtual-desktop pixels, including negative monitor origins.
        double scale = GetScale();
        return new WindowBounds(
            appWindow.Position.X / scale,
            appWindow.Position.Y / scale,
            appWindow.Size.Width / scale,
            appWindow.Size.Height / scale);
    }

    public void Restore(WindowBounds bounds)
    {
        if (!bounds.IsValid)
        {
            return;
        }

        // Convert the saved DIP rectangle using the current monitor scale first. After
        // the move, a different monitor may report another scale; the second pass below
        // reprojects the same DIP rectangle so mixed-DPI and negative-coordinate layouts
        // settle without changing the user's logical size.
        double scale = GetScale();
        RectInt32 requested = new(
            ToPixel(bounds.Left, scale),
            ToPixel(bounds.Top, scale),
            Math.Max(1, ToPixel(bounds.Width, scale)),
            Math.Max(1, ToPixel(bounds.Height, scale)));
        appWindow.MoveAndResize(ClampToWorkArea(requested));

        double targetScale = GetScale();
        if (Math.Abs(targetScale - scale) > 0.001)
        {
            RectInt32 dpiAdjusted = new(
                ToPixel(bounds.Left, targetScale),
                ToPixel(bounds.Top, targetScale),
                Math.Max(1, ToPixel(bounds.Width, targetScale)),
                Math.Max(1, ToPixel(bounds.Height, targetScale)));
            appWindow.MoveAndResize(ClampToWorkArea(dpiAdjusted));
        }
    }

    public void Resize(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height))
        {
            return;
        }

        RestoreIfMaximized();
        double scale = GetScale();
        RectInt32 workArea = GetWorkArea();
        int pixelWidth = Math.Clamp(ToPixel(width, scale), 1, workArea.Width);
        int pixelHeight = Math.Clamp(ToPixel(height, scale), 1, workArea.Height);
        RectInt32 resized = new(
            appWindow.Position.X,
            appWindow.Position.Y,
            pixelWidth,
            pixelHeight);
        appWindow.MoveAndResize(ClampToWorkArea(resized));
    }

    public void MoveToCorner(WindowCorner corner)
    {
        RestoreIfMaximized();
        RectInt32 workArea = GetWorkArea();
        SizeInt32 size = appWindow.Size;
        int x = corner is WindowCorner.TopRight or WindowCorner.BottomRight
            ? Math.Max(workArea.X, workArea.X + workArea.Width - size.Width)
            : workArea.X;
        int y = corner is WindowCorner.BottomLeft or WindowCorner.BottomRight
            ? Math.Max(workArea.Y, workArea.Y + workArea.Height - size.Height)
            : workArea.Y;
        appWindow.Move(new PointInt32(x, y));
    }

    private void RestoreIfMaximized()
    {
        if (appWindow.Presenter is OverlappedPresenter
            {
                State: OverlappedPresenterState.Maximized,
            } presenter)
        {
            presenter.Restore();
        }
    }

    private RectInt32 ClampToWorkArea(RectInt32 requested)
    {
        RectInt32 workArea = GetWorkArea(requested);
        int width = Math.Clamp(requested.Width, 1, workArea.Width);
        int height = Math.Clamp(requested.Height, 1, workArea.Height);
        int maxX = workArea.X + workArea.Width - width;
        int maxY = workArea.Y + workArea.Height - height;
        int x = Math.Clamp(requested.X, workArea.X, maxX);
        int y = Math.Clamp(requested.Y, workArea.Y, maxY);
        return new RectInt32(x, y, width, height);
    }

    private RectInt32 GetWorkArea()
    {
        DisplayArea displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest);
        return displayArea.WorkArea;
    }

    private static RectInt32 GetWorkArea(RectInt32 requested)
    {
        DisplayArea displayArea = DisplayArea.GetFromRect(requested, DisplayAreaFallback.Nearest);
        return displayArea.WorkArea;
    }

    private double GetScale()
    {
        return WindowDpi.GetScale(windowHandle);
    }

    private static int ToPixel(double value, double scale)
    {
        double pixels = Math.Round(value * scale);
        return (int)Math.Clamp(pixels, int.MinValue, int.MaxValue);
    }
}
