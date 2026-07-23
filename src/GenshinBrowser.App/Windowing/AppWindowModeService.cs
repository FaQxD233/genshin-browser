using GenshinBrowser.Models;
using Microsoft.UI.Windowing;

namespace GenshinBrowser.Windowing;

internal sealed class AppWindowModeService : IWindowModeService
{
    private readonly AppWindow appWindow;
    private readonly nint windowHandle;

    public AppWindowModeService(AppWindow appWindow, nint windowHandle)
    {
        this.appWindow = appWindow;
        this.windowHandle = windowHandle;
    }

    public WindowMode CurrentMode { get; private set; } = WindowMode.Free;

    public void Apply(WindowMode mode)
    {
        if (appWindow.Presenter is not OverlappedPresenter presenter)
        {
            throw new InvalidOperationException("BrowserWindow requires an OverlappedPresenter.");
        }

        bool floating = mode == WindowMode.Fixed;
        if (floating && presenter.State == OverlappedPresenterState.Maximized)
        {
            presenter.Restore();
        }
        presenter.PreferredMinimumWidth = WindowDpi.ScaleLength(640, windowHandle);
        presenter.PreferredMinimumHeight = WindowDpi.ScaleLength(floating ? 360 : 400, windowHandle);
        presenter.IsAlwaysOnTop = floating;
        presenter.IsResizable = !floating;
        presenter.IsMaximizable = !floating;
        presenter.IsMinimizable = true;
        presenter.SetBorderAndTitleBar(true, false);
        CurrentMode = mode;
    }
}
