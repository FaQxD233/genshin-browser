using System.Windows;
using GenshinBrowser.Models;

namespace GenshinBrowser.Services;

public sealed class WindowModeService
{
    private readonly Window _window;

    public WindowModeService(Window window)
    {
        _window = window;
    }

    public void ApplyMode(WindowMode mode)
    {
        if (mode == WindowMode.Fixed)
        {
            _window.Topmost = true;
            _window.ResizeMode = ResizeMode.NoResize;
            _window.WindowStyle = WindowStyle.SingleBorderWindow;
            return;
        }

        _window.Topmost = false;
        _window.ResizeMode = ResizeMode.CanResize;
        _window.WindowStyle = WindowStyle.SingleBorderWindow;
    }
}
