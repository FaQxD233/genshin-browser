using GenshinBrowser.Models;

namespace GenshinBrowser.Tests;

public sealed class BrowserModeTests
{
    [Fact]
    public void BrowserAcceleratorKeys_AreEnabledOnlyInBrowsingMode()
    {
        Assert.True(MainWindow.ShouldEnableBrowserAcceleratorKeys(WindowMode.Free));
        Assert.False(MainWindow.ShouldEnableBrowserAcceleratorKeys(WindowMode.Fixed));
    }
}
