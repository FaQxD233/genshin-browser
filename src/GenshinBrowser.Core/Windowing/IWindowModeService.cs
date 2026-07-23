using GenshinBrowser.Models;

namespace GenshinBrowser.Windowing;

public interface IWindowModeService
{
    WindowMode CurrentMode { get; }

    void Apply(WindowMode mode);
}
