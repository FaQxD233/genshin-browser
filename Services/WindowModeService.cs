using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using GenshinBrowser.Models;

namespace GenshinBrowser.Services;

public sealed class WindowModeService
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    internal const int WsExNoActivate = 0x08000000;

    private readonly Window _window;

    public WindowModeService(Window window)
    {
        _window = window;
    }

    /// <param name="floatingClickThrough">浮窗模式是否鼠标穿透；浏览模式始终可点击。</param>
    public void ApplyMode(WindowMode mode, bool floatingClickThrough)
    {
        // 主窗口为无边框分层浮窗（AllowsTransparency=True + WindowStyle=None），
        // 不能再将 WindowStyle 改回 SingleBorderWindow，否则会抛 InvalidOperationException。
        // 两种模式均保持无边框，仅调整置顶、缩放策略与鼠标穿透。
        var floating = mode == WindowMode.Fixed;
        _window.Topmost = floating;
        _window.ResizeMode = floating ? ResizeMode.NoResize : ResizeMode.CanResize;
        // 放在 WPF 改完 Topmost/ResizeMode 之后，只翻转 WS_EX_TRANSPARENT 一位
        SetClickThrough(floating && floatingClickThrough);
    }

    /// <summary>
    /// WS_EX_TRANSPARENT 叠加分层窗口样式（AllowsTransparency）后整窗不接收鼠标，点击落到下层窗口。
    /// 窗口句柄尚未创建时跳过，由调用方在 SourceInitialized 后重新应用。
    /// </summary>
    private void SetClickThrough(bool enabled)
    {
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        UpdateExtendedStyle(handle, set: enabled ? WsExTransparent : 0, clear: enabled ? 0 : WsExTransparent);
    }

    /// <summary>
    /// WS_EX_NOACTIVATE：点窗口不激活，前台也不转过来。光对 WM_MOUSEACTIVATE 回答不激活还不够：前台会转到本程序、
    /// 却没有活动窗口，原来的前台程序（游戏）照样丢焦点（2026-11-14 实测）。窗口句柄尚未创建时跳过。
    /// </summary>
    public void SetNoActivate(bool enabled)
    {
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        UpdateExtendedStyle(handle, set: enabled ? WsExNoActivate : 0, clear: enabled ? 0 : WsExNoActivate);
    }

    /// <summary>置位 / 清除窗口扩展样式的若干位；结果不变时不写。</summary>
    internal static void UpdateExtendedStyle(IntPtr handle, int set, int clear)
    {
        var exStyle = GetWindowLong(handle, GwlExStyle);
        var updated = (exStyle | set) & ~clear;
        if (updated != exStyle)
        {
            SetWindowLong(handle, GwlExStyle, updated);
        }
    }

    // 扩展样式是 32 位 DWORD：GetWindowLong/SetWindowLong 在 32/64 位进程下都适用（Ptr 变体只有指针型索引才需要）
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
