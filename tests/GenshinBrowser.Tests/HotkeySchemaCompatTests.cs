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
        // HotkeyCorruptionRepairAttempted 未置位（旧配置首次加载）。
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
        // 修复后置位标志，后续加载不再尝试。
        Assert.True(settings.HotkeyCorruptionRepairAttempted);
    }

    [Fact]
    public void Load_DoesNotResetLegitimateHotkeyAfterRepairAttempted()
    {
        // BUG-2 回归：用户合法设置 RightCtrl + NumPad1 时，若 RepairAttempted 已为 true，
        // 不应被 RepairKnownHotkeyCorruption 重置为默认。
        using var directory = new TestDirectory();
        var settingsPath = directory.GetPath("settings.json");
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new AppSettings
        {
            SchemaVersion = 1,
            ToggleModeKey = Key.RightCtrl,
            ToggleModeModifiers = ModifierKeys.None,
            TogglePlaybackKey = Key.NumPad1,
            TogglePlaybackModifiers = ModifierKeys.None,
            HotkeyCorruptionRepairAttempted = true,
        }));

        using var settingsService = new SettingsService(settingsPath);
        var settings = settingsService.Load();

        Assert.Equal(Key.RightCtrl, settings.ToggleModeKey);
        Assert.Equal(Key.NumPad1, settings.TogglePlaybackKey);
    }

    [Fact]
    public void Load_ResolvesConflictsAcrossAllFiveHotkeySlots()
    {
        // 五组热键全部写成同一组合：Sanitize 应保留前位（模式），后位依次让位，
        // 最终五组 (Key, Modifiers) 互不重复。
        using var directory = new TestDirectory();
        var settingsPath = directory.GetPath("settings.json");
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new AppSettings
        {
            SchemaVersion = 1,
            ToggleModeKey = Key.F8,
            ToggleModeModifiers = ModifierKeys.None,
            TogglePlaybackKey = Key.F8,
            TogglePlaybackModifiers = ModifierKeys.None,
            ToggleHideKey = Key.F8,
            ToggleHideModifiers = ModifierKeys.None,
            SeekBackwardKey = Key.F8,
            SeekBackwardModifiers = ModifierKeys.None,
            SeekForwardKey = Key.F8,
            SeekForwardModifiers = ModifierKeys.None,
            HotkeyCorruptionRepairAttempted = true,
        }));

        using var settingsService = new SettingsService(settingsPath);
        var settings = settingsService.Load();

        var combos = new[]
        {
            (settings.ToggleModeKey, settings.ToggleModeModifiers),
            (settings.TogglePlaybackKey, settings.TogglePlaybackModifiers),
            (settings.ToggleHideKey, settings.ToggleHideModifiers),
            (settings.SeekBackwardKey, settings.SeekBackwardModifiers),
            (settings.SeekForwardKey, settings.SeekForwardModifiers),
        };
        Assert.Equal(combos.Length, combos.Distinct().Count());
        // 前位保留：模式键仍是 F8
        Assert.Equal(Key.F8, settings.ToggleModeKey);
    }
}
