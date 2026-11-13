namespace GenshinBrowser.Models;

/// <summary>
/// 任务栏跳转列表（右键程序图标）的命令：点菜单项时系统带 <c>--command 名称</c> 再启动一次程序，
/// 新进程把命令转给已在运行的实例后退出。数值放在窗口消息的 wParam 里传，已有成员的值不要改。
/// </summary>
public enum AppCommand
{
    ToggleMode = 1,
    ToggleTouchMode = 2,
    ToggleHide = 3,
    TogglePlayback = 4,
}
