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

    public double ControlWindowWidth { get; set; } = 640;

    public double ControlWindowHeight { get; set; } = 853;

    public double WindowOpacity { get; set; } = 1.0;

    public Key ToggleModeKey { get; set; } = Key.F8;

    public ModifierKeys ToggleModeModifiers { get; set; } = ModifierKeys.None;

    public Key TogglePlaybackKey { get; set; } = Key.K;

    public ModifierKeys TogglePlaybackModifiers { get; set; } = ModifierKeys.None;

    /// <summary>
    /// 主题：Dark / Light。默认 Dark。
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
}
