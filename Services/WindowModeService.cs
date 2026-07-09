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
        // 主窗口为无边框分层浮窗（AllowsTransparency=True + WindowStyle=None），
        // 不能再将 WindowStyle 改回 SingleBorderWindow，否则会抛 InvalidOperationException。
        // 两种模式均保持无边框，仅调整置顶与缩放策略。
        if (mode == WindowMode.Fixed)
        {
            _window.Topmost = true;
            _window.ResizeMode = ResizeMode.NoResize;
            return;
        }

        _window.Topmost = false;
        _window.ResizeMode = ResizeMode.CanResize;
    }
}
