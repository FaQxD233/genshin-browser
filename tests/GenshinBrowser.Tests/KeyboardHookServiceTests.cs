using System.Windows.Input;
using GenshinBrowser.Services;

namespace GenshinBrowser.Tests;

public sealed class KeyboardHookServiceTests
{
    [Fact]
    public void RegistrySupportsAdditionalHotkeysWithoutChangingHookBranches()
    {
        using var service = new KeyboardHookService();
        var invoked = false;

        service.RegisterOrUpdateHotkey("future-action", 0x41, ModifierKeys.Control, () => invoked = true);

        Assert.Equal(1, service.GetRegistrationCountForVirtualKey(0x41));
        Assert.True(service.UnregisterHotkey("future-action"));
        Assert.Equal(0, service.GetRegistrationCountForVirtualKey(0x41));
        Assert.False(invoked);
    }

    [Fact]
    public void BuiltInHotkeys_RejectConflictingVkAndModifiers()
    {
        using var service = new KeyboardHookService();
        var modeVk = service.ToggleModeVk;
        var modeMods = service.ToggleModeModifiers;

        // 试图把播放键改成与模式键相同：应被拒绝，播放键保持默认
        var originalPlaybackVk = service.TogglePlaybackVk;
        service.TogglePlaybackVk = modeVk;
        service.TogglePlaybackModifiers = modeMods;

        Assert.Equal(originalPlaybackVk, service.TogglePlaybackVk);
        Assert.Equal(1, service.GetRegistrationCountForVirtualKey(modeVk));
        Assert.Equal(1, service.GetRegistrationCountForVirtualKey(originalPlaybackVk));
    }

    [Fact]
    public void TrySetToggleModeHotkey_IsAtomicAgainstFinalConflict()
    {
        using var service = new KeyboardHookService();
        // Mode F8+None, Playback F8+Ctrl —— 录制 Mode 为 K+Ctrl 时，分步写入会在中间态失败
        Assert.True(service.TrySetTogglePlaybackHotkey(0x77, ModifierKeys.Control)); // F8+Ctrl
        Assert.True(service.TrySetToggleModeHotkey(0x4B, ModifierKeys.Control)); // K+Ctrl 最终不冲突

        Assert.Equal(0x4B, service.ToggleModeVk);
        Assert.Equal(ModifierKeys.Control, service.ToggleModeModifiers);
        Assert.Equal(0x77, service.TogglePlaybackVk);
        Assert.Equal(ModifierKeys.Control, service.TogglePlaybackModifiers);

        // 最终组合与播放键相同：原子拒绝，状态不变
        Assert.False(service.TrySetToggleModeHotkey(0x77, ModifierKeys.Control));
        Assert.Equal(0x4B, service.ToggleModeVk);
        Assert.Equal(ModifierKeys.Control, service.ToggleModeModifiers);
    }

    [Fact]
    public void RestoreDefaultHotkeyOrder_UncrossesSwappedModeAndPlayback()
    {
        // 模拟 RestoreDefaultHotkeys：Mode=K、Playback=F8 时直接设默认会双双失败；
        // 必须先 park 播放键到 F9，再 Mode=F8、Playback=K。
        using var service = new KeyboardHookService();
        const int vkF8 = 0x77;
        const int vkK = 0x4B;
        const int vkF9 = 0x78;

        Assert.True(service.TrySetTogglePlaybackHotkey(vkF9, ModifierKeys.None));
        Assert.True(service.TrySetToggleModeHotkey(vkK, ModifierKeys.None));
        Assert.True(service.TrySetTogglePlaybackHotkey(vkF8, ModifierKeys.None));
        // 现为 Mode=K, Playback=F8

        Assert.False(service.TrySetTogglePlaybackHotkey(vkK, ModifierKeys.None));
        Assert.False(service.TrySetToggleModeHotkey(vkF8, ModifierKeys.None));

        Assert.True(service.TrySetTogglePlaybackHotkey(vkF9, ModifierKeys.None));
        Assert.True(service.TrySetToggleModeHotkey(vkF8, ModifierKeys.None));
        Assert.True(service.TrySetTogglePlaybackHotkey(vkK, ModifierKeys.None));

        Assert.Equal(vkF8, service.ToggleModeVk);
        Assert.Equal(ModifierKeys.None, service.ToggleModeModifiers);
        Assert.Equal(vkK, service.TogglePlaybackVk);
        Assert.Equal(ModifierKeys.None, service.TogglePlaybackModifiers);
    }

    [Fact]
    public void RegisterOrUpdateHotkey_ThrowsOnConflictWithBuiltIn()
    {
        using var service = new KeyboardHookService();

        Assert.Throws<InvalidOperationException>(() =>
            service.RegisterOrUpdateHotkey(
                "custom",
                service.ToggleModeVk,
                service.ToggleModeModifiers,
                () => { }));
    }

    [Fact]
    public void Start_AfterDispose_ReturnsFalseWithoutInstallingHook()
    {
        var service = new KeyboardHookService();
        service.Dispose();

        var started = service.Start(out var errorCode);

        Assert.False(started);
        Assert.Equal(KeyboardHookService.ObjectDisposedErrorCode, errorCode);
    }

    [Fact]
    public void SuspendBuiltInHotkeys_DefaultsToFalseAndIsSettable()
    {
        using var service = new KeyboardHookService();
        Assert.False(service.SuspendBuiltInHotkeys);

        service.SuspendBuiltInHotkeys = true;
        Assert.True(service.SuspendBuiltInHotkeys);

        service.SuspendBuiltInHotkeys = false;
        Assert.False(service.SuspendBuiltInHotkeys);
    }
}
