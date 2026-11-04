using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using System.Xml.Linq;
using GenshinBrowser.Models;
using GenshinBrowser.Services;
using GenshinBrowser.ViewModels;

namespace GenshinBrowser.Tests;

public sealed class HotkeySlotTests
{
    [Fact]
    public void AppSettings_GetAndSetHotkeyMapToTheirOwnFields()
    {
        var settings = new AppSettings();
        var keys = new[] { Key.A, Key.B, Key.C, Key.D, Key.E };
        foreach (var slot in HotkeySlots.All)
        {
            settings.SetHotkey(slot, new HotkeyBinding(keys[(int)slot], ModifierKeys.Shift));
        }

        Assert.Equal((Key.A, ModifierKeys.Shift), (settings.ToggleModeKey, settings.ToggleModeModifiers));
        Assert.Equal((Key.B, ModifierKeys.Shift), (settings.TogglePlaybackKey, settings.TogglePlaybackModifiers));
        Assert.Equal((Key.C, ModifierKeys.Shift), (settings.ToggleHideKey, settings.ToggleHideModifiers));
        Assert.Equal((Key.D, ModifierKeys.Shift), (settings.SeekBackwardKey, settings.SeekBackwardModifiers));
        Assert.Equal((Key.E, ModifierKeys.Shift), (settings.SeekForwardKey, settings.SeekForwardModifiers));
        Assert.All(HotkeySlots.All, slot => Assert.Equal(keys[(int)slot], settings.GetHotkey(slot).Key));
    }

    [Theory]
    [InlineData(Key.ImeProcessed, Key.None, Key.K, Key.None, Key.K)]
    [InlineData(Key.System, Key.F10, Key.None, Key.None, Key.F10)]
    [InlineData(Key.DeadCharProcessed, Key.None, Key.None, Key.OemQuotes, Key.OemQuotes)]
    [InlineData(Key.J, Key.None, Key.None, Key.None, Key.J)]
    public void ResolveRecordedKey_UsesTheRealKey(Key key, Key systemKey, Key imeProcessedKey, Key deadCharKey, Key expected)
    {
        Assert.Equal(expected, ControlWindowViewModel.ResolveRecordedKey(key, systemKey, imeProcessedKey, deadCharKey));
    }

    [Fact]
    public void SettingsLoad_ResetsHotkeyRecordedAsImeProcessed()
    {
        // 旧版在中文输入法下录制会存下 ImeProcessed（永远不触发）：加载时该组恢复默认
        using var directory = new TestDirectory();
        var path = directory.GetPath("settings.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new AppSettings
        {
            TogglePlaybackKey = Key.ImeProcessed,
            SeekBackwardKey = Key.J,
            HotkeyCorruptionRepairAttempted = true,
        }));

        using var service = new SettingsService(path);
        var settings = service.Load();

        Assert.Equal(Key.K, settings.TogglePlaybackKey);
        Assert.Equal(Key.J, settings.SeekBackwardKey);
    }

    [Fact]
    public void ToastKeysDerivedFromSlotNamesExist()
    {
        // 录制提示与设置成功提示按槽位名拼键（Toast.Record{槽位}、Toast.{槽位}Set）：槽位改名必须同步文案
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var keys = XDocument.Load(Path.Combine(RepositoryRoot(), "Resources/i18n/Strings.zh-CN.xaml")).Root!.Elements()
            .Select(element => (string?)element.Attribute(xaml + "Key"))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(HotkeySlots.All, slot =>
        {
            Assert.Contains($"Toast.Record{slot}", keys);
            Assert.Contains($"Toast.{slot}Set", keys);
        });
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
