using GenshinBrowser.Models;
using GenshinBrowser.Utils;

namespace GenshinBrowser.Tests;

public sealed class TitleBarLayoutTests
{
    [Theory]
    // 浏览模式：始终常驻、显示标题，无论浮窗设置
    [InlineData(WindowMode.Free, true, false, false, true, false, false)]
    [InlineData(WindowMode.Free, true, true, true, true, false, false)]
    [InlineData(WindowMode.Free, false, true, true, true, false, false)]
    // 浮窗不穿透：常驻与否只看「隐藏标题栏」；控制按钮开着也不显示
    [InlineData(WindowMode.Fixed, true, false, false, false, false, false)]
    [InlineData(WindowMode.Fixed, false, false, false, true, false, false)]
    [InlineData(WindowMode.Fixed, true, false, true, false, false, false)]
    [InlineData(WindowMode.Fixed, false, false, true, true, false, false)]
    // 浮窗穿透、控制按钮关：同上，标题栏和视频一起穿透（不要点击层）
    [InlineData(WindowMode.Fixed, true, true, false, false, false, false)]
    [InlineData(WindowMode.Fixed, false, true, false, true, false, false)]
    // 浮窗穿透、控制按钮开：常驻、换成控制按钮、要点击层，不管「隐藏标题栏」
    [InlineData(WindowMode.Fixed, true, true, true, true, true, true)]
    [InlineData(WindowMode.Fixed, false, true, true, true, true, true)]
    public void For_FollowsStateTable(
        WindowMode mode,
        bool autoHide,
        bool clickThrough,
        bool controls,
        bool pinned,
        bool showControls,
        bool needsClickLayer)
    {
        Assert.Equal(
            new TitleBarLayout(pinned, showControls, needsClickLayer),
            TitleBarLayout.For(mode, autoHide, clickThrough, controls));
    }
}
