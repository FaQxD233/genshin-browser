using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Shell;
using GenshinBrowser.Models;

namespace GenshinBrowser.Services;

/// <summary>
/// 任务栏跳转列表（右键程序图标的菜单）：四个菜单项、命令行 <c>--command 名称</c> 的解析、把命令转给已在运行的实例用的窗口消息。
/// 程序清单要求管理员，点菜单项时系统再启动一次程序，每次弹 UAC（用户 2026-11-12 认可）；
/// 两个进程都是管理员，同权限 PostMessage 不受 UIPI 限制。
/// </summary>
internal static class AppCommands
{
    public const string Switch = "--command";

    // 命令、命令行里的名称、菜单标题的文案键；顺序即菜单顺序
    private static readonly (AppCommand Command, string Name, string TitleKey)[] Items =
    [
        (AppCommand.ToggleMode, "toggle-mode", "JumpList.ToggleMode"),
        (AppCommand.ToggleTouchMode, "toggle-touch", "JumpList.ToggleTouchMode"),
        (AppCommand.ToggleHide, "toggle-hide", "JumpList.ToggleHide"),
        (AppCommand.TogglePlayback, "toggle-playback", "JumpList.TogglePlayback"),
    ];

    /// <summary>主窗接收命令的窗口消息：两个进程按同一个名称注册，拿到同一个编号；注册失败时为 0，不收也不发。</summary>
    public static uint Message { get; } = RegisterWindowMessage("GenshinBrowser.AppCommand");

    /// <summary>从命令行取命令（<c>--command 名称</c>，不区分大小写）；没有、缺名称或名称不认识时返回 false。</summary>
    public static bool TryParse(IReadOnlyList<string> args, out AppCommand command)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (!string.Equals(args[i], Switch, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var item in Items)
            {
                if (string.Equals(args[i + 1], item.Name, StringComparison.OrdinalIgnoreCase))
                {
                    command = item.Command;
                    return true;
                }
            }

            break;
        }

        command = default;
        return false;
    }

    /// <summary>把命令投递给已在运行的实例的主窗（不等它执行）。</summary>
    public static bool Post(IntPtr window, AppCommand command) =>
        Message != 0 && PostMessage(window, Message, (IntPtr)(int)command, IntPtr.Zero);

    /// <summary>窗口过程里认命令消息：消息号对、wParam 是已知命令时返回 true。</summary>
    public static bool TryRead(int msg, IntPtr wParam, out AppCommand command)
    {
        command = default;
        if (Message == 0 || (uint)msg != Message)
        {
            return false;
        }

        var value = wParam.ToInt64();
        if (value is < int.MinValue or > int.MaxValue)
        {
            return false;
        }

        command = (AppCommand)(int)value;
        return Enum.IsDefined(command);
    }

    /// <summary>跳转列表：四个菜单项，都是带 <c>--command 名称</c> 再启动一次 <paramref name="applicationPath"/>，标题按当前界面语言。</summary>
    public static JumpList CreateJumpList(string applicationPath)
    {
        var jumpList = new JumpList { ShowFrequentCategory = false, ShowRecentCategory = false };
        foreach (var item in Items)
        {
            jumpList.JumpItems.Add(new JumpTask
            {
                Title = LocalizationService.Get(item.TitleKey),
                ApplicationPath = applicationPath,
                Arguments = $"{Switch} {item.Name}",
                WorkingDirectory = Path.GetDirectoryName(applicationPath),
                IconResourcePath = applicationPath,
            });
        }

        return jumpList;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
