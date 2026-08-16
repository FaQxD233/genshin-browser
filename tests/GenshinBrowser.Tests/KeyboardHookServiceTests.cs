using System.Diagnostics;
using System.Reflection;
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

    [Fact]
    public void NonGameProcessNames_UsesRealProcessNames()
    {
        var names = GetNonGameProcessNames();

        // 必须是 Process.ProcessName 的真实取值，否则浮窗模式下热键会在这些软件中误触发
        Assert.Contains("qbittorrent", names);
        Assert.Contains("idman", names);
        Assert.Contains("rider64", names);
        Assert.Contains("windowsterminal", names);
        Assert.Contains("potplayermini64", names);
        Assert.Contains("ms-teams", names);
        // 微信 4.x 主进程为 Weixin、小程序宿主为 WeChatAppEx，旧版才是 wechat
        Assert.Contains("wechat", names);
        Assert.Contains("weixin", names);
        Assert.Contains("wechatappex", names);
        // AI IDE：Trae / Antigravity / ZCode
        Assert.Contains("trae", names);
        Assert.Contains("antigravity", names);
        Assert.Contains("zcode", names);

        // 曾用/常见的错误别名不应存在（HashSet 精确匹配，别名永远命中不了真实进程名）
        Assert.DoesNotContain("qbit", names);
        Assert.DoesNotContain("idm", names);
        Assert.DoesNotContain("wt", names);
        Assert.DoesNotContain("rider", names);
    }

    [Fact]
    public void GetCachedForegroundProcessName_LiveProcessReturnsActualName()
    {
        using var service = new KeyboardHookService();

        var name = InvokeGetCachedForegroundProcessName(service, (uint)Environment.ProcessId);

        Assert.Equal(Process.GetProcessById(Environment.ProcessId).ProcessName, name);
    }

    [Fact]
    public void GetCachedForegroundProcessName_ExitedProcessReturnsNullWithoutThrowing()
    {
        // 前台窗口进程刚退出：无论退出发生在 GetProcessById 之前（ArgumentException）
        // 还是读取 ProcessName 之前（InvalidOperationException），都应被吞掉并返回 null，
        // 保持「无法确认进程名时视为游戏」的语义，而不是让异常漏进钩子回调。
        using var service = new KeyboardHookService();
        using var exited = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { UseShellExecute = false })!;
        exited.WaitForExit(5000);

        var name = InvokeGetCachedForegroundProcessName(service, (uint)exited.Id);

        Assert.Null(name);
    }

    private static HashSet<string> GetNonGameProcessNames()
    {
        var field = typeof(KeyboardHookService).GetField(
            "NonGameProcessNames", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return Assert.IsType<HashSet<string>>(field!.GetValue(null));
    }

    [Fact]
    public void NewBuiltInHotkeys_AreRegisteredByDefaultAndConflictChecked()
    {
        using var service = new KeyboardHookService();
        const int vkF7 = 0x76;
        const int vkSemicolon = 0xBA; // ';'
        const int vkQuote = 0xDE; // '\''

        // 默认注册占用三个键，各一条（与其余内置键互斥）
        Assert.Equal(1, service.GetRegistrationCountForVirtualKey(vkF7));
        Assert.Equal(1, service.GetRegistrationCountForVirtualKey(vkSemicolon));
        Assert.Equal(1, service.GetRegistrationCountForVirtualKey(vkQuote));

        // 与任一内置默认冲突的赋值被拒绝
        Assert.False(service.TrySetTogglePlaybackHotkey(vkF7, ModifierKeys.None));
        Assert.False(service.TrySetToggleModeHotkey(vkSemicolon, ModifierKeys.None));
        Assert.False(service.TrySetSeekBackwardHotkey(vkQuote, ModifierKeys.None));

        // 空闲键可正常更新并迁移注册
        const int vkF6 = 0x75;
        Assert.True(service.TrySetToggleHideHotkey(vkF6, ModifierKeys.None));
        Assert.Equal(1, service.GetRegistrationCountForVirtualKey(vkF6));
        Assert.Equal(0, service.GetRegistrationCountForVirtualKey(vkF7));

        // 同值重复写入幂等
        Assert.True(service.TrySetToggleHideHotkey(vkF6, ModifierKeys.None));
        Assert.Equal(1, service.GetRegistrationCountForVirtualKey(vkF6));
    }

    [Fact]
    public void Dispose_ReleasesAllBuiltInRegistrations()
    {
        var service = new KeyboardHookService();
        service.Dispose();

        Assert.False(service.TrySetToggleHideHotkey(0x75, ModifierKeys.None));
        Assert.False(service.TrySetSeekBackwardHotkey(0x75, ModifierKeys.None));
        Assert.False(service.TrySetSeekForwardHotkey(0x75, ModifierKeys.None));
    }


    [Fact]
    public void TrySetBuiltInHotkeys_AppliesCrossOccupiedConfigAtomically()
    {
        // 交叉占用配置（Mode=K、Playback=F8 互换默认）：逐个 TrySet 会因旧注册仍持默认被
        // 整批拒绝；批量接口不与旧状态比较，应原子落位
        using var service = new KeyboardHookService();
        const int vkF8 = 0x77;
        const int vkK = 0x4B;
        const int vkF7 = 0x76;
        const int vkSemicolon = 0xBA;
        const int vkQuote = 0xDE;

        Assert.False(service.TrySetToggleModeHotkey(vkK, ModifierKeys.None), "单键设置应被旧默认 Playback=K 拒绝（佐证交叉占用问题）");

        Assert.True(service.TrySetBuiltInHotkeys(
            vkK, ModifierKeys.None,
            vkF8, ModifierKeys.None,
            vkF7, ModifierKeys.None,
            vkSemicolon, ModifierKeys.None,
            vkQuote, ModifierKeys.None));
        Assert.Equal(vkK, service.ToggleModeVk);
        Assert.Equal(vkF8, service.TogglePlaybackVk);
        Assert.Equal(1, service.GetRegistrationCountForVirtualKey(vkK));
        Assert.Equal(1, service.GetRegistrationCountForVirtualKey(vkF8));
        Assert.Equal(1, service.GetRegistrationCountForVirtualKey(vkF7));
        Assert.Equal(1, service.GetRegistrationCountForVirtualKey(vkSemicolon));
        Assert.Equal(1, service.GetRegistrationCountForVirtualKey(vkQuote));

        // 再换回默认同样成功
        Assert.True(service.TrySetBuiltInHotkeys(
            vkF8, ModifierKeys.None,
            vkK, ModifierKeys.None,
            vkF7, ModifierKeys.None,
            vkSemicolon, ModifierKeys.None,
            vkQuote, ModifierKeys.None));
        Assert.Equal(vkF8, service.ToggleModeVk);
    }

    [Fact]
    public void TrySetBuiltInHotkeys_RejectsInternalConflictAndInvalidInput()
    {
        using var service = new KeyboardHookService();
        const int vkF8 = 0x77;
        const int vkK = 0x4B;

        // 内部重复（两组同组合）拒绝且不改动任何状态
        Assert.False(service.TrySetBuiltInHotkeys(
            vkF8, ModifierKeys.None,
            vkF8, ModifierKeys.None,
            0x76, ModifierKeys.None,
            0xBA, ModifierKeys.None,
            0xDE, ModifierKeys.None));
        Assert.Equal(vkF8, service.ToggleModeVk);
        Assert.Equal(vkK, service.TogglePlaybackVk);

        // 同键不同修饰键不算冲突（合法）
        Assert.True(service.TrySetBuiltInHotkeys(
            vkF8, ModifierKeys.None,
            vkF8, ModifierKeys.Control,
            0x76, ModifierKeys.None,
            0xBA, ModifierKeys.None,
            0xDE, ModifierKeys.None));

        // 非法 VK 拒绝
        Assert.False(service.TrySetBuiltInHotkeys(
            0, ModifierKeys.None,
            vkK, ModifierKeys.None,
            0x76, ModifierKeys.None,
            0xBA, ModifierKeys.None,
            0xDE, ModifierKeys.None));
    }


    [Fact]
    public void HotkeyFormatter_DisplaysPrintableOemKeysAsCharacters()
    {
        // OEM 键显示 US 布局主字符而非枚举名（OemSemicolon/OemQuotes 等）
        Assert.Equal(";", HotkeyFormatter.GetKeyName(System.Windows.Input.Key.OemSemicolon));
        Assert.Equal("'", HotkeyFormatter.GetKeyName(System.Windows.Input.Key.OemQuotes));
        Assert.Equal("[", HotkeyFormatter.GetKeyName(System.Windows.Input.Key.OemOpenBrackets));
        Assert.Equal("]", HotkeyFormatter.GetKeyName(System.Windows.Input.Key.OemCloseBrackets));
        Assert.Equal("`", HotkeyFormatter.GetKeyName(System.Windows.Input.Key.OemTilde));
        Assert.Equal("Ctrl + ;", HotkeyFormatter.Format(
            System.Windows.Input.Key.OemSemicolon, System.Windows.Input.ModifierKeys.Control));
    }

    private static string? InvokeGetCachedForegroundProcessName(KeyboardHookService service, uint pid)
    {
        var method = typeof(KeyboardHookService).GetMethod(
            "GetCachedForegroundProcessName", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        // TargetInvocationException 不会被吞：若内部 catch 漏了异常类型，这里会直接测试失败
        return (string?)method!.Invoke(service, new object[] { pid });
    }
}
