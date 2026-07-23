namespace GenshinBrowser.Browser;

public readonly record struct WindowBounds(double Left, double Top, double Width, double Height)
{
    public bool IsValid =>
        double.IsFinite(Left) &&
        double.IsFinite(Top) &&
        double.IsFinite(Width) &&
        double.IsFinite(Height) &&
        Width > 0 &&
        Height > 0;
}
