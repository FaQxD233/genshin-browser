using GenshinBrowser.Browser;
using GenshinBrowser.Models;

namespace GenshinBrowser.Tests;

public sealed class BrowserModeTests
{
    [Fact]
    public void BrowserAcceleratorKeys_AreEnabledOnlyInBrowsingMode()
    {
        Assert.True(BrowserModeRules.ShouldEnableAcceleratorKeys(WindowMode.Free));
        Assert.False(BrowserModeRules.ShouldEnableAcceleratorKeys(WindowMode.Fixed));
    }
}
