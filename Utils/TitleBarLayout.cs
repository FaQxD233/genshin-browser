using GenshinBrowser.Models;

namespace GenshinBrowser.Utils;

/// <summary>
/// 主窗标题栏在当前模式与设置下怎么显示（状态表见 docs/plans/2026-11-12-remote-keys-touch-bar.md 第 7 节）。
/// </summary>
/// <param name="Pinned">常驻：不随鼠标进出隐藏。为 false 时浮窗按悬停显示、离开收回。</param>
/// <param name="ShowControls">标题换成倒退 / 播放暂停 / 快进三个按钮。</param>
/// <param name="NeedsClickLayer">主窗整窗穿透、标题栏却要能点：盖一层点击层接收点按。</param>
public readonly record struct TitleBarLayout(bool Pinned, bool ShowControls, bool NeedsClickLayer)
{
    public static TitleBarLayout For(WindowMode mode, bool autoHideInFloating, bool touchMode)
    {
        // 浏览模式：常驻，主窗不穿透
        if (mode != WindowMode.Fixed)
        {
            return new TitleBarLayout(Pinned: true, ShowControls: false, NeedsClickLayer: false);
        }

        // 触屏模式的浮窗：固定穿透，标题栏常驻、换成控制按钮，经点击层可点
        if (touchMode)
        {
            return new TitleBarLayout(Pinned: true, ShowControls: true, NeedsClickLayer: true);
        }

        // 键鼠的浮窗：常驻与否只看「隐藏标题栏」；开着穿透时标题栏和视频一起穿透
        return new TitleBarLayout(Pinned: !autoHideInFloating, ShowControls: false, NeedsClickLayer: false);
    }
}
