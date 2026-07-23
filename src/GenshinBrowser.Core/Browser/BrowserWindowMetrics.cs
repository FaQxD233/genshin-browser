using GenshinBrowser.Models;

namespace GenshinBrowser.Browser;

public static class BrowserWindowMetrics
{
    public const double TitleBarHeight = 40;

    public static double ToOuterHeight(double contentHeight, WindowMode mode)
    {
        return contentHeight + (mode == WindowMode.Free ? TitleBarHeight : 0);
    }

    public static double ToContentHeight(double outerHeight, WindowMode mode)
    {
        double contentHeight = outerHeight - (mode == WindowMode.Free ? TitleBarHeight : 0);
        return Math.Max(360, contentHeight);
    }
}
