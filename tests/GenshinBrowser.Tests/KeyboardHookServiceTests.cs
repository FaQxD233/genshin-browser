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
}
