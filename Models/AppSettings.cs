using System.Windows.Input;

namespace GenshinBrowser.Models;

public sealed class AppSettings
{
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

    public Key ToggleModeKey { get; set; } = Key.F8;

    public ModifierKeys ToggleModeModifiers { get; set; } = ModifierKeys.None;

    public Key TogglePlaybackKey { get; set; } = Key.K;

    public ModifierKeys TogglePlaybackModifiers { get; set; } = ModifierKeys.None;

    /// <summary>
    /// 主题：Dark / Light / System。默认 Dark。System 跟随 Windows 应用浅/深色。
    /// </summary>
    public string ThemeMode { get; set; } = "Dark";

    /// <summary>
    /// 界面语言：zh-CN / en-US。默认 zh-CN。
    /// </summary>
    public string Language { get; set; } = "zh-CN";

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
}
