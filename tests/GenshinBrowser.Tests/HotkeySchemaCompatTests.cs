using System.IO;
using System.Text.Json;
using System.Windows.Input;
using GenshinBrowser.Models;
using GenshinBrowser.Services;

namespace GenshinBrowser.Tests;

/// <summary>
/// 验证 WPF 版与 WinUI 版共用 settings.json 时的热键 schema 兼容：
/// WinUI 侧以 Win32 虚拟键码（schema 2）落盘，WPF 侧加载时须按 VK→Key 纠正。
/// </summary>
public sealed class HotkeySchemaCompatTests
{
    [Fact]
    public void Load_ConvertsWinUiVirtualKeySchemaToWpfKey()
    {
        using var directory = new TestDirectory();
        var settingsPath = directory.GetPath("settings.json");
        // WinUI schema 2：存的是 Win32 虚拟键码（VK_F8=0x77=119，VK_K=0x4B=75）。
        // System.Text.Json 会把 119/75 先填进 Key 枚举槽位，须由 SettingsService 按 VK→Key 纠正。
        File.WriteAllText(settingsPath,
            """{"SchemaVersion":2,"ToggleModeKey":119,"ToggleModeModifiers":0,"TogglePlaybackKey":75,"TogglePlaybackModifiers":0}""");

        using var settingsService = new SettingsService(settingsPath);
        var settings = settingsService.Load();

        Assert.Equal(Key.F8, settings.ToggleModeKey);
        Assert.Equal(Key.K, settings.TogglePlaybackKey);
        // 加载后归一为 WPF schema，避免二次迁移。
        Assert.Equal(1, settings.SchemaVersion);
    }

    [Fact]
    public void Load_RepairsKnownVirtualKeyCorruption()
    {
        using var directory = new TestDirectory();
        var settingsPath = directory.GetPath("settings.json");
        // VK 被旧 WPF 当 Key 枚举 round-trip 后的典型损坏：模式=RightCtrl、播放=NumPad1、均无修饰键。
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new AppSettings
        {
            SchemaVersion = 1,
            ToggleModeKey = Key.RightCtrl,
            ToggleModeModifiers = ModifierKeys.None,
            TogglePlaybackKey = Key.NumPad1,
            TogglePlaybackModifiers = ModifierKeys.None,
        }));

        using var settingsService = new SettingsService(settingsPath);
        var settings = settingsService.Load();

        Assert.Equal(Key.F8, settings.ToggleModeKey);
        Assert.Equal(Key.K, settings.TogglePlaybackKey);
    }
}
