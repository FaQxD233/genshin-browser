using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GenshinBrowser.Services;

/// <summary>
/// 让使用 Windows 原生非客户区的 WPF 窗口标题栏跟随应用实际亮/暗主题。
/// </summary>
internal static class NativeTitleBarService
{
    // Windows 10 20H1+ / Windows 11 使用 20；较早 Windows 10 版本使用 19。
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    public static void Apply(Window window, bool useDarkMode)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = useDarkMode ? 1 : 0;
        try
        {
            var result = DwmSetWindowAttribute(
                handle,
                DwmwaUseImmersiveDarkMode,
                ref enabled,
                Marshal.SizeOf<int>());

            if (result < 0)
            {
                _ = DwmSetWindowAttribute(
                    handle,
                    DwmwaUseImmersiveDarkModeBefore20H1,
                    ref enabled,
                    Marshal.SizeOf<int>());
            }
        }
        catch (DllNotFoundException)
        {
            // 不支持 DWM 的环境保持系统默认标题栏。
        }
        catch (EntryPointNotFoundException)
        {
            // 旧系统缺少该入口时保持系统默认标题栏。
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
