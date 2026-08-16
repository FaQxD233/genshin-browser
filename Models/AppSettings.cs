using System.Windows.Input;

namespace GenshinBrowser.Models;

public sealed class AppSettings
{
    /// <summary>
    /// 1 = WPF <see cref="Key"/> 枚举整型；2 = Win32 虚拟键码（WinUI 侧）。
    /// 与 WinUI 共用 %LocalAppData%\GenshinBrowser\settings.json，读写必须按版本解释热键字段。
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    public string LastUrl { get; set; } = string.Empty;

    public WindowMode WindowMode { get; set; } = WindowMode.Free;

    public double WindowLeft { get; set; } = 100;

    public double WindowTop { get; set; } = 100;

    public double WindowWidth { get; set; } = 658;

    public double WindowHeight { get; set; } = 370;

    public double ControlWindowLeft { get; set; } = -1;

    public double ControlWindowTop { get; set; } = -1;

    public bool HasControlWindowPosition { get; set; }

    public double ControlWindowWidth { get; set; } = 640;

    public double ControlWindowHeight { get; set; } = 853;

    public double WindowOpacity { get; set; } = 1.0;

    /// <summary>
    /// WebView2 页面缩放系数，1.0 = 100%。
    /// </summary>
    public double ZoomFactor { get; set; } = 1.0;

    public Key ToggleModeKey { get; set; } = Key.F8;

    public ModifierKeys ToggleModeModifiers { get; set; } = ModifierKeys.None;

    public Key TogglePlaybackKey { get; set; } = Key.K;

    public ModifierKeys TogglePlaybackModifiers { get; set; } = ModifierKeys.None;

    /// <summary>浮窗模式临时隐藏/恢复显示浮窗热键。默认 F7（与 F8 相邻，避开游戏快速读档常占的 F9）。</summary>
    public Key ToggleHideKey { get; set; } = Key.F7;

    public ModifierKeys ToggleHideModifiers { get; set; } = ModifierKeys.None;

    /// <summary>视频倒退 5 秒热键。默认 [（US 布局 VK_OEM_4）。</summary>
    public Key SeekBackwardKey { get; set; } = Key.OemOpenBrackets;

    public ModifierKeys SeekBackwardModifiers { get; set; } = ModifierKeys.None;

    /// <summary>视频快进 5 秒热键。默认 ]（US 布局 VK_OEM_6）。</summary>
    public Key SeekForwardKey { get; set; } = Key.OemCloseBrackets;

    public ModifierKeys SeekForwardModifiers { get; set; } = ModifierKeys.None;

    /// <summary>
    /// 主题：Dark / Light / System。默认 Dark。System 跟随 Windows 应用浅/深色。
    /// </summary>
    public string ThemeMode { get; set; } = "Dark";

    /// <summary>
    /// 界面语言：zh-CN / en-US。默认 zh-CN。
    /// </summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>
    /// 全局热键生效范围：黑名单外 / 全部应用 / 关闭。默认黑名单外。
    /// </summary>
    public HotkeyScope HotkeyScope { get; set; } = HotkeyScope.Blacklist;

    /// <summary>
    /// 是否已展示过「首次进入浮窗」引导 toast。
    /// </summary>
    public bool HasSeenFloatingModeHint { get; set; }

    /// <summary>
    /// 上次执行 WebView2 缓存大小检查的 UTC 时间。用于限制检查频率（每 24 小时一次），
    /// 避免每次启动都递归枚举 WebViewProfile 目录。
    /// 默认 DateTime.MinValue 表示从未检查过，下次启动会执行。
    /// </summary>
    public DateTime LastWebView2CacheCheckUtc { get; set; } = DateTime.MinValue;

    /// <summary>
    /// 是否已尝试过「VK 被当成 Key 枚举 round-trip」的损坏修复（一次性迁移标志）。
    /// 默认 false：旧配置首次加载时尝试一次 RepairKnownHotkeyCorruption 并置 true；
    /// 之后即使用户把热键改成与损坏特征相同的组合（RightCtrl + NumPad1）也不会再被重置。
    /// </summary>
    public bool HotkeyCorruptionRepairAttempted { get; set; }
}
