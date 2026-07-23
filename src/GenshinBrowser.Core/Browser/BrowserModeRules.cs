using GenshinBrowser.Models;

namespace GenshinBrowser.Browser;

public static class BrowserModeRules
{
    public static bool ShouldEnableAcceleratorKeys(WindowMode mode) => mode == WindowMode.Free;
}
