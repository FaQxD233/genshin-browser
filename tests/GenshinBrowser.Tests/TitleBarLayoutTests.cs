using GenshinBrowser.Models;
using GenshinBrowser.Utils;

namespace GenshinBrowser.Tests;

public sealed class TitleBarLayoutTests
{
    [Theory]
    // 浏览模式：始终常驻、显示标题，触屏模式也一样
    [InlineData(WindowMode.Free, true, false, true, false, false)]
    [InlineData(WindowMode.Free, false, false, true, false, false)]
    [InlineData(WindowMode.Free, true, true, true, false, false)]
    // 键鼠的浮窗：常驻与否只看「隐藏标题栏」，不要点击层
    [InlineData(WindowMode.Fixed, true, false, false, false, false)]
    [InlineData(WindowMode.Fixed, false, false, true, false, false)]
    // 触屏模式的浮窗：常驻、换成控制按钮、要点击层，不管「隐藏标题栏」
    [InlineData(WindowMode.Fixed, true, true, true, true, true)]
    [InlineData(WindowMode.Fixed, false, true, true, true, true)]
    public void For_FollowsStateTable(
        WindowMode mode,
        bool autoHide,
        bool touchMode,
        bool pinned,
        bool showControls,
        bool needsClickLayer)
    {
        Assert.Equal(
            new TitleBarLayout(pinned, showControls, needsClickLayer),
            TitleBarLayout.For(mode, autoHide, touchMode));
    }
}
