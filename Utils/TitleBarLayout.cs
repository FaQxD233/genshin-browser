using GenshinBrowser.Models;

namespace GenshinBrowser.Utils;

/// <summary>
/// 主窗标题栏在当前模式与设置下怎么显示（状态表见 docs/plans/2026-11-14-floating-titlebar-toggles.md 第 1 节）。
/// </summary>
/// <param name="Pinned">常驻：不随鼠标进出隐藏。为 false 时浮窗按悬停显示、离开收回（穿透时窗口收不到鼠标，唤不出来）。</param>
/// <param name="ShowControls">标题换成倒退 / 播放暂停 / 快进三个按钮。</param>
/// <param name="NeedsClickLayer">主窗整窗穿透、标题栏却要能点：盖一层点击层接收点按。</param>
public readonly record struct TitleBarLayout(bool Pinned, bool ShowControls, bool NeedsClickLayer)
{
    public static TitleBarLayout For(
        WindowMode mode,
        bool clickThrough,
        bool titleBarControls,
        bool autoHideInFloating,
        bool touchMode)
    {
        // 浏览模式：常驻、显示标题，主窗不穿透
        if (mode != WindowMode.Fixed)
        {
            return new TitleBarLayout(Pinned: true, ShowControls: false, NeedsClickLayer: false);
        }

        // 浮窗：鼠标穿透、控制按钮、隐藏标题栏三项各管各的；触屏模式等于固定为 穿透 + 控制按钮 + 常驻
        var passThrough = clickThrough || touchMode;
        var showControls = titleBarControls || touchMode;
        var pinned = !autoHideInFloating || touchMode;

        // 穿透时标题栏和视频一起穿透，常驻又带控制按钮时才盖点击层让整条标题栏能点；
        // 隐藏的标题栏在穿透时唤不出来（按设计），用不着点击层
        return new TitleBarLayout(pinned, showControls, NeedsClickLayer: passThrough && showControls && pinned);
    }
}
