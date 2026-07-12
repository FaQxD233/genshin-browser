namespace GenshinBrowser.Models;

/// <summary>
/// 控制窗需要同步的浏览器状态切片。用于避免每次状态变化都全量刷新列表。
/// </summary>
[Flags]
public enum BrowserStateChangeKind
{
    None = 0,

    /// <summary>地址、前进/后退、加载中、当前页收藏态。</summary>
    Navigation = 1 << 0,

    /// <summary>状态栏文案与 Toast。</summary>
    Status = 1 << 1,

    /// <summary>浏览历史列表。</summary>
    History = 1 << 2,

    /// <summary>收藏列表与当前页收藏按钮。</summary>
    Favorites = 1 << 3,

    /// <summary>窗口模式、快捷键相关文案。</summary>
    Mode = 1 << 4,

    /// <summary>不透明度 / 缩放等外观显示（缩放另有 ZoomChanged，此处作兜底）。</summary>
    Appearance = 1 << 5,

    /// <summary>全量刷新（兼容旧行为）。</summary>
    All = Navigation | Status | History | Favorites | Mode | Appearance,
}
