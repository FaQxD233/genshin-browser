using System.Text.Json.Serialization;

namespace GenshinBrowser.Models;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

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

    public double ZoomFactor { get; set; } = 1.0;

    public int ToggleModeKey { get; set; } = VirtualKeyCodes.F8;

    public HotkeyModifiers ToggleModeModifiers { get; set; } = HotkeyModifiers.None;

    public int TogglePlaybackKey { get; set; } = VirtualKeyCodes.K;

    public HotkeyModifiers TogglePlaybackModifiers { get; set; } = HotkeyModifiers.None;

    public string ThemeMode { get; set; } = "Dark";

    public string Language { get; set; } = "zh-CN";

    public bool HasSeenFloatingModeHint { get; set; }

    public DateTime LastWebView2CacheCheckUtc { get; set; } = DateTime.MinValue;

    [JsonIgnore]
    public HotkeyGesture ToggleModeHotkey => new(ToggleModeKey, ToggleModeModifiers);

    [JsonIgnore]
    public HotkeyGesture TogglePlaybackHotkey => new(TogglePlaybackKey, TogglePlaybackModifiers);
}
