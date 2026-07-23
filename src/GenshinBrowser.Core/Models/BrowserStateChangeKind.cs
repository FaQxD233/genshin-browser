namespace GenshinBrowser.Models;

[Flags]
public enum BrowserStateChangeKind
{
    None = 0,
    Navigation = 1 << 0,
    Status = 1 << 1,
    History = 1 << 2,
    Favorites = 1 << 3,
    Mode = 1 << 4,
    Opacity = 1 << 5,
    Zoom = 1 << 6,
    Hotkeys = 1 << 7,
    Title = 1 << 8,
    WindowBounds = 1 << 9,
    WindowSize = 1 << 10,
    All = Navigation | Status | History | Favorites | Mode | Opacity | Zoom | Hotkeys | Title | WindowBounds | WindowSize,
}
