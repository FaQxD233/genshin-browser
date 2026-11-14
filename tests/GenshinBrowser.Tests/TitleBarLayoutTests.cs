using GenshinBrowser.Models;
using GenshinBrowser.Utils;

namespace GenshinBrowser.Tests;

public sealed class TitleBarLayoutTests
{
    [Theory]
    // 参数顺序：模式、鼠标穿透、控制按钮、隐藏标题栏、触屏模式 → 常驻、换成控制按钮、点击层
    // 浏览模式：始终常驻、显示标题，开关和触屏模式都不管
    [InlineData(WindowMode.Free, false, false, true, false, true, false, false)]
    [InlineData(WindowMode.Free, true, true, false, false, true, false, false)]
    [InlineData(WindowMode.Free, true, true, true, true, true, false, false)]
    // 键鼠浮窗、不穿透：常驻与否只看「隐藏标题栏」，控制按钮直接点，不要点击层
    [InlineData(WindowMode.Fixed, false, false, true, false, false, false, false)]
    [InlineData(WindowMode.Fixed, false, false, false, false, true, false, false)]
    [InlineData(WindowMode.Fixed, false, true, true, false, false, true, false)]
    [InlineData(WindowMode.Fixed, false, true, false, false, true, true, false)]
    // 键鼠浮窗、穿透：隐藏的标题栏唤不出来（按设计，开着控制按钮也一样）；常驻的标题栏不带控制按钮时和视频一起穿透
    [InlineData(WindowMode.Fixed, true, false, true, false, false, false, false)]
    [InlineData(WindowMode.Fixed, true, false, false, false, true, false, false)]
    [InlineData(WindowMode.Fixed, true, true, true, false, false, true, false)]
    // 键鼠浮窗、穿透 + 控制按钮 + 常驻：经点击层整条标题栏可点
    [InlineData(WindowMode.Fixed, true, true, false, false, true, true, true)]
    // 触屏模式的浮窗：常驻、换成控制按钮、要点击层，不管三个键鼠开关
    [InlineData(WindowMode.Fixed, false, false, true, true, true, true, true)]
    [InlineData(WindowMode.Fixed, false, false, false, true, true, true, true)]
    [InlineData(WindowMode.Fixed, true, true, true, true, true, true, true)]
    public void For_FollowsStateTable(
        WindowMode mode,
        bool clickThrough,
        bool titleBarControls,
        bool autoHide,
        bool touchMode,
        bool pinned,
        bool showControls,
        bool needsClickLayer)
    {
        Assert.Equal(
            new TitleBarLayout(pinned, showControls, needsClickLayer),
            TitleBarLayout.For(mode, clickThrough, titleBarControls, autoHide, touchMode));
    }
}
