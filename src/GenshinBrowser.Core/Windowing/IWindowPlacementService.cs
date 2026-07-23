using GenshinBrowser.Browser;

namespace GenshinBrowser.Windowing;

public interface IWindowPlacementService
{
    bool CanPersistCurrentBounds { get; }

    WindowBounds Capture();

    void Restore(WindowBounds bounds);

    void Resize(double width, double height);

    void MoveToCorner(WindowCorner corner);
}
