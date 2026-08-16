namespace GenshinBrowser.Models;

/// <summary>
/// 全局热键（模式切换 / 播放 / 隐藏 / 快进倒退）的生效范围。
/// </summary>
public enum HotkeyScope
{
    /// <summary>除黑名单中的非游戏软件外，任意前台程序都触发（默认，与播放键旧行为一致）。</summary>
    Blacklist = 0,

    /// <summary>任意前台程序都触发，忽略黑名单。</summary>
    AllApps = 1,

    /// <summary>任意前台程序都不触发（相当于临时关闭全局热键；隐藏中的浮窗仍可用隐藏键恢复）。</summary>
    Off = 2,
}
